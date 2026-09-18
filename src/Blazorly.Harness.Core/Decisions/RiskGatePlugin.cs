using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Decisions;

public sealed record RiskGateOptions
{
    /// <summary>P(risky) at or above which an allowed call is parked for human approval.</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>
    /// Tool-call kinds worth asking about. Reads, searches and fetches cannot destroy anything,
    /// so they never reach the model — that is what keeps this off the hot path of a normal turn.
    /// </summary>
    public IReadOnlyCollection<string> Kinds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal) { "execute", "delete" };

    /// <summary>Longest rendered argument blob sent as state.</summary>
    public int MaxArgChars { get; init; } = 2_000;
}

/// <summary>
/// Risk-aware approvals: asks a decision model whether an allowed tool call could cause
/// irreversible harm, and parks it for human approval when the answer is confident enough.
///
/// <b>The escalate-only invariant.</b> This middleware can turn <c>allow</c> into <c>ask</c>. It
/// can never turn <c>ask</c> into <c>allow</c>, never touches <c>deny</c>, and returns null
/// opinion on any failure. The deterministic policy stays authoritative; a calibrated model that
/// is wrong, slow or offline can only ever make the harness more careful, never less. That is what
/// makes it safe to leave enabled, and it is asserted in the test suite.
///
/// Also skipped entirely when no front end can answer an approval (headless <c>blazorly run</c>):
/// there, an escalation would fail closed to a denial and break the run, so the gate stands down
/// and the session's own permission preset remains the only authority.
/// </summary>
public sealed class RiskGatePlugin(DecisionService decisions, RiskGateOptions? options = null) : HarnessPlugin
{
    public const string RiskKey = "risky";

    public override string Name => "risk-gate";

    /// <summary>
    /// Injects only <c>toolPolicy</c>, and that purely for waterfall ordering: registering after
    /// it puts this middleware inside, so an ask-every-tool session short-circuits before the gate
    /// is ever consulted. The <see cref="DecisionService"/> arrives by constructor, deliberately —
    /// declaring it here would mean a <c>disabledPlugins: ["decisions"]</c> entry left this plugin
    /// waiting on a service that never mounts, which fails the whole boot with PLUGIN_DEADLOCK.
    /// </summary>
    public override string[] Inject { get; } = [ToolPolicyService.ServiceKey];

    public RiskGateOptions Options { get; } = options ?? new RiskGateOptions();

    private DecisionService Decisions { get; } = decisions;

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        // Registered after toolPolicy, so this runs inside it: an ask-every-tool session has
        // already short-circuited to Asked and never reaches us.
        var subscription = ctx.OnWaterfall<ToolExecution, PreToolDecision, PreToolDecision>(
            "tools/pre-execute",
            async (execution, decision, next, ct) =>
            {
                if (decision.Kind != PreToolDecision.Allow) return await next(decision).ConfigureAwait(false);
                var escalated = await EvaluateAsync(ctx, execution, ct).ConfigureAwait(false);
                return await next(escalated ?? decision).ConfigureAwait(false);
            });
        ctx.Effect(subscription.Dispose);
        return Task.CompletedTask;
    }

    /// <summary>The escalated decision, or null to leave the incoming one untouched.</summary>
    public async Task<PreToolDecision?> EvaluateAsync(
        HarnessContext ctx, ToolExecution execution, CancellationToken ct)
    {
        if (!Decisions.IsEnabled(DecisionSeams.RiskGate)) return null;

        var input = execution.Input;
        // Cheap deterministic pre-filter: only kinds that can actually destroy something are asked about.
        var kind = SafeKind(execution);
        if (kind is null || !Options.Kinds.Contains(kind)) return null;

        // No answerer means no middle ground: an escalation would fail closed to a denial and
        // break a headless run, so the session's own preset stays the only authority.
        if (ctx.TryGet<ApprovalService>(ApprovalService.ServiceKey) is not { CanAsk: true }) return null;

        var session = input.Agent?.Session;
        var state = BuildState(execution, kind, session);
        var question = new Noul(RiskKey,
            "Could this action destroy data, leak secrets, or cause other harm that cannot be undone "
            + "by re-running or reverting? Answer yes only for irreversible or outward-facing effects.");

        var result = await Decisions.AskAsync(DecisionSeams.RiskGate, state, question, session, ct).ConfigureAwait(false);
        if (result?.ProbabilityOf(RiskKey) is not { } risk || risk < Options.Threshold) return null;

        return PreToolDecision.Asked(
            $"a risk model scored this {kind} call at {risk:P0} (threshold {Options.Threshold:P0})");
    }

    public DecisionState BuildState(ToolExecution execution, string kind, Session? session)
    {
        var input = execution.Input;
        var state = new DecisionState()
            .Add("tool", input.Name, 64)
            .Add("kind", kind, 32)
            .Add("arguments", Raw(input.Arguments), Options.MaxArgChars)
            .Add("cwd", session?.Header.Cwd ?? "", 260);

        if (session is not null)
        {
            state.Add("sandboxMode", session.LatestSandboxMode() ?? "", 64);
            state.Add("recentTools", RecentTools(session), 400);
        }
        return state;
    }

    /// <summary>The last few tool names, so a risky-looking call in a benign sequence reads correctly.</summary>
    private static string RecentTools(Session session)
    {
        var names = new List<string>();
        var events = session.Events;
        for (var i = events.Count - 1; i >= 0 && names.Count < 8; i--)
        {
            if (events[i].Type != SessionEventTypes.ToolCall) continue;
            try { names.Add(SessionEventRead.ToolCallOf(events[i]).Name); }
            catch { /* a malformed event must not break the gate */ }
        }
        names.Reverse();
        return string.Join(" → ", names);
    }

    private static string? SafeKind(ToolExecution execution)
    {
        try { return execution.Definition.PresentCall(execution.Input.Arguments)?.Kind; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null; // unrenderable arguments: no opinion, deterministic policy stands
        }
    }

    private static string Raw(JsonElement arguments)
    {
        try { return arguments.ValueKind == JsonValueKind.Undefined ? "" : arguments.GetRawText(); }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException) { return ""; }
    }
}
