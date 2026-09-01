using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Agent;

/// <summary>ctx.agents — live agent handles and the create/resume factory.</summary>
public sealed class AgentRuntime
{
    public const string ServiceKey = "agents";

    private readonly HarnessContext _ctx;
    private readonly Dictionary<string, Agent> _agents = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AgentRuntime(HarnessContext ctx) => _ctx = ctx;

    public static AgentRuntime Mount(HarnessContext ctx)
    {
        var runtime = new AgentRuntime(ctx);
        ctx.Provide(ServiceKey, runtime);
        return runtime;
    }

    public void Publish(Agent agent)
    {
        lock (_gate) _agents[agent.Id] = agent;
        _ = _ctx.Events.EmitAsync("agent/created", agent, agent);
    }

    internal void Detach(Agent agent)
    {
        lock (_gate) _agents.Remove(agent.Id);
    }

    public Agent? Get(string id)
    {
        lock (_gate) return _agents.GetValueOrDefault(id);
    }

    public IReadOnlyList<Agent> LiveAgents()
    {
        lock (_gate) return [.. _agents.Values];
    }
}

/// <summary>
/// The loop plugin: composes agents from the session store + registries, declares the
/// prompt variables, and owns default model selection.
/// </summary>
public sealed class AgentLoopService
{
    public const string ServiceKey = "agentLoop";

    private readonly HarnessContext _ctx;
    private readonly AgentRuntime _agents;
    private readonly Sessions.SessionStore _sessions;
    private readonly LlmRuntime _llm;
    private readonly Tools.ToolRuntime _tools;
    private readonly SystemPrompt.SystemPromptService _systemPrompt;

    public AgentLoopService(HarnessContext ctx, AgentRuntime agents, Sessions.SessionStore sessions, LlmRuntime llm, Tools.ToolRuntime tools, SystemPrompt.SystemPromptService systemPrompt)
    {
        _ctx = ctx;
        _agents = agents;
        _sessions = sessions;
        _llm = llm;
        _tools = tools;
        _systemPrompt = systemPrompt;
    }

    public static AgentLoopService Mount(HarnessContext ctx)
    {
        var loop = new AgentLoopService(
            ctx,
            ctx.Get<AgentRuntime>(AgentRuntime.ServiceKey),
            ctx.Get<Sessions.SessionStore>(Sessions.SessionStore.ServiceKey),
            ctx.Get<LlmRuntime>(LlmRuntime.ServiceKey),
            ctx.Get<Tools.ToolRuntime>(Tools.ToolRuntime.ServiceKey),
            ctx.Get<SystemPrompt.SystemPromptService>(SystemPrompt.SystemPromptService.ServiceKey));
        ctx.Provide(ServiceKey, loop);
        return loop;
    }

    /// <summary>Default model selection when AgentOptions omit provider/model.</summary>
    public LlmCallConfig DefaultSelection { get; set; } = new() { Provider = "replay", Model = "demo" };

    public int MaxParallelToolCalls { get; set; } = 10;

    /// <summary>Creates a fresh agent with its own session, scope, and inbox.</summary>
    public Agent Create(Sessions.SessionMeta? meta = null, AgentOptions? options = null, string? sessionId = null, Action<Agent>? setup = null)
    {
        var session = _sessions.Create(sessionId, meta);
        return CreateForSession(session, options, setup, source: "startup");
    }

    /// <summary>Resumes a persisted session as a live agent.</summary>
    public async Task<Agent> ResumeAsync(string sessionId, AgentOptions? options = null, Action<Agent>? setup = null, CancellationToken ct = default)
    {
        var session = await _sessions.OpenAsync(sessionId, ct).ConfigureAwait(false);
        return CreateForSession(session, options, setup, source: "resume");
    }

    private Agent CreateForSession(Sessions.Session session, AgentOptions? options, Action<Agent>? setup, string source)
    {
        var effective = (options ?? new AgentOptions()).OverriddenBy(new AgentOptions(DefaultSelection.Provider, DefaultSelection.Model, null));
        var agent = new Agent(_ctx, _llm, _tools, _systemPrompt, session, effective)
        {
            RetryLimit = RetryLimit,
        };
        agent.Driver.MaxParallelToolCalls = MaxParallelToolCalls;
        setup?.Invoke(agent);
        _agents.Publish(agent);
        _ = _ctx.Events.EmitAsync("agent/session-start", new SessionStartEvent(agent, source), agent);
        return agent;
    }

    public int RetryLimit { get; set; } = 5;

    /// <summary>Registers the harness identity section and the provider/model/cwd prompt variables.</summary>
    public IDisposable RegisterDefaultPrompt()
    {
        var identity = _systemPrompt.RegisterSection("harness:identity", -100, _ =>
            """
            You are Blazorly Harness, an agentic coding assistant powered by {{provider}} ({{model}}).

            You complete tasks with tools: read files before editing them, run commands to verify
            work, and keep going until the task is done. Report outcomes plainly; never claim work
            you did not verify. Prefer one tool call at a time for mutations; parallel calls are
            allowed for independent reads and searches.
            """);
        var provider = _systemPrompt.RegisterVariable("provider", ctx => ctx.Agent?.Options.Provider ?? DefaultSelection.Provider);
        var model = _systemPrompt.RegisterVariable("model", ctx => ctx.Agent?.Options.Model ?? DefaultSelection.Model);
        var cwd = _systemPrompt.RegisterVariable("cwd", ctx => ctx.Cwd ?? "(unspecified)");
        return Disposable.Of(() =>
        {
            identity.Dispose();
            provider.Dispose();
            model.Dispose();
            cwd.Dispose();
        });
    }

    public async Task FlushAsync(Sessions.Session session, CancellationToken ct = default)
    {
        if (_sessions.Persistence is not null) await _sessions.Persistence.FlushAsync(session.Id, ct).ConfigureAwait(false);
    }
}
