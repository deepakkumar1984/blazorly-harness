using System.Text.RegularExpressions;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

/// <summary>The deterministic complexity estimate for a user brief.</summary>
public sealed record ComplexityScore(int Total, IReadOnlyList<string> Reasons);

/// <summary>
/// Heuristic complexity scorer for user briefs. Pure and deterministic on purpose:
/// the auto-plan decision must be free, instant, and reproducible in tests. Signals
/// are additive (0–100) and every contributing rule names itself in the reasons.
/// </summary>
public static class ComplexityScorer
{
    private static readonly Regex NumberedItem = new(@"(?m)^\s*\d+[.)]\s+\S", RegexOptions.Compiled);
    private static readonly Regex BulletItem = new(@"(?m)^\s*[-*]\s+\S", RegexOptions.Compiled);
    private static readonly Regex FileRef = new(@"(?<![\w@])@[\w][\w./+-]{0,120}", RegexOptions.Compiled);
    private static readonly Regex CodeFence = new(@"^```", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly string[] SequencingWords =
        ["first", "then", "after that", "next", "finally", "once", "before that", "lastly", "step by step"];

    private static readonly string[] ScopeVerbs =
        ["refactor", "migrate", "redesign", "rewrite", "restructure", "overhaul", "port", "architect",
         "implement", "build", "integrate", "end-to-end", "end to end"];

    private static readonly string[] MultiEntityWords =
        ["across", "multiple files", "several", "each of", "all the", "every module", "every file", "throughout"];

    private static readonly string[] QuestionStarters =
        ["what", "why", "how does", "how do", "how is", "how was", "who", "when", "where", "is ", "are ",
         "do ", "does ", "did ", "can ", "explain", "describe", "tell me", "summarize", "review"];

    public static ComplexityScore Score(string text)
    {
        var reasons = new List<string>();
        var total = 0;

        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new ComplexityScore(0, reasons);

        // length — substantial briefs carry design surface area
        if (trimmed.Length >= 240)
        {
            total += 8;
            if (trimmed.Length >= 600) total += 8;
            if (trimmed.Length >= 1200) total += 9;
            reasons.Add($"substantial brief (~{trimmed.Length} chars)");
        }

        // explicit multi-step structure
        var numbered = NumberedItem.Matches(trimmed).Count;
        if (numbered >= 3)
        {
            total += 14;
            reasons.Add($"numbered steps ({numbered})");
        }
        else if (BulletItem.Matches(trimmed).Count >= 3)
        {
            total += 8;
            reasons.Add("bullet list (3+)");
        }

        // sequencing language — the brief narrates an order of operations
        var sequencing = SequencingWords.Where(w => trimmed.Contains(w, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
        if (sequencing.Count >= 2)
        {
            total += 10;
            reasons.Add("sequencing words (" + string.Join(", ", sequencing.Take(3)) + ")");
        }

        // scope verbs — the work reshapes code rather than touching a point
        var verbs = ScopeVerbs.Where(w => trimmed.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
        if (verbs.Count > 0)
        {
            total += 12;
            if (verbs.Count >= 2) total += 6;
            reasons.Add("scope: " + string.Join("/", verbs.Take(2)));
        }

        // multi-entity targets
        if (MultiEntityWords.Any(w => trimmed.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            total += 8;
            reasons.Add("multiple targets");
        }

        // @file references — the brief spans specific artifacts
        var refs = FileRef.Matches(trimmed).Count;
        if (refs >= 2)
        {
            total += 8;
            reasons.Add($"{refs} @file references");
        }

        // code blocks — concrete material to rework
        var fences = CodeFence.Matches(trimmed).Count;
        if (fences >= 2)
        {
            total += 6;
            reasons.Add("code blocks");
        }

        // questions rarely need a plan: investigation is the answer, not a mutation brief
        var lower = trimmed.ToLowerInvariant();
        if (QuestionStarters.Any(s => lower.StartsWith(s, StringComparison.Ordinal)))
        {
            total = Math.Min(total, 30);
            reasons.Add("reads as a question — capped");
        }

        if (total > 100) total = 100;
        return new ComplexityScore(total, reasons);
    }
}

/// <summary>
/// Decides whether a fresh user turn should auto-engage plan mode. Pure over the
/// session log + claimed messages so the exact policy is unit-testable.
/// </summary>
public static class AutoPlanPolicy
{
    /// <summary>
    /// The human brief for this turn when auto-plan is structurally allowed to consider it, or
    /// null when the turn is exempt. These guards are structural, not semantic, so a decision
    /// model replaces only the complexity judgment below — never this.
    /// </summary>
    public static string? EligibleBrief(Session session, IReadOnlyList<Message> messages)
    {
        if (session.Events.Any(e => e.Type == SessionEventTypes.SubagentDescriptor)) return null; // subagent briefs are orchestrator-authored
        if (GoalService.Active(session) is not null) return null; // goal rounds drive their own continuation turns

        var text = string.Join("\n", messages
            .Where(m => m.Role == "user" && m.Source.Kind == "user")
            .Select(m => m.FlattenText())).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Fresh-arc rule: true when plan mode was lifted during the previous user turn (an approval
    /// or a manual /plan off), which makes this prompt a follow-up in that arc — it should run
    /// without re-engaging. Re-engagement waits for a brief sent after that turn ended.
    /// </summary>
    public static bool FollowsApprovedPlan(Session session)
    {
        var lastUserSeq = (int?)null;
        var lastLiftSeq = (int?)null;
        var events = session.Events;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            if (lastUserSeq is null && e.Type == SessionEventTypes.UserMessage) lastUserSeq = e.Seq;
            if (lastLiftSeq is null && e.Type == SessionEventTypes.PlanMode && !e.Data.GetProperty("active").GetBoolean())
                lastLiftSeq = e.Seq;
            if (lastUserSeq is not null && lastLiftSeq is not null) break;
        }
        return lastLiftSeq is not null && lastUserSeq is not null && lastLiftSeq > lastUserSeq;
    }

    /// <summary>True when this turn's brief looks complex enough to plan first.</summary>
    public static bool ShouldEngage(Session session, IReadOnlyList<Message> messages, int threshold, out ComplexityScore score)
    {
        score = new ComplexityScore(0, []);
        if (EligibleBrief(session, messages) is not { } text) return false;

        var candidate = ComplexityScorer.Score(text);
        score = candidate;
        if (candidate.Total < threshold) return false;
        if (FollowsApprovedPlan(session)) return false;
        return true;
    }
}

/// <summary>
/// auto-plan: at the first step of a fresh user turn, score the brief for complexity
/// and engage plan mode before the model runs. Reuses the plan-mode machinery (mutation
/// guard, exit_plan_mode approval) — this plugin only decides *when* planning starts.
/// </summary>
public sealed class AutoPlanPlugin(
    int threshold = AutoPlanPlugin.DefaultThreshold,
    Core.Decisions.DecisionService? decisions = null,
    AutoPlanDecisionOptions? decision = null) : HarnessPlugin
{
    public const int DefaultThreshold = 55;

    /// <summary>The question key the decision model answers.</summary>
    public const string PlanKey = "needs_plan";

    public override string Name => "auto-plan";
    public override string[] Inject { get; } = [PlanModeService.ServiceKey, "systemPrompt"];

    private readonly AutoPlanDecisionOptions _decision = decision ?? new AutoPlanDecisionOptions();

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var planMode = ctx.Get<PlanModeService>(PlanModeService.ServiceKey);

        ctx.OnWaterfall<PreStepEvent, List<Message>, PreStepDecision>("agent/pre-step", async (payload, value, next, ct) =>
        {
            // Step 1 of a turn is the only place a human brief enters; steers land at
            // later steps and must never flip the mode mid-turn.
            if (payload.Step == 1 && value is { Count: > 0 }
                && !planMode.IsActive(payload.Agent.Session)
                && await ShouldEngageAsync(payload.Agent.Session, value, ct).ConfigureAwait(false) is { } score)
            {
                PlanModeService.Toggle(payload.Agent.Session, active: true, auto: true, score: score.Total, reasons: score.Reasons);
            }
            return await next(value).ConfigureAwait(false);
        });

        var prompt = ctx.Get<SystemPromptService>("systemPrompt");
        var section = prompt.RegisterSection("auto-plan", 103, context =>
        {
            if (context.Agent is null) return "";
            var mode = planMode.Latest(context.Agent.Session);
            if (mode is not { Active: true, Auto: true }) return "";
            var why = mode.Reasons is { Count: > 0 } ? string.Join("; ", mode.Reasons) : "complex brief";
            return
                $"Plan mode was engaged automatically for this task (complexity {mode.Score ?? 0}/100: {why}). "
                + "Investigate, then present the plan with exit_plan_mode for approval. "
                + "If no interactive reviewer is available, present the plan as your final message and stop.";
        });
        ctx.Effect(section.Dispose);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The engagement decision. A decision model answers first when its seam is enabled and it is
    /// confident in either direction; an uncertain, unavailable or failed call falls through to the
    /// deterministic heuristic, so disabling System One restores exactly the previous behaviour.
    /// Returns the score to display when plan mode should engage, else null.
    /// </summary>
    public async Task<ComplexityScore?> ShouldEngageAsync(
        Session session, IReadOnlyList<Message> messages, CancellationToken ct)
    {
        // Structural guards first: exempt turns never reach the model, so they cost nothing.
        if (AutoPlanPolicy.EligibleBrief(session, messages) is not { } brief) return null;
        if (AutoPlanPolicy.FollowsApprovedPlan(session)) return null;

        if (decisions is not null && decisions.IsEnabled(Core.Decisions.DecisionSeams.AutoPlan))
        {
            var state = new Core.Decisions.DecisionState()
                .Add("brief", brief, _decision.MaxBriefChars)
                .Add("cwd", session.Header.Cwd ?? "", 260);
            var result = await decisions.AskAsync(Core.Decisions.DecisionSeams.AutoPlan, state,
                new Core.Decisions.Noul(PlanKey,
                    "Does this request need investigation and an approved plan before anything is changed? "
                    + "Answer yes for multi-file, multi-step or design-shaped work; no for a question, "
                    + "a lookup, or a single small edit whose scope is already obvious."),
                session, ct).ConfigureAwait(false);

            if (result?.ProbabilityOf(PlanKey) is { } p)
            {
                if (p >= _decision.EngageAt)
                    return new ComplexityScore((int)Math.Round(p * 100), [$"decision model: P(needs plan) = {p:P0}"]);
                if (p <= _decision.SkipAt) return null;
                // Inside the uncertain band: the model abstains, the heuristic decides.
            }
        }

        return AutoPlanPolicy.ShouldEngage(session, messages, threshold, out var score) ? score : null;
    }
}

/// <summary>
/// Calibration bands for the auto-plan decision. Two thresholds rather than one: below
/// <see cref="SkipAt"/> the brief certainly runs, at or above <see cref="EngageAt"/> it certainly
/// plans, and the band between is where the model abstains and the deterministic scorer decides.
/// A calibrated model is only worth trusting where it is actually confident.
/// </summary>
public sealed record AutoPlanDecisionOptions
{
    public double EngageAt { get; init; } = 0.60;
    public double SkipAt { get; init; } = 0.25;
    public int MaxBriefChars { get; init; } = 8_000;
}
