using System.Text.Json;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

public class GoalTests
{
    private static ToolExecutionInput Input(Agent agent, string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args, SessionJson.Options),
        CallId = $"call_{name}",
        Agent = agent,
    };

    [Fact]
    public async Task CreateGoal_AppendsChangeEventAndFoldsActive()
    {
        await using var harness = TestHarness.Create();
        new GoalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(agent, "create_goal", new { objective = "ship the goals feature", max_goal_rounds = 3 }));
        Assert.False(result.IsError);
        Assert.Contains("Goal created", Assert.IsType<TextBlock>(result.Content.Single()).Text);

        var change = Assert.Single(agent.Session.Events, e => e.Type == SessionEventTypes.GoalChange);
        Assert.Equal("create", change.Data.GetProperty("operation").GetString());
        Assert.Equal(1, change.Data.GetProperty("version").GetInt32());
        var active = GoalService.Active(agent.Session);
        Assert.NotNull(active);
        Assert.Equal("ship the goals feature", active!.Objective);
        Assert.Equal(GoalStatus.Active, active.Status);
        Assert.Equal(1, active.RoundsStarted);
        Assert.Equal(3, active.MaxRounds);
        Assert.Null(active.BlockedReason);

        // omitted cap defaults to 5; the goal section rides the system prompt
        var other = harness.CreateAgent();
        Assert.False((await harness.Tools.Execute(Input(other, "create_goal", new { objective = "default cap" }))).IsError);
        Assert.Equal(GoalService.DefaultMaxRounds, GoalService.Active(other.Session)!.MaxRounds);
        Assert.Contains(harness.Prompt.Assemble(agent, null).Sections, s => s.Name == "goal" && s.Text.Contains("create_goal"));
    }

    [Fact]
    public async Task UpdateGoal_TransitionsThroughPauseBlockResumeEditComplete()
    {
        await using var harness = TestHarness.Create();
        new GoalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();
        await harness.Tools.Execute(Input(agent, "create_goal", new { objective = "write docs", max_goal_rounds = 5 }));

        Assert.False((await harness.Tools.Execute(Input(agent, "update_goal", new { operation = "pause" }))).IsError);
        Assert.Null(GoalService.Active(agent.Session));
        Assert.Equal(GoalStatus.Paused, GoalService.Fold(agent.Session)!.Status);

        Assert.False((await harness.Tools.Execute(Input(agent, "update_goal", new { operation = "block", reason = "missing credentials" }))).IsError);
        var blocked = GoalService.Fold(agent.Session)!;
        Assert.Equal(GoalStatus.Blocked, blocked.Status);
        Assert.Equal("missing credentials", blocked.BlockedReason);

        Assert.False((await harness.Tools.Execute(Input(agent, "update_goal", new { operation = "resume" }))).IsError);
        var resumed = GoalService.Active(agent.Session);
        Assert.NotNull(resumed);
        Assert.Null(resumed!.BlockedReason);

        Assert.False((await harness.Tools.Execute(Input(agent, "update_goal", new { operation = "edit", objective = "write the docs and tests" }))).IsError);
        Assert.Equal("write the docs and tests", GoalService.Active(agent.Session)!.Objective);

        Assert.False((await harness.Tools.Execute(Input(agent, "update_goal", new { operation = "complete" }))).IsError);
        Assert.Null(GoalService.Active(agent.Session));
        Assert.Equal(GoalStatus.Complete, GoalService.Fold(agent.Session)!.Status);
    }

    [Fact]
    public async Task BlockWithoutReason_IsRejected()
    {
        await using var harness = TestHarness.Create();
        new GoalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();
        await harness.Tools.Execute(Input(agent, "create_goal", new { objective = "needs a reason to block" }));

        var result = await harness.Tools.Execute(Input(agent, "update_goal", new { operation = "block" }));
        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.InvalidArgs, result.Error!.Info!.Code);
    }

    [Fact]
    public async Task ActiveGoal_SteersASecondStepAndAdvancesRound()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("ok"));
        new GoalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();
        await harness.Tools.Execute(Input(agent, "create_goal", new { objective = "write the report", max_goal_rounds = 2 }));

        agent.Followup(Message.CreateUserText("start"));
        await agent.WhenIdleAsync();

        Assert.Equal(2, agent.Session.Events.Count(e => e.Type == SessionEventTypes.StepStart));
        var goal = GoalService.Fold(agent.Session);
        Assert.Equal(GoalStatus.Active, goal!.Status);
        Assert.Equal(2, goal.RoundsStarted); // the continuation driver consumed the second round
        Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.UserMessage
            && SessionEventRead.MessageOf(e).FlattenText().Contains("Goal round 2/2: continue working toward: write the report"));
    }

    [Fact]
    public async Task CappedGoal_DoesNotSteer()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("ok"));
        new GoalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();
        await harness.Tools.Execute(Input(agent, "create_goal", new { objective = "one shot", max_goal_rounds = 1 }));

        agent.Followup(Message.CreateUserText("start"));
        await agent.WhenIdleAsync();

        Assert.Equal(1, agent.Session.Events.Count(e => e.Type == SessionEventTypes.StepStart));
        Assert.Equal(1, GoalService.Fold(agent.Session)!.RoundsStarted);
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.UserMessage
            && SessionEventRead.MessageOf(e).FlattenText().Contains("Goal round"));
    }
}

public class PlanModeTests
{
    private static ToolExecutionInput Input(Agent agent, string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args, SessionJson.Options),
        CallId = $"call_{name}",
        Agent = agent,
    };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blazorly-goalsplan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Guard_DeniesMutationsWhileActiveAndAllowsAfterToggle()
    {
        await using var harness = TestHarness.Create();
        new PlanModePlugin().Apply(harness.Ctx);
        var planMode = harness.Ctx.Get<PlanModeService>("planMode");
        var dir = TempDir();
        try
        {
            var agent = harness.CreateAgent(dir);
            PlanModeService.Toggle(agent.Session, true);
            Assert.True(planMode.IsActive(agent.Session));
            Assert.Contains(harness.Prompt.Assemble(agent, null).Sections, s => s.Name == "plan-mode" && s.Text.Contains("PLAN MODE ACTIVE"));

            var denied = await harness.Tools.Execute(Input(agent, "write", new { file_path = "guarded.txt", content = "nope" }));
            Assert.True(denied.IsError);
            Assert.Equal(ToolErrorCodes.Denied, denied.Error!.Info!.Code);
            Assert.Contains("plan mode", denied.Error.Message);
            Assert.False(File.Exists(Path.Combine(dir, "guarded.txt")));

            PlanModeService.Toggle(agent.Session, false);
            Assert.False(planMode.IsActive(agent.Session));
            Assert.False((await harness.Tools.Execute(Input(agent, "write", new { file_path = "guarded.txt", content = "written" }))).IsError);
            Assert.True(File.Exists(Path.Combine(dir, "guarded.txt")));
            Assert.Contains(harness.Prompt.Assemble(agent, null).Sections, s => s.Name == "plan-mode" && s.Text.Contains("exit_plan_mode is available"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExitPlanMode_WhileInactive_ErrorsNotInPlanMode()
    {
        await using var harness = TestHarness.Create();
        new PlanModePlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(agent, "exit_plan_mode", new { plan = "# Plan\n1. Do the thing" }));
        Assert.True(result.IsError);
        Assert.Equal("NOT_IN_PLAN_MODE", result.Error!.Info!.Code);
    }

    [Fact]
    public async Task ExitPlanMode_Approved_ExitsPlanModeAndLiftsGuard()
    {
        await using var harness = TestHarness.Create();
        var questions = UserQuestionsService.Mount(harness.Ctx);
        AskQuestion? asked = null;
        questions.SetProvider((list, ct) =>
        {
            asked = list.Single();
            return Task.FromResult<IReadOnlyList<AskAnswer>>([new AskAnswer("plan", "Approve and proceed")]);
        });
        new PlanModePlugin().Apply(harness.Ctx);
        var dir = TempDir();
        try
        {
            var agent = harness.CreateAgent(dir);
            PlanModeService.Toggle(agent.Session, true);

            var result = await harness.Tools.Execute(Input(agent, "exit_plan_mode", new { plan = "# Plan\n1. Ship it" }));
            Assert.False(result.IsError);
            Assert.Contains("Plan approved. Proceed with the plan.", Assert.IsType<TextBlock>(result.Content.Single()).Text);

            Assert.NotNull(asked);
            Assert.Equal("plan", asked!.Id);
            Assert.Equal("Plan review", asked.Header);
            Assert.Equal("Approve this plan?", asked.Question);
            Assert.NotNull(asked.Options);
            Assert.Contains(asked.Options!, o => o.Label == "Approve and proceed (Recommended)");
            Assert.Contains(asked.Options!, o => o.Label == "Keep planning");

            Assert.False(harness.Ctx.Get<PlanModeService>("planMode").IsActive(agent.Session));
            Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.PlanMode && !e.Data.GetProperty("active").GetBoolean());

            var write = await harness.Tools.Execute(Input(agent, "write", new { file_path = "after-approval.txt", content = "unlocked" }));
            Assert.False(write.IsError);
            Assert.True(File.Exists(Path.Combine(dir, "after-approval.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExitPlanMode_KeepPlanning_IsFeedbackNotError()
    {
        await using var harness = TestHarness.Create();
        var questions = UserQuestionsService.Mount(harness.Ctx);
        questions.SetProvider((list, ct) => Task.FromResult<IReadOnlyList<AskAnswer>>([new AskAnswer("plan", "Keep planning")]));
        new PlanModePlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();
        PlanModeService.Toggle(agent.Session, true);

        var result = await harness.Tools.Execute(Input(agent, "exit_plan_mode", new { plan = "# Plan" }));
        Assert.False(result.IsError);
        Assert.Contains("keep planning", Assert.IsType<TextBlock>(result.Content.Single()).Text);
        Assert.True(harness.Ctx.Get<PlanModeService>("planMode").IsActive(agent.Session));

        var stillDenied = await harness.Tools.Execute(Input(agent, "write", new { file_path = "still-guarded.txt", content = "nope" }));
        Assert.True(stillDenied.IsError);
        Assert.Contains("plan mode", stillDenied.Error!.Message);
    }

    [Fact]
    public async Task ExitPlanMode_WithoutQuestionsService_FailsClosed()
    {
        await using var harness = TestHarness.Create();
        new PlanModePlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();
        PlanModeService.Toggle(agent.Session, true);

        var result = await harness.Tools.Execute(Input(agent, "exit_plan_mode", new { plan = "# Plan" }));
        Assert.True(result.IsError);
        Assert.Equal("NO_USER_QUESTIONS_PROVIDER", result.Error!.Info!.Code);
    }
}
