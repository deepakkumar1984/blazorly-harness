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
    /// <summary>True when this turn's brief looks complex enough to plan first.</summary>
    public static bool ShouldEngage(Session session, IReadOnlyList<Message> messages, int threshold, out ComplexityScore score)
    {
        score = new ComplexityScore(0, []);
        if (session.Events.Any(e => e.Type == SessionEventTypes.SubagentDescriptor)) return false; // subagent briefs are orchestrator-authored
        if (GoalService.Active(session) is not null) return false; // goal rounds drive their own continuation turns

        var text = string.Join("\n", messages
            .Where(m => m.Role == "user" && m.Source.Kind == "user")
            .Select(m => m.FlattenText())).Trim();
        if (text.Length == 0) return false;

        var candidate = ComplexityScorer.Score(text);
        if (candidate.Total < threshold)
        {
            score = candidate;
            return false;
        }

        // Fresh-arc rule: if plan mode was lifted during the previous user turn (an
        // approval or a manual /plan off), this prompt is a follow-up in that arc —
        // let it run; re-engage only for a brief sent after that turn ended.
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
        if (lastLiftSeq is not null && lastUserSeq is not null && lastLiftSeq > lastUserSeq)
        {
            score = candidate;
            return false;
        }

        score = candidate;
        return true;
    }
}

/// <summary>
/// auto-plan: at the first step of a fresh user turn, score the brief for complexity
/// and engage plan mode before the model runs. Reuses the plan-mode machinery (mutation
/// guard, exit_plan_mode approval) — this plugin only decides *when* planning starts.
/// </summary>
public sealed class AutoPlanPlugin(int threshold = AutoPlanPlugin.DefaultThreshold) : HarnessPlugin
{
    public const int DefaultThreshold = 55;

    public override string Name => "auto-plan";
    public override string[] Inject { get; } = [PlanModeService.ServiceKey, "systemPrompt"];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var planMode = ctx.Get<PlanModeService>(PlanModeService.ServiceKey);

        ctx.OnWaterfall<PreStepEvent, List<Message>, PreStepDecision>("agent/pre-step", async (payload, value, next, ct) =>
        {
            // Step 1 of a turn is the only place a human brief enters; steers land at
            // later steps and must never flip the mode mid-turn.
            if (payload.Step == 1 && value is { Count: > 0 }
                && !planMode.IsActive(payload.Agent.Session)
                && AutoPlanPolicy.ShouldEngage(payload.Agent.Session, value, threshold, out var score))
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
}
