using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.TokenMeter;

/// <summary>One detached, immutable measurement of an agent's context state.</summary>
public sealed record ContextMeterReading(
    long PressureTokens,
    long SystemTokens,
    long ToolsTokens,
    long MessageTokens,
    long? ProviderPressureTokens,
    long? ContextWindowTokens,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheWriteTokens)
{
    /// <summary>Occupancy of the context window by heuristic pressure, 0..100.</summary>
    public int OccupancyPercent => ContextWindowTokens is { } window and > 0
        ? (int)Math.Min(100, PressureTokens * 100 / window)
        : 0;
}

/// <summary>
/// Replay-aware token measurement (dsh token-meter): one fold over the durable log plus the
/// assembled request header. Heuristic figures use the shared chars/4 estimator; the provider
/// anchor is the newest usage's prompt size (uncached input + cache reads + writes); usage
/// totals sum every successful step sample.
/// </summary>
public sealed class TokenMeterService
{
    public const string ServiceKey = "tokenMeter";

    private readonly SystemPromptService _systemPrompt;

    public TokenMeterService(SystemPromptService systemPrompt) => _systemPrompt = systemPrompt;

    /// <summary>Default window when neither the host nor the newest request/context declares one.</summary>
    public long ContextWindowTokens { get; set; } = 65_536;

    /// <summary>Host-supplied lookup for the current route's real context window (model catalog).
    /// When set, it wins over historical request declarations, which may be stale.</summary>
    public Func<string?, string?, long?>? ModelWindowResolver { get; set; }

    public static TokenMeterService Mount(HarnessContext ctx)
    {
        var service = new TokenMeterService(ctx.Get<SystemPromptService>(SystemPromptService.ServiceKey));
        ctx.Provide(ServiceKey, service);
        return service;
    }

    public ContextMeterReading Measure(Agent.Agent agent)
        => Measure(agent, null, null);

    /// <summary>Same measurement with usage totals supplied by an incremental folder (skips
    /// the full event scan). <paramref name="declaredWindow"/> is the latest request/context
    /// declaration; the resolver (current route's catalog window) still wins when set.</summary>
    public ContextMeterReading Measure(Agent.Agent agent, (long Input, long Output, long CacheRead, long CacheWrite)? totals, long? declaredWindow)
    {
        var assembly = _systemPrompt.Assemble(agent, agent.Session.Header.Cwd);
        var systemText = SystemPromptService.RenderPrompt(assembly);
        var systemTokens = TokenEstimator.Estimate(systemText);
        var toolsTokens = TokenEstimator.EstimateToolSchemas(assembly.ToolSchemas);
        var messages = agent.Session.DeriveMessages();
        var messageTokens = TokenEstimator.EstimateMessages(messages);

        long? providerPressure = null;
        long? historicalWindow = null;
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        if (totals is { } t)
        {
            input = t.Input;
            output = t.Output;
            cacheRead = t.CacheRead;
            cacheWrite = t.CacheWrite;
            providerPressure = t.Input + t.CacheRead + t.CacheWrite;
        }
        else
        {
            foreach (var e in agent.Session.Events)
            {
                if (e.Type == SessionEventTypes.AssistantMessage)
                {
                    var usage = SessionEventRead.AssistantMessageOf(e).Usage;
                    if (usage is not null)
                    {
                        input += usage.InputTokens;
                        output += usage.OutputTokens;
                        cacheRead += usage.CacheReadTokens ?? 0;
                        cacheWrite += usage.CacheWriteTokens ?? 0;
                        providerPressure = usage.InputTokens + (usage.CacheReadTokens ?? 0) + (usage.CacheWriteTokens ?? 0);
                    }
                }
                else if (e.Type == SessionEventTypes.RequestContext)
                {
                    var payload = SessionJson.FromElement<SessionPayloads.RequestContextPayload>(e.Data);
                    historicalWindow = payload.ContextWindow; // latest declaration wins
                }
            }
        }

        var routeWindow = ModelWindowResolver?.Invoke(agent.Options.Provider, agent.Options.Model);
        var window = routeWindow is { } rw and > 0 ? rw
            : (declaredWindow ?? historicalWindow) is { } dw and > 0 ? dw
            : ContextWindowTokens;

        return new ContextMeterReading(
            PressureTokens: systemTokens + toolsTokens + messageTokens,
            SystemTokens: systemTokens,
            ToolsTokens: toolsTokens,
            MessageTokens: messageTokens,
            ProviderPressureTokens: providerPressure,
            ContextWindowTokens: window,
            TotalInputTokens: input,
            TotalOutputTokens: output,
            TotalCacheReadTokens: cacheRead,
            TotalCacheWriteTokens: cacheWrite);
    }
}
