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

    public static TokenMeterService Mount(HarnessContext ctx)
    {
        var service = new TokenMeterService(ctx.Get<SystemPromptService>(SystemPromptService.ServiceKey));
        ctx.Provide(ServiceKey, service);
        return service;
    }

    public ContextMeterReading Measure(Agent.Agent agent)
    {
        var assembly = _systemPrompt.Assemble(agent, agent.Session.Header.Cwd);
        var systemText = SystemPromptService.RenderPrompt(assembly);
        var systemTokens = TokenEstimator.Estimate(systemText);
        var toolsTokens = TokenEstimator.EstimateToolSchemas(assembly.ToolSchemas);
        var messages = agent.Session.DeriveMessages();
        var messageTokens = TokenEstimator.EstimateMessages(messages);

        long? providerPressure = null;
        long? declaredWindow = null;
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
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
                declaredWindow ??= payload.ContextWindow;
            }
        }

        return new ContextMeterReading(
            PressureTokens: systemTokens + toolsTokens + messageTokens,
            SystemTokens: systemTokens,
            ToolsTokens: toolsTokens,
            MessageTokens: messageTokens,
            ProviderPressureTokens: providerPressure,
            ContextWindowTokens: declaredWindow ?? ContextWindowTokens,
            TotalInputTokens: input,
            TotalOutputTokens: output,
            TotalCacheReadTokens: cacheRead,
            TotalCacheWriteTokens: cacheWrite);
    }
}
