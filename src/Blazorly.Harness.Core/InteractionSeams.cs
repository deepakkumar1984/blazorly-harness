using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core;

public enum ApprovalOutcome
{
    AllowedOnce,
    Rejected,
    Cancelled,
    Unavailable,
}

public sealed record ApprovalRequest(Agent.Agent Agent, string ToolName, string CallId, string? Reason);

/// <summary>
/// ctx.approval — a one-shot permission decision seam. Absence fails closed; the UI
/// front end provides the active answerer.
/// </summary>
public sealed class ApprovalService
{
    public const string ServiceKey = "approval";

    public delegate Task<ApprovalOutcome> Answerer(ApprovalRequest request, CancellationToken ct);

    private readonly HarnessContext _ctx;
    private readonly List<Answerer> _answerers = [];
    private readonly object _gate = new();

    public ApprovalService(HarnessContext ctx) => _ctx = ctx;

    /// <summary>
    /// True when some front end is currently able to answer. A caller that would escalate to
    /// <c>Ask</c> must check this first: with no answerer the seam fails closed to
    /// <see cref="ApprovalOutcome.Unavailable"/>, which the tool pipeline turns into a denial —
    /// so an advisory escalation would silently break headless runs.
    /// </summary>
    public bool CanAsk
    {
        get { lock (_gate) return _answerers.Count > 0; }
    }

    public static ApprovalService Mount(HarnessContext ctx)
    {
        var service = new ApprovalService(ctx);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    /// <summary>Replaces the answerer stack with a single front end (the web UI park).</summary>
    public IDisposable SetAnswerer(Answerer answerer)
    {
        lock (_gate)
        {
            _answerers.Clear();
            _answerers.Add(answerer);
        }
        return _ctx.Effect(() => { lock (_gate) _answerers.Remove(answerer); });
    }

    /// <summary>
    /// Pushes a scoped answerer on top: it answers only requests it owns (returning
    /// Unavailable otherwise, which falls through to the next answerer). Automation bridges
    /// like ACP scope themselves to their own sessions this way.
    /// </summary>
    public IDisposable PushAnswerer(Answerer answerer)
    {
        lock (_gate) _answerers.Add(answerer);
        return _ctx.Effect(() => { lock (_gate) _answerers.Remove(answerer); });
    }

    public async Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Answerer[] stack;
        lock (_gate) stack = [.. _answerers];
        foreach (var answerer in stack.Reverse())
        {
            try
            {
                var outcome = await answerer(request, ct).ConfigureAwait(false);
                if (outcome != ApprovalOutcome.Unavailable) return outcome;
            }
            catch (OperationCanceledException)
            {
                return ApprovalOutcome.Cancelled;
            }
        }
        return ApprovalOutcome.Unavailable;
    }
}

public sealed record AskOption(string Label, string? Description = null);

public sealed record AskQuestion(string Id, string Question, string? Header = null, IReadOnlyList<AskOption>? Options = null, bool MultiSelect = false);

public sealed record AskAnswer(string Id, string Text);

/// <summary>
/// Where an ask originates. Front ends scope their cards to this session id — an ask parked
/// without one surfaces in every open chat, which reads as the app talking to the wrong person.
/// </summary>
public sealed record AskOrigin(string SessionId)
{
    /// <summary>For asks with no owning session (direct seam calls, tests).</summary>
    public static AskOrigin Global { get; } = new("(global)");
}

/// <summary>
/// Long-form document attached to an ask so the human can review what they are deciding on —
/// a plan under approval, for instance. The question card previews it; the UI offers the full
/// text in its own reading surface.
/// </summary>
public sealed record AskDocument(string Title, string Markdown);

/// <summary>One human round-trip: the questions, who is asking, and what they need read first.</summary>
public sealed record AskRequest(IReadOnlyList<AskQuestion> Questions, AskOrigin Origin, AskDocument? Document = null);

/// <summary>ctx.userQuestions — the human question/answer seam; the UI provides the provider.</summary>
public sealed class UserQuestionsService
{
    public const string ServiceKey = "userQuestions";

    public delegate Task<IReadOnlyList<AskAnswer>> Provider(AskRequest request, CancellationToken ct);

    private readonly HarnessContext _ctx;
    private Provider? _provider;

    public UserQuestionsService(HarnessContext ctx) => _ctx = ctx;

    public static UserQuestionsService Mount(HarnessContext ctx)
    {
        var service = new UserQuestionsService(ctx);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    public IDisposable SetProvider(Provider provider)
    {
        _provider = provider;
        return _ctx.Effect(() => _provider = null);
    }

    /// <summary>Asks without session ownership; front ends that scope cards will not show it.</summary>
    public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct)
        => AskAsync(new AskRequest(questions, AskOrigin.Global), ct);

    public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, AskOrigin origin, CancellationToken ct)
        => AskAsync(new AskRequest(questions, origin), ct);

    /// <summary>Asks the human through the active provider; throws when no front end can answer.</summary>
    public async Task<IReadOnlyList<AskAnswer>> AskAsync(AskRequest request, CancellationToken ct)
    {
        var provider = _provider ?? throw new Kernel.HarnessException("NO_USER_QUESTIONS_PROVIDER", "no user-questions provider is mounted");
        return await provider(request, ct).ConfigureAwait(false);
    }
}
