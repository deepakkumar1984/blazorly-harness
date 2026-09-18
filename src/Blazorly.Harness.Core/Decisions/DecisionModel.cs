using System.Security.Cryptography;
using System.Text;

namespace Blazorly.Harness.Core.Decisions;

/// <summary>
/// A yes/no question. The answer is a calibrated probability, not a boolean — the caller owns
/// the threshold, so the same model output can gate a destructive command at 0.3 and an
/// auto-plan at 0.6 without re-asking.
/// </summary>
public sealed record Noul(string Key, string Prompt) : DecisionQuestion(Key, Prompt);

/// <summary>A pick-one question. Options are the whole answer space; the model cannot invent one.</summary>
public sealed record Choice(string Key, string Prompt, IReadOnlyList<string> Options) : DecisionQuestion(Key, Prompt)
{
    /// <summary>System One caps a choice at 255 alternatives.</summary>
    public const int MaxOptions = 255;
}

/// <summary>An ordinal rating over a declared range (e.g. 0–10 risk, 0–100 complexity).</summary>
public sealed record Rating(string Key, string Prompt, int Min, int Max) : DecisionQuestion(Key, Prompt);

/// <summary>One typed question. Key is how the answer comes back; Prompt is the question text.</summary>
public abstract record DecisionQuestion(string Key, string Prompt);

/// <summary>
/// One answer. Exactly one of <see cref="Probability"/> / <see cref="Value"/> / <see cref="Score"/>
/// is meaningful, selected by <see cref="Kind"/>. Flat and JSON-serializable on purpose: this
/// record is what lands in the durable <c>decision/result</c> event, so a replay can see the
/// probability the gate actually used rather than only the branch it took.
/// </summary>
public sealed record DecisionAnswer(string Key, string Kind)
{
    public const string NoulKind = "noul";
    public const string ChoiceKind = "choice";
    public const string RatingKind = "rating";

    /// <summary>P(yes) for a noul; null for other kinds.</summary>
    public double? Probability { get; init; }

    /// <summary>The chosen option for a choice; null otherwise.</summary>
    public string? Value { get; init; }

    /// <summary>The full distribution for a choice, when the model reports one.</summary>
    public IReadOnlyDictionary<string, double>? Options { get; init; }

    /// <summary>The rating for a rating question; null otherwise.</summary>
    public double? Score { get; init; }

    /// <summary>Confidence in this answer, when the model reports one separately from the value.</summary>
    public double? Confidence { get; init; }
}

/// <summary>
/// The outcome of one decision call. <c>null</c> answers are never returned: a model that cannot
/// answer is reported as <see cref="Degraded"/> with whatever it did produce, and the caller falls
/// back to its own deterministic logic. Failing loud-but-optional is the whole contract.
/// </summary>
public sealed record DecisionResult(
    string Impl,
    IReadOnlyDictionary<string, DecisionAnswer> Answers,
    long LatencyMs,
    int StateChars,
    bool Degraded = false,
    string? Error = null,
    string? ModelVersion = null)
{
    public bool TryGet(string key, out DecisionAnswer answer) => Answers.TryGetValue(key, out answer!);

    public double? ProbabilityOf(string key) => Answers.TryGetValue(key, out var a) ? a.Probability : null;

    public string? ValueOf(string key) => Answers.TryGetValue(key, out var a) ? a.Value : null;

    public double? ScoreOf(string key) => Answers.TryGetValue(key, out var a) ? a.Score : null;
}

/// <summary>
/// The unstructured context a decision is made against. Named parts rather than one blob so the
/// wire adapter can send a structured state and so the durable event can record a hash of exactly
/// what was shown — a decision is only reproducible if its input is.
/// </summary>
public sealed class DecisionState
{
    /// <summary>Per-part character cap. State size is the real cost driver, not output tokens.</summary>
    public const int DefaultPartChars = 8_000;

    private readonly List<StatePart> _parts = [];

    public sealed record StatePart(string Name, string Text, bool Truncated);

    public IReadOnlyList<StatePart> Parts => _parts;

    public int Chars { get; private set; }

    /// <summary>Adds a named part, clipping to <paramref name="maxChars"/> and marking the clip.</summary>
    public DecisionState Add(string name, string? text, int maxChars = DefaultPartChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return this;
        var body = text!;
        var truncated = false;
        if (body.Length > maxChars)
        {
            // Keep the head: for diffs, commands and briefs the beginning carries the intent.
            body = body[..maxChars] + "\n…[truncated]";
            truncated = true;
        }
        _parts.Add(new StatePart(name, body, truncated));
        Chars += body.Length;
        return this;
    }

    /// <summary>Short stable digest of the state, recorded on the decision event for replay.</summary>
    public string Hash()
    {
        var builder = new StringBuilder();
        foreach (var part in _parts) builder.Append(part.Name).Append('\u0000').Append(part.Text).Append('\u0001');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16].ToLowerInvariant();
    }
}

/// <summary>
/// A model that turns unstructured state into typed decisions — the "System One" seam. Returns
/// <c>null</c> when it has no opinion (disabled, unconfigured, timed out, errored); callers must
/// treat null as "use the deterministic path", never as "the answer is no".
/// </summary>
public interface IDecisionModel
{
    /// <summary>Stable implementation id recorded on every decision event ("none", "systemone", …).</summary>
    string Impl { get; }

    /// <summary>False when this model is not configured; the service skips the call entirely.</summary>
    bool Available { get; }

    Task<DecisionResult?> DecideAsync(
        DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct = default);
}

/// <summary>
/// The default model: no opinion, ever. Mounting this is how the harness "works in the normal
/// way" — every seam falls through to the deterministic logic it already had, with no network
/// call, no latency and nothing new in the session log.
/// </summary>
public sealed class NoOpDecisionModel : IDecisionModel
{
    public static readonly NoOpDecisionModel Instance = new();

    public string Impl => "none";

    public bool Available => false;

    public Task<DecisionResult?> DecideAsync(
        DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct = default)
        => Task.FromResult<DecisionResult?>(null);
}

/// <summary>The seams that may consult a decision model, and the names used in settings and events.</summary>
public static class DecisionSeams
{
    /// <summary>Should a fresh user turn engage plan mode before the first model call?</summary>
    public const string AutoPlan = "auto-plan";

    /// <summary>Is this tool call risky enough to park for human approval?</summary>
    public const string RiskGate = "risk-gate";

    /// <summary>Is the agent making no progress (semantic loop, not just identical calls)?</summary>
    public const string Loop = "loop";

    /// <summary>How load-bearing is this context block, when pruning under pressure?</summary>
    public const string Compaction = "compaction";

    /// <summary>
    /// Seams with a consumer wired into the loop today. Reporting is driven by this list, not by
    /// <see cref="Known"/>, so the API and the Settings page can never advertise a seam that
    /// silently does nothing.
    /// </summary>
    public static readonly IReadOnlyList<string> Wired = [AutoPlan, RiskGate];

    /// <summary>
    /// Declared but not yet consumed. Named now so the settings vocabulary is stable and a probe
    /// can exercise the wire shape before the consumer lands; nothing in the loop reads them.
    /// </summary>
    public static readonly IReadOnlyList<string> Planned = [Loop, Compaction];

    /// <summary>Every seam name the settings file accepts.</summary>
    public static readonly IReadOnlyList<string> Known = [.. Wired, .. Planned];

    /// <summary>True when a consumer exists, so an "enabled" report means something will happen.</summary>
    public static bool IsWired(string seam) => Wired.Contains(seam, StringComparer.Ordinal);
}
