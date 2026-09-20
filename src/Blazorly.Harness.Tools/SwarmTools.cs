using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

// ---- swarm: shard, fan out, join, review ----

public sealed record SwarmTaskInput(string Title, string Prompt);

public sealed record SwarmArgs(
    string Objective,
    IReadOnlyList<SwarmTaskInput>? Tasks = null,
    [property: JsonPropertyName("max_parallel")] int? MaxParallel = null,
    bool Review = true,
    [property: JsonPropertyName("max_review_rounds")] int? MaxReviewRounds = null);

public sealed record SwarmTaskResult(
    string Title,
    string Status,
    [property: JsonPropertyName("session_id")] string? SessionId,
    string? Summary,
    [property: JsonPropertyName("review_note")] string? ReviewNote,
    int Attempt);

public sealed record SwarmReviewResult(
    string Verdict,
    string Notes,
    [property: JsonPropertyName("failed_tasks")] IReadOnlyList<string> FailedTasks,
    IReadOnlyList<string> Followups,
    string? Diagnostic,
    int Round);

public sealed record SwarmOutput(string Objective, string Status, IReadOnlyList<SwarmTaskResult> Tasks, IReadOnlyList<SwarmReviewResult> Reviews);

/// <summary>
/// swarm: parallel fan-out over a task list. With no explicit tasks a planner subagent shards
/// the objective first (auto-sharding); workers then run as concurrent background subagents
/// (capped by max_parallel) and are joined. A reviewer subagent verifies the joined work in the
/// workspace and — while review rounds remain — failed tasks are re-dispatched with the review
/// notes. If the tool call is cancelled mid-join, running workers are left alive: they are
/// continuable children, readable later via subagent_list.
/// </summary>
public sealed class SwarmTool(SubagentService subagents) : ToolDefinition<SwarmArgs, SwarmOutput>
{
    public const int DefaultMaxParallel = 4;
    public const int MaxParallelCap = 10;
    public const int DefaultMaxReviewRounds = 1;
    public const int MaxReviewRoundsCap = 3;

    public const string StatusComplete = "complete";
    public const string StatusPartial = "partial";
    public const string StatusReviewFailed = "review-failed";
    public const string StatusReviewInconclusive = "review-inconclusive";

    public override string Name => "swarm";

    public override string Description =>
        "Run an objective as a parallel swarm: a planner shards it into tasks (or take explicit tasks), "
        + "workers execute concurrently as background subagents (max_parallel), then a reviewer agent verifies "
        + "the completed work in the workspace. Failed tasks are re-dispatched with the review notes up to "
        + "max_review_rounds. Use for wide, independent work; use workflow for a fixed sequential pipeline, "
        + "ralph for an open-ended loop.";

    public override int? TimeoutMs => 900000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["objective"] = JsonSchema.String("The goal the swarm must achieve; tasks exist to cover it."),
            ["tasks"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["title"] = JsonSchema.String("Short task name used in reports and review verdicts."),
                    ["prompt"] = JsonSchema.String("Self-contained worker instruction: the worker sees only the objective and its own task."),
                },
                Required = ["title", "prompt"],
                AdditionalProperties = false,
            }, description: "Optional explicit task list; omit to let the planner shard the objective."),
            ["max_parallel"] = JsonSchema.Integer($"Concurrent workers, 1–{MaxParallelCap}. Defaults to {DefaultMaxParallel}."),
            ["review"] = JsonSchema.Boolean("Run the reviewer gate after the join (default true)."),
            ["max_review_rounds"] = JsonSchema.Integer($"Re-dispatch rounds for failed tasks, 0–{MaxReviewRoundsCap}. Defaults to {DefaultMaxReviewRounds}."),
        },
        required: ["objective"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["objective"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(values:
            [
                JsonSerializer.SerializeToElement(StatusComplete),
                JsonSerializer.SerializeToElement(StatusPartial),
                JsonSerializer.SerializeToElement(StatusReviewFailed),
                JsonSerializer.SerializeToElement(StatusReviewInconclusive),
            ]),
            ["tasks"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["title"] = JsonSchema.String(),
                    ["status"] = JsonSchema.String(),
                    ["session_id"] = JsonSchema.String(),
                    ["summary"] = JsonSchema.String(),
                    ["review_note"] = JsonSchema.String(),
                    ["attempt"] = JsonSchema.Integer(),
                },
                Required = ["title", "status", "attempt"],
                AdditionalProperties = false,
            }),
            ["reviews"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["verdict"] = JsonSchema.String(),
                    ["notes"] = JsonSchema.String(),
                    ["failed_tasks"] = JsonSchema.Array(JsonSchema.String()),
                    ["followups"] = JsonSchema.Array(JsonSchema.String()),
                    ["diagnostic"] = JsonSchema.String(),
                    ["round"] = JsonSchema.Integer(),
                },
                Required = ["verdict", "notes", "round"],
                AdditionalProperties = false,
            }),
        },
        required: ["objective", "status", "tasks"]);

    internal static JsonSchema.Schema PlannerSchema { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["tasks"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["title"] = JsonSchema.String(),
                    ["prompt"] = JsonSchema.String(),
                },
                Required = ["title", "prompt"],
                AdditionalProperties = false,
            }, minItems: 1, maxItems: 12, description: "Independent, self-contained tasks covering the objective."),
        },
        required: ["tasks"]);

    internal static JsonSchema.Schema ReviewerSchema { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["verdict"] = JsonSchema.String(values: [JsonSerializer.SerializeToElement("pass"), JsonSerializer.SerializeToElement("fail")]),
            ["notes"] = JsonSchema.String("What you verified, what holds, what does not."),
            ["failed_tasks"] = JsonSchema.Array(JsonSchema.String(), description: "Titles of tasks whose work is missing or wrong and needs a redo."),
            ["followups"] = JsonSchema.Array(JsonSchema.String(), description: "Smaller items the lead can finish itself."),
        },
        required: ["verdict", "notes"]);

    internal sealed record Planned(string Title, string Prompt, int Attempt = 1, string? ReviewNote = null);

    internal sealed record PlannedTask(string Title, string Prompt);

    private sealed record Outcome(Planned Task, string? SessionId, string Summary, string FinishKind);

    protected override async Task<SwarmOutput> ExecuteTyped(SwarmArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        var objective = args.Objective.Trim();
        if (objective.Length == 0) throw new ToolException("INVALID_ARGS", "objective must be non-empty");
        var maxParallel = Math.Clamp(args.MaxParallel ?? DefaultMaxParallel, 1, MaxParallelCap);
        var maxReviewRounds = Math.Clamp(args.MaxReviewRounds ?? DefaultMaxReviewRounds, 0, MaxReviewRoundsCap);

        // 1. shard: explicit tasks, or the planner decomposes the objective.
        List<Planned> pending = args.Tasks is { Count: > 0 }
            ? [.. args.Tasks.Select(t => Validated(t))]
            : await PlanAsync(lead, objective, exec.Signal).ConfigureAwait(false);

        // 2. fan out → join → review → (bounded) re-dispatch of failed tasks.
        var reviews = new List<SwarmReviewResult>();
        var settled = new Dictionary<string, Outcome>(StringComparer.Ordinal); // title → latest outcome, survives retry rounds
        for (var round = 0; ; round++)
        {
            var outcomes = await FanOutAsync(lead, objective, pending, maxParallel, exec.Signal).ConfigureAwait(false);
            foreach (var outcome in outcomes) settled[outcome.Task.Title] = outcome;
            if (!args.Review) break;

            var review = await ReviewAsync(lead, objective, [.. settled.Values], round + 1, exec.Signal).ConfigureAwait(false);
            reviews.Add(review);
            if (review.Diagnostic is not null) break; // verdict unreadable: never retry on our own parse failure
            if (review.Verdict == "pass" || round >= maxReviewRounds) break;

            var failedTitles = review.FailedTasks.ToHashSet(StringComparer.Ordinal);
            var redo = settled.Values.Where(o => failedTitles.Contains(o.Task.Title) || o.FinishKind != "completed").ToList();
            if (redo.Count == 0) break;
            pending = [.. redo.Select(o => o.Task with { Attempt = o.Task.Attempt + 1, ReviewNote = review.Notes })];
        }

        List<Outcome> finalOutcomes = [.. settled.Values];
        return new SwarmOutput(objective, FinalStatus(args.Review, reviews, finalOutcomes), finalOutcomes.Select(ToResult).ToList(), reviews);
    }

    private static string FinalStatus(bool reviewEnabled, IReadOnlyList<SwarmReviewResult> reviews, IReadOnlyList<Outcome> outcomes)
    {
        if (!reviewEnabled)
            return outcomes.All(o => o.FinishKind == "completed") ? StatusComplete : StatusPartial;
        var last = reviews[^1];
        if (last.Diagnostic is not null) return StatusReviewInconclusive;
        return last.Verdict == "pass" ? StatusComplete : StatusReviewFailed;
    }

    private static SwarmTaskResult ToResult(Outcome outcome) => new(
        outcome.Task.Title,
        outcome.FinishKind == "completed" ? "completed" : outcome.FinishKind,
        outcome.SessionId,
        outcome.Summary,
        outcome.Task.ReviewNote,
        outcome.Task.Attempt);

    private static Planned Validated(SwarmTaskInput input)
    {
        var title = input.Title?.Trim() ?? "";
        var prompt = input.Prompt?.Trim() ?? "";
        if (title.Length == 0 || prompt.Length == 0)
            throw new ToolException("INVALID_ARGS", "every swarm task needs a non-empty title and prompt");
        return new Planned(title, prompt);
    }

    private async Task<List<Planned>> PlanAsync(Agent lead, string objective, CancellationToken ct)
    {
        var result = await subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: "Shard the objective below into independent, self-contained tasks a single agent can complete "
                + "without talking to the others. Prefer 2–6 tasks; exceed that only when the objective genuinely "
                + "decomposes further. Task prompts must be self-contained (a worker sees only the objective and its "
                + "own task), non-overlapping, and together they must cover the objective.\n\nObjective: " + objective,
            Description: "swarm planner",
            Persona: "You are a planning agent: decompose objectives into independent tasks. You do no work yourself.",
            OutputSchema: PlannerSchema), ct).ConfigureAwait(false);

        if (result.Structured is not { } structured)
            throw new ToolException("SWARM_PLAN_INVALID", $"planner produced no usable task list: {result.Diagnostic ?? result.Summary}");
        return [.. ParsePlanned(structured).Select(t => new Planned(t.Title, t.Prompt))];
    }

    internal static IEnumerable<PlannedTask> ParsePlanned(JsonElement structured)
    {
        if (!structured.TryGetProperty("tasks", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
            throw new ToolException("SWARM_PLAN_INVALID", "planner produced no tasks");
        var planned = new List<PlannedTask>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = StringOf(item, "title");
            var prompt = StringOf(item, "prompt");
            if (title is { Length: > 0 } && prompt is { Length: > 0 })
                planned.Add(new PlannedTask(title, prompt));
        }
        if (planned.Count == 0) throw new ToolException("SWARM_PLAN_INVALID", "planner produced no usable tasks");
        return planned;
    }

    /// <summary>Fans workers out as background subagents under a concurrency cap and joins them all.</summary>
    private async Task<List<Outcome>> FanOutAsync(
        Agent lead, string objective, IReadOnlyList<Planned> tasks, int maxParallel, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(maxParallel, maxParallel);
        var outcomes = new Outcome[tasks.Count];
        await Task.WhenAll(tasks.Select(async (task, i) =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var started = subagents.SpawnBackgroundAsync(lead, new SubagentRequest(
                    Prompt: WorkerPrompt(objective, task, i + 1, tasks.Count),
                    Description: $"swarm worker: {task.Title}",
                    Persona: "You are a swarm worker: complete your assigned task autonomously, then stop. Never ask "
                        + "the user questions — the user is not available to you; make reasonable assumptions and "
                        + "record them in your report.",
                    Continuable: true));
                var child = subagents.GetChild(started.SessionId);
                if (child is null)
                {
                    outcomes[i] = new Outcome(task, null, "(worker failed to start)", "error");
                    return;
                }
                await child.WhenIdleAsync().ConfigureAwait(false);
                var (summary, finishKind) = SubagentService.LatestOutput(child.Session);
                await subagents.FlushAsync(child.Id, CancellationToken.None).ConfigureAwait(false);
                outcomes[i] = new Outcome(task, child.Id, summary, finishKind);
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
        return [.. outcomes];
    }

    internal static string WorkerPrompt(string objective, Planned task, int number, int total)
    {
        var builder = new StringBuilder();
        builder.Append($"You are worker {number} of {total} in a swarm run by a coordinating lead.");
        builder.AppendLine().AppendLine();
        builder.AppendLine("Objective (context only — your task below is the authoritative scope):");
        builder.Append(objective);
        builder.AppendLine().AppendLine();
        builder.AppendLine($"Your task: {task.Title}");
        builder.Append(task.Prompt);
        if (task.Attempt > 1 && task.ReviewNote is { Length: > 0 })
        {
            builder.AppendLine().AppendLine();
            builder.AppendLine($"A reviewer examined attempt {task.Attempt - 1} and returned:");
            builder.Append(task.ReviewNote.Trim());
            builder.AppendLine().AppendLine();
            builder.Append("Address the review in this retry.");
        }
        builder.AppendLine().AppendLine();
        builder.Append("Complete your task autonomously in the workspace, then stop; your final message is your report to the lead.");
        return builder.ToString();
    }

    private async Task<SwarmReviewResult> ReviewAsync(
        Agent lead, string objective, IReadOnlyList<Outcome> outcomes, int round, CancellationToken ct)
    {
        var reports = string.Join("\n\n", outcomes.Select(o =>
            $"### {o.Task.Title} (attempt {o.Task.Attempt}, {o.FinishKind})\n{o.Summary}"));
        var result = await subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: "You are the reviewer for a completed swarm. Workers ran the tasks below in this workspace. "
                + "Verify their work: read what they changed, check it against the objective, and run cheap "
                + "verification (build, tests, lint) when it exists. Judge the delivered outcome, not the prose.\n\n"
                + "Objective: " + objective + "\n\nWorker reports:\n" + reports,
            Description: "swarm reviewer",
            Persona: "You are a rigorous reviewer agent: verify completed work against the workspace itself, "
                + "not the workers' claims. failed_tasks must name only task titles whose work needs a redo.",
            OutputSchema: ReviewerSchema), ct).ConfigureAwait(false);

        if (result.Structured is not { } structured)
            return new SwarmReviewResult("fail", result.Summary, [], [], result.Diagnostic ?? "reviewer output did not validate", round);

        var verdict = StringOf(structured, "verdict") == "pass" ? "pass" : "fail";
        return new SwarmReviewResult(
            verdict,
            StringOf(structured, "notes") ?? "",
            StringsOf(structured, "failed_tasks"),
            StringsOf(structured, "followups"),
            null,
            round);
    }

    internal static string? StringOf(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    internal static IReadOnlyList<string> StringsOf(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString() ?? "")]
            : [];

    protected override IReadOnlyList<ContentBlock> RenderTyped(SwarmArgs args, SwarmOutput output)
    {
        var builder = new StringBuilder();
        builder.Append($"Swarm — objective: {output.Objective}");
        builder.AppendLine().AppendLine();
        foreach (var task in output.Tasks)
        {
            builder.Append($"- {task.Title} [{task.Status}] (attempt {task.Attempt}");
            if (task.SessionId is not null) builder.Append($", {task.SessionId}");
            builder.Append(")");
            if (task.Summary is { Length: > 0 }) builder.Append(": ").Append(task.Summary.Replace("\n", " "));
            builder.AppendLine();
        }
        foreach (var review in output.Reviews)
        {
            builder.AppendLine();
            builder.Append($"Review (round {review.Round}) — {review.Verdict}: {review.Notes.Replace("\n", " ")}");
            if (review.Diagnostic is not null) builder.Append($" [diagnostic: {review.Diagnostic}]");
        }
        builder.AppendLine().AppendLine();
        builder.Append($"Final status: {output.Status}");
        return [new TextBlock(builder.ToString())];
    }

    protected override ToolCallView? PresentCallTyped(SwarmArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Swarm",
        Description = args.Objective.Length > 80 ? args.Objective[..80] : args.Objective,
    };
}

// ---- review: an independent reviewer for any completed work ----

public sealed record ReviewArgs(string Scope, string? Focus = null);

public sealed record ReviewIssue(
    string Severity,
    string? Location,
    string? Description);

public sealed record ReviewToolOutput(
    string Verdict,
    string Notes,
    IReadOnlyList<ReviewIssue> Issues,
    IReadOnlyList<string> Followups,
    string? Diagnostic,
    [property: JsonPropertyName("session_id")] string SessionId);

/// <summary>
/// review: spawns an independent reviewer subagent forked from this conversation (it sees the
/// session log through the last settled turn) that verifies the described completed work in the
/// workspace — reading files and running cheap checks rather than trusting the transcript — and
/// returns a schema-validated verdict with issues and followups.
/// </summary>
public sealed class ReviewTool(SubagentService subagents) : ToolDefinition<ReviewArgs, ReviewToolOutput>
{
    public override string Name => "review";

    public override string Description =>
        "Have an independent reviewer agent verify completed work. The reviewer is forked from this "
        + "conversation and checks the actual workspace (files, build, tests) instead of trusting the "
        + "transcript; it returns a structured verdict (pass | fail | concerns), specific issues, and "
        + "followups. Call it before declaring non-trivial work done.";

    public override int? TimeoutMs => 600000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["scope"] = JsonSchema.String("What was done and where — enough for a fresh reviewer to locate the work."),
            ["focus"] = JsonSchema.String("Optional aspect to weigh hardest (correctness, performance, tests, …)."),
        },
        required: ["scope"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["verdict"] = JsonSchema.String(values:
            [
                JsonSerializer.SerializeToElement("pass"),
                JsonSerializer.SerializeToElement("fail"),
                JsonSerializer.SerializeToElement("concerns"),
            ]),
            ["notes"] = JsonSchema.String(),
            ["issues"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["severity"] = JsonSchema.String(values:
                    [
                        JsonSerializer.SerializeToElement("critical"),
                        JsonSerializer.SerializeToElement("major"),
                        JsonSerializer.SerializeToElement("minor"),
                    ]),
                    ["location"] = JsonSchema.String(),
                    ["description"] = JsonSchema.String(),
                },
                Required = ["severity", "description"],
                AdditionalProperties = false,
            }),
            ["followups"] = JsonSchema.Array(JsonSchema.String()),
            ["diagnostic"] = JsonSchema.String(),
            ["session_id"] = JsonSchema.String(),
        },
        required: ["verdict", "notes", "session_id"]);

    internal static JsonSchema.Schema VerdictSchema { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["verdict"] = JsonSchema.String(values:
            [
                JsonSerializer.SerializeToElement("pass"),
                JsonSerializer.SerializeToElement("fail"),
                JsonSerializer.SerializeToElement("concerns"),
            ]),
            ["notes"] = JsonSchema.String("What you verified and how; the basis of the verdict."),
            ["issues"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["severity"] = JsonSchema.String(values:
                    [
                        JsonSerializer.SerializeToElement("critical"),
                        JsonSerializer.SerializeToElement("major"),
                        JsonSerializer.SerializeToElement("minor"),
                    ]),
                    ["location"] = JsonSchema.String("File, function, or test the issue lives at."),
                    ["description"] = JsonSchema.String("The specific problem and, when possible, the fix."),
                },
                Required = ["severity", "description"],
                AdditionalProperties = false,
            }, description: "Concrete problems found; empty when the work holds."),
            ["followups"] = JsonSchema.Array(JsonSchema.String(), description: "Small residual items the lead can finish itself."),
        },
        required: ["verdict", "notes"]);

    protected override async Task<ReviewToolOutput> ExecuteTyped(ReviewArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        var scope = args.Scope.Trim();
        if (scope.Length == 0) throw new ToolException("INVALID_ARGS", "scope must be non-empty");
        var focus = args.Focus?.Trim();

        var prompt = "Review the completed work described below. You are forked from the conversation that did it: "
            + "verify its claims against the actual workspace — read the files it touched, run cheap checks "
            + "(build, targeted tests, lint) when they exist — rather than trusting the transcript. Report "
            + "specific, actionable findings; an empty issues list means the work holds.\n\nScope: " + scope;
        if (focus is { Length: > 0 }) prompt += "\n\nWeigh hardest: " + focus;

        var result = await subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: prompt,
            Description: "reviewer",
            Persona: "You are an independent senior reviewer: skeptical, specific, and fair. Judge the "
                + "delivered work in the workspace, not the narrative around it.",
            Fork: true,
            OutputSchema: VerdictSchema), exec.Signal).ConfigureAwait(false);

        if (result.Structured is not { } structured)
            return new ReviewToolOutput("concerns", result.Summary, [], [], result.Diagnostic ?? "reviewer output did not validate", result.SessionId);

        var issues = new List<ReviewIssue>();
        if (structured.TryGetProperty("issues", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var severity = SwarmTool.StringOf(item, "severity") ?? "minor";
                var description = SwarmTool.StringOf(item, "description");
                if (description is null) continue;
                issues.Add(new ReviewIssue(severity, SwarmTool.StringOf(item, "location"), description));
            }
        }
        var verdict = SwarmTool.StringOf(structured, "verdict") is { } v && v is "pass" or "fail" or "concerns" ? v : "concerns";
        return new ReviewToolOutput(
            verdict,
            SwarmTool.StringOf(structured, "notes") ?? "",
            issues,
            SwarmTool.StringsOf(structured, "followups"),
            null,
            result.SessionId);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(ReviewArgs args, ReviewToolOutput output)
    {
        var builder = new StringBuilder();
        builder.Append($"Reviewer verdict: {output.Verdict}");
        if (output.Diagnostic is not null) builder.Append($" (diagnostic: {output.Diagnostic})");
        builder.AppendLine().AppendLine();
        builder.Append(output.Notes);
        foreach (var issue in output.Issues)
        {
            builder.AppendLine().AppendLine();
            builder.Append($"[{issue.Severity}] {(issue.Location is null ? "" : issue.Location + ": ")}{issue.Description}");
        }
        if (output.Followups.Count > 0)
        {
            builder.AppendLine().AppendLine();
            builder.Append("Followups: " + string.Join("; ", output.Followups));
        }
        return [new TextBlock(builder.ToString())];
    }

    protected override ToolCallView? PresentCallTyped(ReviewArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Review completed work",
        Description = args.Scope.Length > 80 ? args.Scope[..80] : args.Scope,
    };
}
