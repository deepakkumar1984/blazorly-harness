using System.Text.Json;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using static Blazorly.Harness.Tests.Scripted;

namespace Blazorly.Harness.Tests;

/// <summary>A complex brief used across auto-plan tests — safely above the threshold
/// (length + sequencing + scope verbs + multi-entity + @files + numbered steps).</summary>
public static class AutoPlanBriefs
{
    public const string Complex =
        """
        Refactor the persistence layer end to end.
        1) Map every module that touches the session log
        2) Migrate the writers to the new append path
        3) Rewrite the compaction pass
        4) Overhaul the tests across all suites to match

        Files in scope: @src/Persistence @src/Tests @src/Core. First audit the current
        behavior, then restructure the code, and finally re-run every test suite before
        wrapping up. Keep observable behavior identical throughout the change.
        """;

    public const string Trivial = "fix the typo on line 3 of readme";
}

public class ComplexityScorerTests
{
    [Fact]
    public void ComplexBrief_ScoresAboveThreshold_WithReasons()
    {
        var score = ComplexityScorer.Score(AutoPlanBriefs.Complex);
        Assert.True(score.Total >= 55, $"expected >=55, got {score.Total}: {string.Join("; ", score.Reasons)}");
        Assert.Contains(score.Reasons, r => r.StartsWith("scope:", StringComparison.Ordinal));
        Assert.Contains(score.Reasons, r => r.StartsWith("sequencing", StringComparison.Ordinal));
    }

    [Fact]
    public void TrivialBrief_ScoresLow()
    {
        var score = ComplexityScorer.Score(AutoPlanBriefs.Trivial);
        Assert.True(score.Total < 55, $"expected <55, got {score.Total}");
        Assert.DoesNotContain(score.Reasons, r => r.StartsWith("scope:", StringComparison.Ordinal));
    }

    [Fact]
    public void QuestionBrief_IsCapped_EvenWhenLongAndBroad()
    {
        var padded = "How does the pipeline work across all the modules? " + new string('x', 700);
        var score = ComplexityScorer.Score(padded);
        Assert.True(score.Total <= 30, $"expected cap at 30, got {score.Total}");
        Assert.Contains(score.Reasons, r => r.Contains("question", StringComparison.Ordinal));
    }

    [Fact]
    public void FileReferences_AndNumberedSteps_Contribute()
    {
        var score = ComplexityScorer.Score("rework @a.txt and @b.txt\n1) one\n2) two\n3) three");
        Assert.Contains(score.Reasons, r => r.Contains("@file", StringComparison.Ordinal));
        Assert.Contains(score.Reasons, r => r.StartsWith("numbered", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyText_ScoresZero() => Assert.Equal(0, ComplexityScorer.Score("   ").Total);

    [Fact]
    public void EvalTaskPrompts_AllScoreBelowThreshold()
    {
        // auto-plan must never hijack the deterministic eval corpus
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "tasks"))) dir = dir.Parent;
        Assert.NotNull(dir);
        foreach (var taskJson in Directory.GetFiles(Path.Combine(dir!.FullName, "eval", "tasks"), "task.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(taskJson));
            var prompt = doc.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() : null;
            if (prompt is null) continue;
            var score = ComplexityScorer.Score(prompt);
            Assert.True(score.Total < 55, $"{taskJson}: eval prompt scored {score.Total} ({string.Join("; ", score.Reasons)})");
        }
    }
}

public class AutoPlanPolicyTests
{
    private static (TestHarness Harness, Agent Agent) Boot()
    {
        var harness = TestHarness.Create();
        return (harness, harness.CreateAgent(Path.Combine(Path.GetTempPath(), "blazorly-autoplan-" + Guid.NewGuid().ToString("N")[..8])));
    }

    [Fact]
    public void HighBrief_Engages()
    {
        var (_, agent) = Boot();
        var engaged = AutoPlanPolicy.ShouldEngage(agent.Session, [Message.CreateUserText(AutoPlanBriefs.Complex)], 55, out var score);
        Assert.True(engaged);
        Assert.True(score.Total >= 55);
    }

    [Fact]
    public void LowBrief_DoesNotEngage()
    {
        var (_, agent) = Boot();
        Assert.False(AutoPlanPolicy.ShouldEngage(agent.Session, [Message.CreateUserText(AutoPlanBriefs.Trivial)], 55, out _));
    }

    [Fact]
    public void SubagentSessions_NeverEngage()
    {
        var (_, agent) = Boot();
        agent.Session.Append(SessionEventTypes.SubagentDescriptor, new { }, new Session.AppendOptions(Ignorable: true));
        Assert.False(AutoPlanPolicy.ShouldEngage(agent.Session, [Message.CreateUserText(AutoPlanBriefs.Complex)], 55, out _));
    }

    [Fact]
    public void ActiveGoalSessions_NeverEngage()
    {
        var (_, agent) = Boot();
        agent.Session.Append(SessionEventTypes.GoalChange,
            new GoalChangePayload(1, "create", new GoalSnapshot("ship it", GoalStatus.Active, 1, 5, null, 0), 0));
        Assert.False(AutoPlanPolicy.ShouldEngage(agent.Session, [Message.CreateUserText(AutoPlanBriefs.Complex)], 55, out _));
    }

    [Fact]
    public void FollowUpAfterApproval_InSameArc_DoesNotEngage()
    {
        var (_, agent) = Boot();
        agent.Session.Append(SessionEventTypes.UserMessage, Message.CreateUserText(AutoPlanBriefs.Complex),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append())); // previous turn's prompt
        PlanModeService.Toggle(agent.Session, true, auto: true, score: 70, reasons: ["test"]);
        PlanModeService.Toggle(agent.Session, false); // approval lifted the mode during that turn
        Assert.False(AutoPlanPolicy.ShouldEngage(agent.Session, [Message.CreateUserText("now write the comprehensive test suite for it")], 55, out _));
    }

    [Fact]
    public void FreshArc_AfterFollowUp_EngagesAgain()
    {
        var (_, agent) = Boot();
        agent.Session.Append(SessionEventTypes.UserMessage, Message.CreateUserText(AutoPlanBriefs.Complex),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        PlanModeService.Toggle(agent.Session, true, auto: true);
        PlanModeService.Toggle(agent.Session, false);
        agent.Session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("ok, looks good"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append())); // follow-up turn happened
        Assert.True(AutoPlanPolicy.ShouldEngage(agent.Session, [Message.CreateUserText(AutoPlanBriefs.Complex)], 55, out _));
    }

    [Fact]
    public void LegacyPlanModePayloads_StillFold()
    {
        var (_, agent) = Boot();
        agent.Session.Append(SessionEventTypes.PlanMode, new { active = true }); // pre-auto event shape
        var service = new PlanModeService();
        Assert.True(service.IsActive(agent.Session));
        var latest = service.Latest(agent.Session);
        Assert.NotNull(latest);
        Assert.Null(latest!.Auto);
        Assert.Null(latest.Reasons);
    }

    [Fact]
    public void ToolResultMessages_AreNotScoredAsHumanBriefs()
    {
        var (_, agent) = Boot();
        var toolResult = Message.CreateToolResult("call_1", [new TextBlock("{}")]);
        Assert.False(AutoPlanPolicy.ShouldEngage(agent.Session, [toolResult], 0, out var score));
        Assert.Equal(0, score.Total);
    }
}

public class AutoPlanE2eTests
{
    private static string TempDir() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "blazorly-autoplan-e2e-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    [Fact]
    public async Task ComplexBrief_AutoEngages_GuardsBlocks_ThenApprovalUnlocks()
    {
        var dir = TempDir();
        var request = 0;
        await using var harness = TestHarness.Create(options =>
        {
            if (options.Purpose == "session-title") return Scripted.Text("t");
            request++;
            return request switch
            {
                1 => Scripted.ToolCall("bash", new { command = "echo hi", description = "try to mutate" }),
                2 => Scripted.ToolCall("exit_plan_mode", new { plan = "# Plan\n1. Ship it" }),
                3 => Scripted.ToolCall("bash", new { command = "echo hi > " + Path.Combine(dir, "proof.txt"), description = "mutate after approval" }),
                _ => Scripted.Text("done"),
            };
        });
        var questions = UserQuestionsService.Mount(harness.Ctx);
        questions.SetProvider((list, ct) =>
            Task.FromResult<IReadOnlyList<AskAnswer>>([new AskAnswer("plan", "Approve and proceed")]));
        new PlanModePlugin().Apply(harness.Ctx);
        new AutoPlanPlugin().Apply(harness.Ctx);
        try
        {
            var agent = harness.CreateAgent(dir);
            agent.Followup(Message.CreateUserText(AutoPlanBriefs.Complex));
            await agent.WhenIdleAsync();

            var events = agent.Session.Events;

            // 1. auto-engaged before the first model call, with score + reasons
            var engage = events.Single(e => e.Type == SessionEventTypes.PlanMode
                && e.Data.TryGetProperty("auto", out var auto) && auto.GetBoolean());
            Assert.True(engage.Data.GetProperty("active").GetBoolean());
            Assert.True(engage.Data.GetProperty("score").GetInt32() >= 55);
            Assert.True(engage.Seq < events.First(e => e.Type == SessionEventTypes.StepStart).Seq);

            // 2. the guard blocked the first mutation
            var blocked = events.Where(e => e.Type == SessionEventTypes.ToolResult)
                .Select(e => e.Data.GetRawText())
                .Any(t => t.Contains("plan mode is active", StringComparison.Ordinal));
            Assert.True(blocked);

            // 3. approval lifted the mode and the follow-up mutation ran
            Assert.Contains(events, e => e.Type == SessionEventTypes.PlanMode && !e.Data.GetProperty("active").GetBoolean());
            Assert.True(File.Exists(Path.Combine(dir, "proof.txt")));

            // 4. the turn finished cleanly
            Assert.Contains(events, e => e.Type == SessionEventTypes.TurnEnd);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TrivialBrief_RunsStraightThrough_NoPlanEvents()
    {
        var dir = TempDir();
        var request = 0;
        await using var harness = TestHarness.Create(options =>
        {
            if (options.Purpose == "session-title") return Scripted.Text("t");
            request++;
            return request == 1
                ? Scripted.ToolCall("bash", new { command = "echo hi > " + Path.Combine(dir, "ok.txt"), description = "write the proof file" })
                : Scripted.Text("done");
        });
        new PlanModePlugin().Apply(harness.Ctx);
        new AutoPlanPlugin().Apply(harness.Ctx);
        try
        {
            var agent = harness.CreateAgent(dir);
            agent.Followup(Message.CreateUserText(AutoPlanBriefs.Trivial));
            await agent.WhenIdleAsync();

            Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.PlanMode);
            Assert.True(File.Exists(Path.Combine(dir, "ok.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ManualPlanMode_IsNeverReEngagedOrLiftedByAutoPlan()
    {
        await using var harness = TestHarness.Create(options => Scripted.Text("fine"));
        new PlanModePlugin().Apply(harness.Ctx);
        new AutoPlanPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent(Path.Combine(Path.GetTempPath(), "blazorly-autoplan-" + Guid.NewGuid().ToString("N")[..8]));

        PlanModeService.Toggle(agent.Session, true); // manual /plan
        agent.Followup(Message.CreateUserText(AutoPlanBriefs.Complex));
        await agent.WhenIdleAsync();

        // still exactly one plan/mode event: the manual one — auto-plan respected it
        var planEvents = agent.Session.Events.Where(e => e.Type == SessionEventTypes.PlanMode).ToList();
        Assert.Single(planEvents);
        Assert.False(planEvents[0].Data.TryGetProperty("auto", out _) && planEvents[0].Data.GetProperty("auto").GetBoolean());
    }
}
