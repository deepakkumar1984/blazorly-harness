using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

/// <summary>The current goal: active|paused|blocked|complete, plus continuation bookkeeping.</summary>
public sealed record GoalSnapshot(string Objective, string Status /*active|paused|blocked|complete*/, int RoundsStarted, int MaxRounds, string? BlockedReason, long UpdatedAt);

public static class GoalStatus
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Blocked = "blocked";
    public const string Complete = "complete";
}

/// <summary>Durable payload of a goal/change event, camelCase via SessionJson; "clear" carries no goal.</summary>
public sealed record GoalChangePayload(int Version, string Operation, GoalSnapshot? Goal, long At);

/// <summary>
/// ctx.goals — the long-running objective, folded from goal/change events. Every event carries
/// the full post-change snapshot, so edit is partial-update semantics computed server-side.
/// </summary>
public sealed class GoalService
{
    public const string ServiceKey = "goals";
    public const int PayloadVersion = 1;
    public const int DefaultMaxRounds = 5;

    /// <summary>Folds the goal from goal/change events in order; "clear" empties, latest wins.</summary>
    public static GoalSnapshot? Fold(Session session)
    {
        GoalSnapshot? goal = null;
        foreach (var e in session.Events)
        {
            if (e.Type != SessionEventTypes.GoalChange) continue;
            GoalChangePayload change;
            try
            {
                change = SessionJson.FromElement<GoalChangePayload>(e.Data);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                continue; // tolerate malformed or foreign payloads
            }
            if (change.Version != PayloadVersion) continue;
            goal = change.Goal;
        }
        return goal;
    }

    /// <summary>The goal when its status is active, else null.</summary>
    public static GoalSnapshot? Active(Session session)
        => Fold(session) is { Status: GoalStatus.Active } active ? active : null;

    /// <summary>Registers the objective; the creating turn consumes the first round.</summary>
    public static GoalSnapshot Create(Session session, string objective, int maxRounds)
    {
        if (Fold(session) is not null)
            throw new HarnessException("GOAL_EXISTS", "a goal already exists; complete or update it instead of creating another");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var goal = new GoalSnapshot(objective, GoalStatus.Active, RoundsStarted: 1, maxRounds, BlockedReason: null, UpdatedAt: now);
        session.Append(SessionEventTypes.GoalChange, new GoalChangePayload(PayloadVersion, "create", goal, now));
        return goal;
    }

    /// <summary>Applies an operation (edit|pause|resume|complete|block) and appends the merged snapshot.</summary>
    public static GoalSnapshot Update(Session session, string operation, string? objective = null, string? reason = null)
    {
        var current = Fold(session) ?? throw new HarnessException("NO_GOAL", "no goal to update; create one with create_goal first");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var next = operation switch
        {
            "edit" => current with { Objective = objective ?? current.Objective, BlockedReason = reason ?? current.BlockedReason, UpdatedAt = now },
            "pause" => current with { Status = GoalStatus.Paused, UpdatedAt = now },
            "resume" => current with { Status = GoalStatus.Active, BlockedReason = null, UpdatedAt = now },
            "complete" => current with { Status = GoalStatus.Complete, UpdatedAt = now },
            "block" => current with { Status = GoalStatus.Blocked, BlockedReason = reason, UpdatedAt = now },
            _ => throw new HarnessException("INVALID_OPERATION", $"unknown goal operation '{operation}'"),
        };
        session.Append(SessionEventTypes.GoalChange, new GoalChangePayload(PayloadVersion, operation, next, now));
        return next;
    }

    /// <summary>Consumes one continuation round (a goal/change edit); used by the turn-stopping driver.</summary>
    internal static GoalSnapshot AdvanceRound(Session session, GoalSnapshot goal)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var next = goal with { RoundsStarted = goal.RoundsStarted + 1, UpdatedAt = now };
        session.Append(SessionEventTypes.GoalChange, new GoalChangePayload(PayloadVersion, "edit", next, now));
        return next;
    }
}

internal static class GoalSchemas
{
    internal static JsonSchema.Schema Snapshot() => JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["objective"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(values:
            [
                JsonSerializer.SerializeToElement(GoalStatus.Active),
                JsonSerializer.SerializeToElement(GoalStatus.Paused),
                JsonSerializer.SerializeToElement(GoalStatus.Blocked),
                JsonSerializer.SerializeToElement(GoalStatus.Complete),
            ]),
            ["roundsStarted"] = JsonSchema.Integer(),
            ["maxRounds"] = JsonSchema.Integer(),
            ["blockedReason"] = JsonSchema.String(),
            ["updatedAt"] = JsonSchema.Integer(),
        },
        required: ["objective", "status", "roundsStarted", "maxRounds", "updatedAt"]);
}

internal static class GoalToolCommon
{
    internal static void RequireAgent(ToolRunContext exec)
    {
        if (exec.Agent is null) throw new ToolException("NO_AGENT", "goal tools require an owning agent");
    }
}

public sealed record CreateGoalArgs(string Objective, [property: System.Text.Json.Serialization.JsonPropertyName("max_goal_rounds")] int? MaxGoalRounds = null);

/// <summary>create_goal: registers the session objective as a durable goal/change event.</summary>
public sealed class CreateGoalTool : ToolDefinition<CreateGoalArgs, GoalSnapshot>
{
    public override string Name => "create_goal";

    public override string Description =>
        "Register a long-running objective for this session. While the goal is active the harness "
        + "starts further rounds toward it (bounded by max_goal_rounds) until you complete or block it.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["objective"] = JsonSchema.String("The objective to keep working toward."),
            ["max_goal_rounds"] = JsonSchema.Integer("Total rounds the harness may spend on the goal, including the current one. Defaults to 5."),
        },
        required: ["objective"]);

    public override JsonSchema.Schema Output { get; } = GoalSchemas.Snapshot();

    protected override Task<GoalSnapshot> ExecuteTyped(CreateGoalArgs args, ToolRunContext exec)
    {
        GoalToolCommon.RequireAgent(exec);
        var objective = args.Objective.Trim();
        if (objective.Length == 0) throw new ToolException("INVALID_ARGS", "objective must be non-empty");
        var maxRounds = args.MaxGoalRounds ?? GoalService.DefaultMaxRounds;
        if (maxRounds < 1) throw new ToolException("INVALID_ARGS", "max_goal_rounds must be at least 1");
        return Task.FromResult(GoalService.Create(exec.Session, objective, maxRounds));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(CreateGoalArgs args, GoalSnapshot value)
        => [new TextBlock(
            $"Goal created: \"{value.Objective}\" (round {value.RoundsStarted} of up to {value.MaxRounds}). "
            + "Call update_goal with operation \"complete\" when done, or \"block\" with a reason if you cannot proceed.")];

    protected override ToolCallView? PresentCallTyped(CreateGoalArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Create goal",
        Description = args.Objective,
    };
}

public sealed record GetGoalArgs;

public sealed record GetGoalOutput(GoalSnapshot? Goal);

/// <summary>get_goal: reads the folded goal snapshot (or none).</summary>
public sealed class GetGoalTool : ToolDefinition<GetGoalArgs, GetGoalOutput>
{
    public override string Name => "get_goal";

    public override string Description =>
        "Read the current goal (objective, status, rounds started, round cap, blocked reason), or confirm none is set.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema> { ["goal"] = GoalSchemas.Snapshot() });

    protected override bool IsConcurrencySafeTyped(GetGoalArgs args) => true;

    protected override Task<GetGoalOutput> ExecuteTyped(GetGoalArgs args, ToolRunContext exec)
    {
        GoalToolCommon.RequireAgent(exec);
        return Task.FromResult(new GetGoalOutput(GoalService.Fold(exec.Session)));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(GetGoalArgs args, GetGoalOutput value)
    {
        if (value.Goal is not { } goal) return [new TextBlock("No goal is set; create one with create_goal.")];
        var blocked = goal.BlockedReason is { Length: > 0 } reason ? $" — blocked: {reason}" : "";
        return [new TextBlock($"Goal: \"{goal.Objective}\" — {goal.Status} ({goal.RoundsStarted}/{goal.MaxRounds} rounds){blocked}.")];
    }
}

public sealed record UpdateGoalArgs(string Operation, string? Objective = null, string? Reason = null);

/// <summary>update_goal: applies an operation and appends the merged goal/change snapshot.</summary>
public sealed class UpdateGoalTool : ToolDefinition<UpdateGoalArgs, GoalSnapshot>
{
    public override string Name => "update_goal";

    public override string Description =>
        "Update the goal: \"edit\" merges a new objective (and optionally a note), \"pause\" halts continuation, "
        + "\"resume\" reactivates, \"complete\" marks it done, \"block\" records why you cannot proceed.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["operation"] = JsonSchema.String("The change to apply.", values:
            [
                JsonSerializer.SerializeToElement("edit"),
                JsonSerializer.SerializeToElement("pause"),
                JsonSerializer.SerializeToElement("resume"),
                JsonSerializer.SerializeToElement("complete"),
                JsonSerializer.SerializeToElement("block"),
            ]),
            ["objective"] = JsonSchema.String("Replacement objective (edit only)."),
            ["reason"] = JsonSchema.String("Why the goal cannot proceed (block), or a note (edit)."),
        },
        required: ["operation"]);

    public override JsonSchema.Schema Output { get; } = GoalSchemas.Snapshot();

    protected override Task<GoalSnapshot> ExecuteTyped(UpdateGoalArgs args, ToolRunContext exec)
    {
        GoalToolCommon.RequireAgent(exec);
        if (args.Operation == "block" && string.IsNullOrWhiteSpace(args.Reason))
            throw new ToolException("INVALID_ARGS", "block requires a reason");
        var objective = args.Objective?.Trim();
        if (objective is { Length: 0 }) throw new ToolException("INVALID_ARGS", "objective must be non-empty");
        if (GoalService.Fold(exec.Session) is null)
            throw new ToolException("NO_GOAL", "no goal exists; create one with create_goal first");
        return Task.FromResult(GoalService.Update(exec.Session, args.Operation, objective, args.Reason));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(UpdateGoalArgs args, GoalSnapshot value) => [new TextBlock(args.Operation switch
    {
        "pause" => $"Goal paused: \"{value.Objective}\". Continuation stops until update_goal operation \"resume\".",
        "resume" => $"Goal resumed: \"{value.Objective}\" ({value.RoundsStarted}/{value.MaxRounds} rounds).",
        "complete" => $"Goal complete: \"{value.Objective}\". Continuation stops.",
        "block" => $"Goal blocked: {value.BlockedReason}. Continuation stops until it is resumed.",
        _ => $"Goal updated: \"{value.Objective}\" ({value.RoundsStarted}/{value.MaxRounds} rounds).",
    })];

    protected override ToolCallView? PresentCallTyped(UpdateGoalArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Update goal",
        Description = args.Operation,
    };
}

/// <summary>Mounts the goals family: create/get/update_goal tools, the goal prompt section, and the continuation driver.</summary>
public sealed class GoalPlugin : HarnessPlugin
{
    public override string Name => "goals";
    public override string[] Inject { get; } = ["tools"];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        ctx.Provide(GoalService.ServiceKey, new GoalService());

        var tools = ctx.Get<ToolRuntime>("tools");
        ctx.Effect(tools.Register(new CreateGoalTool()).Dispose);
        ctx.Effect(tools.Register(new GetGoalTool()).Dispose);
        ctx.Effect(tools.Register(new UpdateGoalTool()).Dispose);

        var prompt = ctx.Get<SystemPromptService>("systemPrompt");
        var section = prompt.RegisterSection("goal", 103, _ =>
            "Long-running objectives: register one with create_goal (max_goal_rounds bounds autonomous continuation), "
            + "inspect it with get_goal, and manage it with update_goal (edit/pause/resume/complete/block). "
            + "While a goal is active the harness keeps starting rounds toward it; complete it as soon as the objective is met.");
        ctx.Effect(section.Dispose);

        ctx.Effect(ctx.Events.On<TurnStoppingEvent>("agent/turn-stopping", ContinueGoalAsync).Dispose);
        return Task.CompletedTask;
    }

    /// <summary>At each turn boundary an active goal with rounds left steers one more round.</summary>
    private static Task ContinueGoalAsync(TurnStoppingEvent e, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.CompletedTask;
        var goal = GoalService.Fold(e.Agent.Session);
        if (goal is not { Status: GoalStatus.Active } || goal.RoundsStarted >= goal.MaxRounds)
            return Task.CompletedTask;
        var round = GoalService.AdvanceRound(e.Agent.Session, goal);
        e.Agent.Steer(Message.CreateUserText(
            $"Goal round {round.RoundsStarted}/{round.MaxRounds}: continue working toward: {round.Objective}. "
            + "Call update_goal with operation \"complete\" when done, or \"block\" with a reason if you cannot proceed."));
        return Task.CompletedTask;
    }
}
