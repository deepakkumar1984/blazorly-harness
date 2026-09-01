using System.Text.Json;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

/// <summary>Durable payload of a plan/mode event, camelCase via SessionJson.</summary>
public sealed record PlanModePayload(bool Active);

/// <summary>ctx.planMode — the plan-mode flag folded from plan/mode events (latest wins).</summary>
public sealed class PlanModeService
{
    public const string ServiceKey = "planMode";

    /// <summary>True when the latest plan/mode event marks plan mode active.</summary>
    public bool IsActive(Session session)
    {
        var events = session.Events;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Type != SessionEventTypes.PlanMode) continue;
            try
            {
                return SessionJson.FromElement<PlanModePayload>(events[i].Data).Active;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Appends the durable plan/mode event that sets the mode.</summary>
    public void SetActive(Session session, bool active) => Toggle(session, active);

    /// <summary>Orchestrator helper (e.g. a /plan command): flip plan mode without resolving the service.</summary>
    public static void Toggle(Session session, bool active)
        => session.Append(SessionEventTypes.PlanMode, new PlanModePayload(active));
}

public sealed record ExitPlanModeArgs(string Plan);

public sealed record ExitPlanModeOutput(bool Approved, bool PlanModeActive);

/// <summary>
/// exit_plan_mode: presents the complete plan for approval while plan mode is active. Approval
/// exits plan mode; "keep planning" is user feedback, not an error.
/// </summary>
public sealed class ExitPlanModeTool(PlanModeService planMode) : ToolDefinition<ExitPlanModeArgs, ExitPlanModeOutput>
{
    public override string Name => "exit_plan_mode";

    public override string Description =>
        "Present the complete plan (markdown) for user approval while plan mode is active. Approval exits "
        + "plan mode and re-enables mutations; if the user wants to keep planning, revise and present again.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["plan"] = JsonSchema.String("The complete plan in markdown. This is exactly what the user reviews."),
        },
        required: ["plan"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["approved"] = JsonSchema.Boolean(),
            ["planModeActive"] = JsonSchema.Boolean(),
        },
        required: ["approved", "planModeActive"]);

    protected override async Task<ExitPlanModeOutput> ExecuteTyped(ExitPlanModeArgs args, ToolRunContext exec)
    {
        if (exec.Agent is null) throw new ToolException("NO_AGENT", "this tool requires an owning agent");
        if (!planMode.IsActive(exec.Session))
            throw new ToolException("NOT_IN_PLAN_MODE", "plan mode is not active; investigate and present a plan only after plan mode is turned on");

        var questions = exec.Agent.Ctx.TryGet<UserQuestionsService>(UserQuestionsService.ServiceKey)
            ?? throw new ToolException("NO_USER_QUESTIONS_PROVIDER", "no user-questions service is mounted; the plan cannot be reviewed");
        var question = new AskQuestion(
            Id: "plan",
            Question: "Approve this plan?",
            Header: "Plan review",
            Options: [new AskOption("Approve and proceed (Recommended)"), new AskOption("Keep planning")]);
        IReadOnlyList<AskAnswer> answers;
        try
        {
            answers = await questions.AskAsync([question], exec.Signal).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            throw new ToolException(ex.Code, ex.Message);
        }

        var answer = answers.FirstOrDefault(a => a.Id == question.Id);
        var approved = answer is not null && answer.Text.Contains("Approve", StringComparison.OrdinalIgnoreCase);
        if (!approved) return new ExitPlanModeOutput(Approved: false, PlanModeActive: true);
        planMode.SetActive(exec.Session, false);
        return new ExitPlanModeOutput(Approved: true, PlanModeActive: false);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(ExitPlanModeArgs args, ExitPlanModeOutput value)
        => value.Approved
            ? [new TextBlock("Plan approved. Proceed with the plan.")]
            : [new TextBlock("The user wants to keep planning; revise the plan and present again.")];

    protected override ToolCallView? PresentCallTyped(ExitPlanModeArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Exit plan mode",
        Description = $"plan review ({args.Plan.Length} chars)",
    };
}

/// <summary>Mounts plan mode: exit_plan_mode, the mutation guard, and the plan-mode prompt section.</summary>
public sealed class PlanModePlugin : HarnessPlugin
{
    /// <summary>Tool names denied by the guard while plan mode is active.</summary>
    public static IReadOnlySet<string> MutationTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "bash", "write", "edit", "run_code", "terminal_send", "terminal_open",
    };

    public override string Name => "plan-mode";
    public override string[] Inject { get; } = ["tools", "userQuestions"];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var service = new PlanModeService();
        ctx.Provide(PlanModeService.ServiceKey, service);

        var tools = ctx.Get<ToolRuntime>("tools");
        ctx.Effect(tools.Register(new ExitPlanModeTool(service)).Dispose);
        ctx.Effect(tools.AddGuard(exec =>
        {
            if (exec.Agent is null) return null;
            if (!service.IsActive(exec.Agent.Session)) return null;
            return MutationTools.Contains(exec.Name)
                ? "plan mode is active — present your plan with exit_plan_mode before mutating"
                : null;
        }).Dispose);

        var prompt = ctx.Get<SystemPromptService>("systemPrompt");
        var section = prompt.RegisterSection("plan-mode", 104, context =>
        {
            if (context.Agent is null) return "";
            return service.IsActive(context.Agent.Session)
                ? "PLAN MODE ACTIVE: do not mutate anything; investigate, then present the complete plan via exit_plan_mode."
                : "exit_plan_mode is available for plan-first workflows.";
        });
        ctx.Effect(section.Dispose);
        return Task.CompletedTask;
    }
}
