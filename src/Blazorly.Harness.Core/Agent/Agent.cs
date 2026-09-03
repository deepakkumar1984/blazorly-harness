using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Agent;

public sealed record AgentOptions(string? Provider = null, string? Model = null, int? MaxTokens = null, string? ReasoningEffort = null)
{
    public AgentOptions OverriddenBy(AgentOptions other) => new(
        other.Provider ?? Provider,
        other.Model ?? Model,
        other.MaxTokens ?? MaxTokens,
        other.ReasoningEffort ?? ReasoningEffort);
}

public static class AgentStatus
{
    public const string Idle = "idle";
    public const string Running = "running";
}

public sealed record AgentCancelCause(string Kind, string? Reason = null)
{
    public static AgentCancelCause User() => new("user");
    public static AgentCancelCause Disposed() => new("disposed");
    public static AgentCancelCause Hook(string reason) => new("hook", reason);
}

/// <summary>Pre-step decision: reject spends no step; enter carries the authoritative batch.</summary>
public sealed record PreStepDecision
{
    public const string EnterKind = "enter";
    public const string RejectKind = "reject";

    public required string Kind { get; init; }
    public IReadOnlyList<Message> Messages { get; init; } = [];

    public static PreStepDecision Enter(IReadOnlyList<Message> messages) => new() { Kind = EnterKind, Messages = messages };
    public static PreStepDecision Reject() => new() { Kind = RejectKind };
}

/// <summary>Post-step decision: continue runs the next step; stop ends the turn blocked.</summary>
public sealed record PostStepDecision
{
    public const string ContinueKind = "continue";
    public const string StopKind = "stop";

    public required string Kind { get; init; }

    public static PostStepDecision Continue() => new() { Kind = ContinueKind };
    public static PostStepDecision Stop() => new() { Kind = StopKind };
}

public sealed record RequestErrorAction
{
    public const string RetryKind = "retry";
    public required string Kind { get; init; }

    /// <summary>True when the deciding listener already applied its own backoff delay.</summary>
    public bool BackoffHandled { get; init; }

    public static RequestErrorAction Retry(bool backoffHandled = false) => new() { Kind = RetryKind, BackoffHandled = backoffHandled };
}

public sealed record PreStepEvent(Agent Agent, int Turn, int Step, IReadOnlyList<Message> Messages, CancellationToken Ct);
public sealed record RequestEvent(Agent Agent, int Turn, int Step, LlmCallConfig Config, CancellationToken Ct);

public sealed record RequestErrorEvent(Agent Agent, int Turn, int Step, LlmFailure Failure, int Attempts, CancellationToken Ct);

public sealed record StatusEvent(Agent Agent, string Status);

public sealed record InboxMessageEvent(Agent Agent, Message Message, int? Turn = null);

public sealed record TurnStoppingEvent(Agent Agent, int Turn, CancellationToken Ct);

public sealed record PostStepEvent(Agent Agent, int Turn, int Step, CancellationToken Ct);

public sealed record SessionStartEvent(Agent Agent, string Source);

/// <summary>
/// A live agent: durable inbox + single-actor driver state machine. The session log is the
/// source of truth; the inbox is a projection; all interleaving happens at await points.
/// </summary>
public sealed class Agent : IAsyncDisposable
{
    private readonly HarnessContext _root;
    private readonly Llm.LlmRuntime _llm;
    private readonly Tools.ToolRuntime _tools;
    private readonly SystemPrompt.SystemPromptService _systemPrompt;

    private readonly object _phaseGate = new();
    private PhaseState _phase = new() { Kind = AgentStatus.Idle };
    private TaskCompletionSource _activityDone = CompletedTask();
    private AgentCancelCause? _cancelCause;
    private string? _retainedContextSnapshot;

    private sealed class PhaseState
    {
        public string Kind = AgentStatus.Idle;
        public CancellationTokenSource? Cts;
        public int Turn;
        public int Step;
        public bool WakeRequested;
    }

    public Agent(
        HarnessContext root,
        Llm.LlmRuntime llm,
        Tools.ToolRuntime tools,
        SystemPrompt.SystemPromptService systemPrompt,
        Sessions.Session session,
        AgentOptions options)
    {
        _root = root;
        _llm = llm;
        _tools = tools;
        _systemPrompt = systemPrompt;
        Session = session;
        Options = options;
        Scope = root.CreateScope(this);
        Ctx = Scope.Ctx;
        Inbox = new Inbox(session, e => _ = root.Events.EmitAsync("session/event", new Sessions.SessionEventNotification(session, e), this));
        Driver = new AgentDriver(this, root, llm, tools, systemPrompt);
    }

    public string Id => Session.Id;
    public Sessions.Session Session { get; }
    public AgentOptions Options { get; set; }
    public Inbox Inbox { get; }
    public object ScopeKey => this;
    public Scope Scope { get; }
    public HarnessContext Ctx { get; }
    public AgentDriver Driver { get; }

    public string Status
    {
        get { lock (_phaseGate) return _phase.Kind == AgentStatus.Running ? AgentStatus.Running : AgentStatus.Idle; }
    }

    public int RetryLimit { get; set; } = 5;

    private static TaskCompletionSource CompletedTask()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    // ---- public surface ----

    public void Followup(Message message) => Send(message, InboxTarget.NextTurn, wakeup: true);
    public void Steer(Message message) => Send(message, InboxTarget.NextStep, wakeup: true);
    public void Inject(Message message) => Send(message, InboxTarget.NextStep, wakeup: false);

    /// <summary>Delivers a message; a waking send starts the driver or latches for replay at convergence.</summary>
    public void Send(Message message, string target, bool wakeup)
    {
        bool wakingAfterAbort;
        lock (_phaseGate)
        {
            // A waking send after the running phase aborted re-routes to next-turn.
            wakingAfterAbort = wakeup
                && _phase.Kind != AgentStatus.Idle
                && _phase.Cts is { IsCancellationRequested: true };
        }
        Inbox.Insert(message, wakingAfterAbort ? InboxTarget.NextTurn : target);
        _ = _root.Events.EmitAsync("agent/inbox/inserted", new InboxMessageEvent(this, message), this);
        if (wakeup) WakeDriver(wakingAfterAbort);
    }

    private void WakeDriver(bool wakeAfterAbort)
    {
        TaskCompletionSource? toAwait = null;
        lock (_phaseGate)
        {
            if (_phase.Kind != AgentStatus.Idle)
            {
                if (_phase.Cts is { } cts && !cts.IsCancellationRequested)
                {
                    // A live running driver drains the queue itself.
                    return;
                }
                if (_cancelCause is { Kind: "disposed" }) return; // teardown never waits on model turns
                _phase.WakeRequested = true;
                return;
            }
            // Reserve the phase synchronously so a following cancel reaches the turn's token.
            _cancelCause = null;
            _retainedContextSnapshot = null;
            _phase = new PhaseState
            {
                Kind = AgentStatus.Running,
                Cts = new CancellationTokenSource(),
                Turn = _phase.Turn,
                Step = 0,
            };
            _activityDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            toAwait = _activityDone;
        }
        _ = _root.Events.EmitAsync("agent/status", new StatusEvent(this, AgentStatus.Running), this);
        _ = Task.Run(async () =>
        {
            try
            {
                await Driver.KickAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = _root.Events.EmitAsync("agent/error", new { agent = this, error = ex }, this);
            }
            finally
            {
                bool replayWake = false;
                lock (_phaseGate)
                {
                    var lastTurn = _phase.Turn;
                    var latched = _phase.WakeRequested;
                    _phase = new PhaseState { Kind = AgentStatus.Idle, Turn = lastTurn };
                    replayWake = latched;
                }
                _ = _root.Events.EmitAsync("agent/status", new StatusEvent(this, AgentStatus.Idle), this);
                toAwait!.TrySetResult();
                if (replayWake && Inbox.HasPending) WakeDriver(wakeAfterAbort: false);
            }
        });
    }

    public void Cancel(AgentCancelCause cause, bool keepInbox = false)
    {
        if (!keepInbox) Inbox.Clear();
        lock (_phaseGate)
        {
            _cancelCause ??= cause;
            _phase.Cts?.Cancel();
        }
    }

    internal CancellationToken DriverToken
    {
        get { lock (_phaseGate) return _phase.Cts?.Token ?? CancellationToken.None; }
    }

    internal AgentCancelCause? CancelCause
    {
        get { lock (_phaseGate) return _cancelCause; }
    }

    internal (int Turn, int Step) AdvanceTurn(int turn)
    {
        lock (_phaseGate)
        {
            _phase.Turn = turn;
            _phase.Step = 0;
            return (_phase.Turn, _phase.Step);
        }
    }

    internal void SetStep(int step)
    {
        lock (_phaseGate) _phase.Step = step;
    }

    /// <summary>The last rendered runtime-context snapshot; cleared by compaction so it re-appends.</summary>
    public string? RetainedContextSnapshot
    {
        get => _retainedContextSnapshot;
        set => _retainedContextSnapshot = value;
    }

    public async Task WhenIdleAsync()
    {
        while (true)
        {
            var activity = _activityDone;
            await activity.Task.ConfigureAwait(false);
            if (ReferenceEquals(activity, Volatile.Read(ref _activityDone))) return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel(AgentCancelCause.Disposed());
        await WhenIdleAsync().ConfigureAwait(false);
        await Scope.DisposeAsync().ConfigureAwait(false);
        _ = _root.Events.EmitAsync("agent/disposed", this, this);
    }
}
