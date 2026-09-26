using System.Globalization;
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

    /// <summary>Releases an idle agent after its session has been selected for deletion.</summary>
    public async Task RemoveAsync(string id)
    {
        var agent = Get(id);
        if (agent is null) return;
        if (agent.Status == AgentStatus.Running) throw new InvalidOperationException("Stop this agent before deleting its session.");
        await agent.DisposeAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_agents.GetValueOrDefault(id) == agent) _agents.Remove(id);
        }
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
    public LlmCallConfig DefaultSelection { get; set; } = new() { Provider = "", Model = "" };

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
        var inheritModel = options?.Provider is null || options.Provider == DefaultSelection.Provider;
        var effective = new AgentOptions(DefaultSelection.Provider, inheritModel ? DefaultSelection.Model : "")
            .OverriddenBy(options ?? new AgentOptions());
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

    /// <summary>
    /// The static identity header: always rendered first, in every workspace, and never
    /// user-editable — workspace-level configuration replaces only the instruction body
    /// below it, and editors must not surface this line as configurable text. It also carries
    /// today's date (<c>{{date}}</c> / <c>{{weekday}}</c>), which the model cannot know otherwise.
    /// </summary>
    public const string IdentityHeader =
        "You are Blazorly Harness, an agentic coding assistant powered by {{provider}} ({{model}}).\n"
        + "Today is {{weekday}}, {{date}} in this machine's local time zone.";

    /// <summary>The built-in instruction body; fallback when no workspace override applies.
    /// The text itself lives in <see cref="HarnessDefaultInstructions.Body"/>.</summary>
    public const string DefaultIdentityBody = HarnessDefaultInstructions.Body;

    /// <summary>
    /// Optional replacement for the configurable instruction body below <see cref="IdentityHeader"/>
    /// — the host's seam for workspace-level custom instructions. Null or empty falls back to
    /// <see cref="DefaultIdentityBody"/>. The section interpolates leniently: registered variables
    /// ({{provider}}, {{model}}, {{cwd}}, {{date}}, {{weekday}}, …) substitute in custom text too,
    /// while an unknown {{placeholder}} stays literal instead of failing the request.
    /// </summary>
    public Func<SystemPrompt.SystemPromptContext, string?>? IdentityOverride { get; set; }

    /// <summary>Registers the harness identity section and the prompt variables it interpolates
    /// (provider, model, cwd, and today's date as seen by the server).</summary>
    public IDisposable RegisterDefaultPrompt()
    {
        var identity = _systemPrompt.RegisterSection("harness:identity", -100,
            ctx => IdentityHeader + "\n\n"
                + (IdentityOverride?.Invoke(ctx) is { Length: > 0 } custom ? custom : DefaultIdentityBody),
            lenient: true);
        var provider = _systemPrompt.RegisterVariable("provider", ctx => ctx.Agent?.Options.Provider ?? DefaultSelection.Provider);
        var model = _systemPrompt.RegisterVariable("model", ctx => ctx.Agent?.Options.Model ?? DefaultSelection.Model);
        var cwd = _systemPrompt.RegisterVariable("cwd", ctx => ctx.Cwd ?? "(unspecified)");
        // Re-read per assembly, so a chat that runs past midnight sees the day roll over.
        // Invariant culture: English day names and ISO dates regardless of the server locale.
        var date = _systemPrompt.RegisterVariable("date", _ => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var weekday = _systemPrompt.RegisterVariable("weekday", _ => DateTime.Now.ToString("dddd", CultureInfo.InvariantCulture));
        return Disposable.Of(() =>
        {
            identity.Dispose();
            provider.Dispose();
            model.Dispose();
            cwd.Dispose();
            date.Dispose();
            weekday.Dispose();
        });
    }

    public async Task FlushAsync(Sessions.Session session, CancellationToken ct = default)
    {
        if (_sessions.Persistence is not null) await _sessions.Persistence.FlushAsync(session.Id, ct).ConfigureAwait(false);
    }
}
