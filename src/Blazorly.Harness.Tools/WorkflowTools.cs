using System.Text;
using System.Text.Json.Serialization;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record WorkflowStepInput(string Prompt, string? Description = null);

public sealed record WorkflowArgs(string? Name, IReadOnlyList<WorkflowStepInput> Steps);

public sealed record WorkflowStepResult(
    int Step,
    [property: JsonPropertyName("session_id")] string SessionId,
    string Summary);

public sealed record WorkflowOutput(string? Name, string Status, IReadOnlyList<WorkflowStepResult> Steps);

/// <summary>
/// workflow: sequential delegated steps — one fresh subagent per step, each receiving its step
/// prompt plus a context preamble carrying the previous step's summary. Not concurrency-safe.
/// </summary>
public sealed class WorkflowTool(SubagentService subagents) : ToolDefinition<WorkflowArgs, WorkflowOutput>
{
    public override string Name => "workflow";

    public override string Description =>
        "Run a fixed sequence of delegated steps, one fresh subagent per step. Each step receives its prompt "
        + "plus the previous step's summary as context. Returns {step, session_id, summary} per step. "
        + "Use for a known pipeline; use ralph for open-ended objectives.";

    public override int? TimeoutMs => 600000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["name"] = JsonSchema.String("Optional workflow name used in prompts and the report."),
            ["steps"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["prompt"] = JsonSchema.String("The step's instruction; this agent does only this step."),
                    ["description"] = JsonSchema.String("Optional one-line description of the step."),
                },
                Required = ["prompt"],
                AdditionalProperties = false,
            }, minItems: 1, description: "Ordered steps to run."),
        },
        required: ["steps"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["name"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(),
            ["steps"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["step"] = JsonSchema.Integer(),
                    ["session_id"] = JsonSchema.String(),
                    ["summary"] = JsonSchema.String(),
                },
                Required = ["step", "session_id", "summary"],
                AdditionalProperties = false,
            }),
        },
        required: ["status", "steps"]);

    protected override async Task<WorkflowOutput> ExecuteTyped(WorkflowArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        if (args.Steps.Count == 0) throw new ToolException("INVALID_ARGS", "workflow needs at least one step");
        for (var i = 0; i < args.Steps.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(args.Steps[i].Prompt))
                throw new ToolException("INVALID_ARGS", $"step {i + 1} prompt must be non-empty");
        }

        var name = string.IsNullOrWhiteSpace(args.Name) ? "workflow" : args.Name.Trim();
        var results = new List<WorkflowStepResult>(args.Steps.Count);
        string? previousSummary = null;
        for (var i = 0; i < args.Steps.Count; i++)
        {
            var step = args.Steps[i];
            var prompt = BuildStepPrompt(name, i + 1, args.Steps.Count, step.Prompt, previousSummary);
            var result = await subagents.SpawnAsync(lead, new SubagentRequest(
                Prompt: prompt,
                Description: step.Description ?? $"{name} step {i + 1}"), exec.Signal).ConfigureAwait(false);
            results.Add(new WorkflowStepResult(i + 1, result.SessionId, result.Summary));
            previousSummary = result.Summary;
        }
        return new WorkflowOutput(args.Name?.Trim(), "completed", results);
    }

    internal static string BuildStepPrompt(string name, int number, int total, string prompt, string? previousSummary)
    {
        var builder = new StringBuilder();
        builder.Append($"You are step {number} of {total} in the workflow '{name}', run by a coordinating lead.");
        if (previousSummary is { Length: > 0 })
        {
            builder.AppendLine().AppendLine();
            builder.AppendLine("Context from the previous step — build on it, do not redo it:");
            builder.Append(previousSummary.Trim());
        }
        builder.AppendLine().AppendLine();
        builder.AppendLine("Your step's prompt:");
        builder.Append(prompt.Trim());
        builder.AppendLine().AppendLine();
        builder.Append("Complete just this step autonomously, then stop; your final message becomes this step's result for the next agent.");
        return builder.ToString();
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(WorkflowArgs args, WorkflowOutput output)
    {
        var builder = new StringBuilder();
        builder.Append($"Workflow '{output.Name ?? "workflow"}' completed {output.Steps.Count} step(s):");
        foreach (var step in output.Steps)
        {
            builder.AppendLine().AppendLine();
            builder.Append($"Step {step.Step} ({step.SessionId}):").AppendLine();
            builder.Append(step.Summary);
        }
        return [new TextBlock(builder.ToString())];
    }

    protected override ToolCallView? PresentCallTyped(WorkflowArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = $"Workflow '{(args.Name is { Length: > 0 } ? args.Name : "workflow")}'",
        Description = $"{args.Steps.Count} steps",
    };
}

public sealed record RalphArgs(string Objective, int? MaxRounds = null);

public sealed record RalphRound(int Round, string Outcome, string Summary);

public sealed record RalphOutput(string Objective, IReadOnlyList<RalphRound> Rounds, string Status);

/// <summary>
/// ralph: a bounded loop of fresh subagents pursuing an immutable objective. Each round hands
/// off to the next until a child ends with OBJECTIVE_COMPLETE or the round budget is spent.
/// Not concurrency-safe.
/// </summary>
public sealed class RalphTool(SubagentService subagents) : ToolDefinition<RalphArgs, RalphOutput>
{
    public const int DefaultMaxRounds = 3;
    public const int MaxRoundCap = 20;

    public const string StatusComplete = "complete";
    public const string StatusPartial = "partial";

    private const string CompleteMarker = "OBJECTIVE_COMPLETE";
    private const string BlockedMarker = "OBJECTIVE_BLOCKED:";

    public override string Name => "ralph";

    public override string Description =>
        "Pursue an open-ended objective with a bounded loop of fresh subagents: each round's final message hands "
        + $"off to the next until a round ends with {CompleteMarker} or max_rounds is reached. "
        + "Prefer this over manual re-spawning when success is not guaranteed in one pass.";

    public override int? TimeoutMs => 900000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["objective"] = JsonSchema.String("The immutable objective; rounds may not redefine or narrow it."),
            ["max_rounds"] = JsonSchema.Integer($"Round budget, 1–{MaxRoundCap}. Defaults to {DefaultMaxRounds}."),
        },
        required: ["objective"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["objective"] = JsonSchema.String(),
            ["rounds"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["round"] = JsonSchema.Integer(),
                    ["outcome"] = JsonSchema.String(),
                    ["summary"] = JsonSchema.String(),
                },
                Required = ["round", "outcome", "summary"],
                AdditionalProperties = false,
            }),
            ["status"] = JsonSchema.String(),
        },
        required: ["objective", "rounds", "status"]);

    protected override async Task<RalphOutput> ExecuteTyped(RalphArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        var objective = args.Objective.Trim();
        if (objective.Length == 0) throw new ToolException("INVALID_ARGS", "objective must be non-empty");
        var maxRounds = Math.Clamp(args.MaxRounds ?? DefaultMaxRounds, 1, MaxRoundCap);

        var rounds = new List<RalphRound>(maxRounds);
        Handoff? handoff = null;
        for (var round = 1; round <= maxRounds; round++)
        {
            var result = await subagents.SpawnAsync(lead, new SubagentRequest(
                Prompt: BuildRoundPrompt(objective, round, handoff),
                Description: $"ralph round {round} of {maxRounds}"), exec.Signal).ConfigureAwait(false);

            var (outcome, summary, blocker) = ParseFinalLine(result.Summary);
            rounds.Add(new RalphRound(round, outcome, summary));
            if (outcome == StatusComplete) return new RalphOutput(objective, rounds, StatusComplete);
            handoff = outcome == "blocked"
                ? new Handoff("blocked", summary, $"clear the blocker first: {blocker}")
                : new Handoff("progress", summary, "continue toward the objective from here");
        }
        return new RalphOutput(objective, rounds, StatusPartial);
    }

    private sealed record Handoff(string Status, string Summary, string Next);

    private static string BuildRoundPrompt(string objective, int round, Handoff? handoff)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Objective (immutable — never redefine or narrow it):");
        builder.Append(objective);
        if (handoff is not null)
        {
            builder.AppendLine().AppendLine();
            builder.AppendLine($"Previous round handoff (round {round - 1}):");
            builder.AppendLine($"- status: {handoff.Status}");
            builder.AppendLine($"- summary: {handoff.Summary}");
            builder.AppendLine($"- next: {handoff.Next}");
        }
        builder.AppendLine().AppendLine();
        builder.Append("Work autonomously toward the objective this round; a fresh agent continues from your handoff afterwards.");
        builder.AppendLine().AppendLine();
        builder.Append($"End your final message with a line '{CompleteMarker}' when the objective is fully achieved, "
            + $"or '{BlockedMarker} reason' when you cannot proceed.");
        return builder.ToString();
    }

    /// <summary>Classifies a round's summary by its final non-empty line; the marker line is stripped from the body.</summary>
    internal static (string Outcome, string Summary, string? Blocker) ParseFinalLine(string summary)
    {
        var lines = summary.Replace("\r\n", "\n").Split('\n');
        var lastLine = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Trim().Length > 0)
            {
                lastLine = i;
                break;
            }
        }
        if (lastLine < 0) return ("progress", summary.Trim(), null);
        var finalLine = lines[lastLine].Trim();
        var body = string.Join("\n", lines.Take(lastLine)).Trim();
        var summaryWithoutMarker = body.Length > 0 ? body : summary.Trim();
        if (finalLine == CompleteMarker) return (StatusComplete, summaryWithoutMarker, null);
        if (finalLine.StartsWith(BlockedMarker, StringComparison.Ordinal))
        {
            var reason = finalLine[BlockedMarker.Length..].Trim();
            return ("blocked", summaryWithoutMarker, reason.Length > 0 ? reason : "unspecified");
        }
        return ("progress", summary.Trim(), null);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(RalphArgs args, RalphOutput output)
    {
        var builder = new StringBuilder();
        builder.Append("Ralph loop — objective: ").Append(output.Objective);
        foreach (var round in output.Rounds)
        {
            builder.AppendLine().AppendLine();
            builder.Append($"Round {round.Round} — {round.Outcome}:").AppendLine();
            builder.Append(round.Summary);
        }
        builder.AppendLine().AppendLine();
        builder.Append($"Final status: {output.Status}");
        return [new TextBlock(builder.ToString())];
    }

    protected override ToolCallView? PresentCallTyped(RalphArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Ralph loop",
        Description = args.Objective.Length > 80 ? args.Objective[..80] : args.Objective,
    };
}

/// <summary>Mounts the delegation workflows: the fixed-step pipeline and the ralph loop.</summary>
public sealed class WorkflowPlugin : HarnessPlugin
{
    public override string Name => "workflows";
    public override string[] Inject { get; } = [ToolRuntime.ServiceKey, SubagentService.ServiceKey];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var subagents = ctx.TryGet<SubagentService>(SubagentService.ServiceKey) ?? SubagentService.Mount(ctx);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);
        ctx.Effect(tools.Register(new WorkflowTool(subagents)).Dispose);
        ctx.Effect(tools.Register(new RalphTool(subagents)).Dispose);
        return Task.CompletedTask;
    }
}
