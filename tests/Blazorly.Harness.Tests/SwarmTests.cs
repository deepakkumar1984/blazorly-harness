using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;

namespace Blazorly.Harness.Tests;

public class SwarmTests
{
    private static ToolExecutionInput Input(Agent agent, string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = $"call_{name}",
        Signal = CancellationToken.None,
        Agent = agent,
    };

    private static string Rendered(ToolExecutionResult result)
        => string.Join("\n", result.Content.OfType<TextBlock>().Select(b => b.Text));

    private static string Flatten(GenerateOptions options)
        => string.Concat(options.Messages.Select(m => m.FlattenText()));

    private const string PlannerJson = """{"tasks":[{"title":"alpha","prompt":"ALPHA-PROMPT do the first half"},{"title":"beta","prompt":"BETA-PROMPT do the second half"}]}""";
    private const string ReviewPassJson = """{"verdict":"pass","notes":"both tasks verified in the workspace","failed_tasks":[],"followups":[]}""";

    [Fact]
    public async Task Swarm_PlannerShardsFansOutJoinsAndReviews()
    {
        var calls = new List<GenerateOptions>();
        await using var harness = TestHarness.Create(options =>
        {
            calls.Add(options);
            var text = Flatten(options);
            if (text.Contains("Shard the objective")) return Scripted.Text(PlannerJson);
            if (text.Contains("ALPHA-PROMPT")) return Scripted.Text("alpha done");
            if (text.Contains("BETA-PROMPT")) return Scripted.Text("beta done");
            if (text.Contains("reviewer for a completed swarm")) return Scripted.Text(ReviewPassJson);
            return Scripted.Text("lead fallback");
        });
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(lead, "swarm", new { objective = "ship the release" }));
        Assert.False(result.IsError);

        var value = result.Value.GetValueOrDefault();
        Assert.Equal("complete", value.GetProperty("status").GetString());
        var tasks = value.GetProperty("tasks");
        Assert.Equal(2, tasks.GetArrayLength());
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("title").GetString() == "alpha" && t.GetProperty("summary").GetString() == "alpha done");
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("title").GetString() == "beta" && t.GetProperty("summary").GetString() == "beta done");
        Assert.All(tasks.EnumerateArray(), t => Assert.Equal("completed", t.GetProperty("status").GetString()));

        var reviews = value.GetProperty("reviews");
        var review = Assert.Single(reviews.EnumerateArray());
        Assert.Equal("pass", review.GetProperty("verdict").GetString());
        Assert.Contains("verified", review.GetProperty("notes").GetString());

        // planner + two workers + reviewer, all children of the lead at depth 1
        var children = subagents.ChildrenOf(lead.Id);
        Assert.Equal(4, children.Count);
        Assert.All(children, h => Assert.Equal(1, h.DelegationDepth));

        Assert.Contains("Final status: complete", Rendered(result));

        // workers saw the objective as context and their own task as scope
        var alpha = calls.Single(o => Flatten(o).Contains("ALPHA-PROMPT"));
        Assert.Contains("ship the release", Flatten(alpha));
        Assert.Contains("worker", Flatten(alpha));
    }

    [Fact]
    public async Task Swarm_ExplicitTasks_SkipsThePlanner()
    {
        await using var harness = TestHarness.Create(options =>
        {
            var text = Flatten(options);
            if (text.Contains("Shard the objective")) return Scripted.Text("""{"tasks":[{"title":"should not happen","prompt":"x"}]}""");
            if (text.Contains("ONLY-TASK")) return Scripted.Text("only task done");
            if (text.Contains("reviewer for a completed swarm")) return Scripted.Text(ReviewPassJson);
            return Scripted.Text("lead fallback");
        });
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(lead, "swarm", new
        {
            objective = "one thing only",
            tasks = new object[] { new { title = "solo", prompt = "ONLY-TASK the single task" } },
        }));
        Assert.False(result.IsError);

        var value = result.Value.GetValueOrDefault();
        Assert.Equal("complete", value.GetProperty("status").GetString());
        var task = Assert.Single(value.GetProperty("tasks").EnumerateArray());
        Assert.Equal("solo", task.GetProperty("title").GetString());
        Assert.Equal("only task done", task.GetProperty("summary").GetString());

        // one worker + one reviewer, no planner
        Assert.Equal(2, subagents.ChildrenOf(lead.Id).Count);
    }

    [Fact]
    public async Task Swarm_ReviewFailRedispatchesFailedTasksWithNotes_ThenPass()
    {
        var workerPrompts = new List<string>();
        var reviewerCalls = 0;
        await using var harness = TestHarness.Create(options =>
        {
            var text = Flatten(options);
            if (text.Contains("Shard the objective")) return Scripted.Text(PlannerJson);
            if (text.Contains("reviewer for a completed swarm"))
            {
                reviewerCalls++;
                return reviewerCalls == 1
                    ? Scripted.Text("""{"verdict":"fail","notes":"alpha output is empty","failed_tasks":["alpha"],"followups":[]}""")
                    : Scripted.Text(ReviewPassJson);
            }
            if (text.Contains("ALPHA-PROMPT"))
            {
                workerPrompts.Add(text);
                return Scripted.Text(workerPrompts.Count == 1 ? "alpha half-done" : "alpha fixed");
            }
            if (text.Contains("BETA-PROMPT")) return Scripted.Text("beta done");
            return Scripted.Text("lead fallback");
        });
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(lead, "swarm", new { objective = "ship it" }));
        Assert.False(result.IsError);

        var value = result.Value.GetValueOrDefault();
        Assert.Equal("complete", value.GetProperty("status").GetString());

        // alpha re-ran as attempt 2 carrying the reviewer notes; beta ran once
        Assert.Equal(2, workerPrompts.Count(t => t.Contains("ALPHA-PROMPT")));
        Assert.Contains(workerPrompts, p => p.Contains("A reviewer examined attempt 1") && p.Contains("alpha output is empty"));
        var alpha = value.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("title").GetString() == "alpha");
        Assert.Equal(2, alpha.GetProperty("attempt").GetInt32());
        Assert.Equal("alpha fixed", alpha.GetProperty("summary").GetString());
        var beta = value.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("title").GetString() == "beta");
        Assert.Equal(1, beta.GetProperty("attempt").GetInt32());

        // planner + 2 workers + retry worker + 2 reviewers
        Assert.Equal(6, subagents.ChildrenOf(lead.Id).Count);
        Assert.Equal(2, value.GetProperty("reviews").GetArrayLength());
    }

    [Fact]
    public async Task Swarm_ReviewRoundsExhausted_ReportsReviewFailed()
    {
        await using var harness = TestHarness.Create(options =>
        {
            var text = Flatten(options);
            if (text.Contains("Shard the objective")) return Scripted.Text(PlannerJson);
            if (text.Contains("reviewer for a completed swarm"))
                return Scripted.Text("""{"verdict":"fail","notes":"still wrong","failed_tasks":["alpha"],"followups":[]}""");
            if (text.Contains("ALPHA-PROMPT")) return Scripted.Text("alpha attempt");
            if (text.Contains("BETA-PROMPT")) return Scripted.Text("beta done");
            return Scripted.Text("lead fallback");
        });
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(lead, "swarm", new { objective = "ship it", max_review_rounds = 1 }));
        Assert.False(result.IsError);

        var value = result.Value.GetValueOrDefault();
        Assert.Equal("review-failed", value.GetProperty("status").GetString());
        Assert.Equal(2, value.GetProperty("reviews").GetArrayLength());
        Assert.Contains("Final status: review-failed", Rendered(result));
        Assert.Equal(6, subagents.ChildrenOf(lead.Id).Count); // planner + workers + retry + two reviewers
    }

    [Fact]
    public async Task Swarm_InvalidPlannerOutput_FailsClosed()
    {
        await using var harness = TestHarness.Create(options =>
            Flatten(options).Contains("Shard the objective")
                ? Scripted.Text("I would rather do it all myself.")
                : Scripted.Text("lead fallback"));
        SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(lead, "swarm", new { objective = "ship it" }));
        Assert.True(result.IsError);
        Assert.Equal("SWARM_PLAN_INVALID", result.Error!.Info!.Code);
    }

    [Fact]
    public async Task Swarm_ReviewDisabled_StatusComesFromFinishKinds()
    {
        var calls = new List<GenerateOptions>();
        await using var harness = TestHarness.Create(options =>
        {
            calls.Add(options);
            var text = Flatten(options);
            if (text.Contains("Shard the objective")) return Scripted.Text(PlannerJson);
            if (text.Contains("ALPHA-PROMPT")) return Scripted.Text("alpha done");
            if (text.Contains("BETA-PROMPT")) return Scripted.Text("beta done");
            return Scripted.Text("lead fallback");
        });
        SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(lead, "swarm", new { objective = "ship it", review = false }));
        Assert.False(result.IsError);

        var value = result.Value.GetValueOrDefault();
        Assert.Equal("complete", value.GetProperty("status").GetString());
        Assert.Equal(0, value.GetProperty("reviews").GetArrayLength());
        Assert.DoesNotContain(calls, o => Flatten(o).Contains("reviewer for a completed swarm"));
        Assert.Contains("Final status: complete", Rendered(result));
    }

    [Fact]
    public async Task ReviewTool_ForkedReviewerReturnsStructuredVerdict()
    {
        await using var harness = TestHarness.Create(options =>
        {
            var text = Flatten(options);
            if (text.Contains("Review the completed work"))
                return Scripted.Text("""{"verdict":"concerns","notes":"build passes, one test skipped","issues":[{"severity":"minor","location":"tests/Foo.cs:42","description":"skipped assertion"}],"followups":["unskip the test"]}""");
            return Scripted.Text("lead turn done");
        });
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        // the reviewer forks the lead's log: settle one turn first so the fork has a boundary
        lead.Followup(Message.CreateUserText("do some work"));
        await lead.WhenIdleAsync();

        var result = await harness.Tools.Execute(Input(lead, "review", new { scope = "the feature branch changes", focus = "tests" }));
        Assert.False(result.IsError);

        var value = result.Value.GetValueOrDefault();
        Assert.Equal("concerns", value.GetProperty("verdict").GetString());
        Assert.Contains("skipped", value.GetProperty("notes").GetString());
        var issue = Assert.Single(value.GetProperty("issues").EnumerateArray());
        Assert.Equal("minor", issue.GetProperty("severity").GetString());
        Assert.Equal("tests/Foo.cs:42", issue.GetProperty("location").GetString());
        Assert.Equal("unskip the test", Assert.Single(value.GetProperty("followups").EnumerateArray()).GetString());

        // the reviewer was a fork: a child session seeded with the lead's settled log
        var reviewerId = value.GetProperty("session_id").GetString();
        var reviewer = subagents.GetChild(reviewerId!);
        Assert.NotNull(reviewer);
        Assert.Equal(lead.Id, reviewer!.Session.Header.ParentSession);
        Assert.True(reviewer.Session.Header.SeedLength > 0);
        Assert.Contains("Reviewer verdict: concerns", Rendered(result));
    }

    [Fact]
    public async Task ReviewTool_UnparseableVerdict_ReportsDiagnostic()
    {
        await using var harness = TestHarness.Create(options =>
            Flatten(options).Contains("Review the completed work")
                ? Scripted.Text("looks fine to me")
                : Scripted.Text("lead turn done"));
        SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();
        lead.Followup(Message.CreateUserText("do some work"));
        await lead.WhenIdleAsync();

        var result = await harness.Tools.Execute(Input(lead, "review", new { scope = "the work" }));
        Assert.False(result.IsError);

        var value = result.Value.GetValueOrDefault();
        Assert.Equal("concerns", value.GetProperty("verdict").GetString());
        Assert.NotNull(value.GetProperty("diagnostic").GetString());
        Assert.Contains("diagnostic", Rendered(result));
    }
}

public class TeamConcurrencyTests
{
    private static ToolExecutionInput Input(Agent agent, string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = $"call_{name}",
        Signal = CancellationToken.None,
        Agent = agent,
    };

    [Fact]
    public async Task TeamTools_AreMarkedParallelSafe()
    {
        await using var harness = TestHarness.Create();
        SubagentService.Mount(harness.Ctx);
        new TeamPlugin().Apply(harness.Ctx);
        new SubagentToolsPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        Assert.Equal(ToolRuntime.Mode.Parallel, harness.Tools.ExecutionMode("spawn_teammate", JsonSerializer.SerializeToElement(new { label = "a" }), lead.ScopeKey));
        Assert.Equal(ToolRuntime.Mode.Parallel, harness.Tools.ExecutionMode("send_message", JsonSerializer.SerializeToElement(new { to_session_id = "x", body = "y" }), lead.ScopeKey));
        Assert.Equal(ToolRuntime.Mode.Parallel, harness.Tools.ExecutionMode("interrupt_agent", JsonSerializer.SerializeToElement(new { session_id = "x" }), lead.ScopeKey));
        Assert.Equal(ToolRuntime.Mode.Parallel, harness.Tools.ExecutionMode("wait_agent", JsonSerializer.SerializeToElement(new { session_id = "x" }), lead.ScopeKey));
        Assert.Equal(ToolRuntime.Mode.Parallel, harness.Tools.ExecutionMode("team_task_create", JsonSerializer.SerializeToElement(new { title = "t" }), lead.ScopeKey));
        Assert.Equal(ToolRuntime.Mode.Parallel, harness.Tools.ExecutionMode("subagent_start", JsonSerializer.SerializeToElement(new { prompt = "p" }), lead.ScopeKey));
        Assert.Equal(ToolRuntime.Mode.Parallel, harness.Tools.ExecutionMode("subagent_send", JsonSerializer.SerializeToElement(new { session_id = "x", prompt = "p" }), lead.ScopeKey));
    }

    [Fact]
    public async Task ParallelSends_ToDifferentTeammates_BothDeliver()
    {
        await using var harness = TestHarness.Create(options =>
        {
            var text = string.Concat(options.Messages.Select(m => m.FlattenText()));
            if (text.Contains("TASK-ONE")) return Scripted.Text("one finished");
            if (text.Contains("TASK-TWO")) return Scripted.Text("two finished");
            return Scripted.Text("teammate standing by.");
        });
        SubagentService.Mount(harness.Ctx);
        new TeamPlugin().Apply(harness.Ctx);
        var service = TeamService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var first = await service.SpawnTeammateAsync(lead, "one");
        var second = await service.SpawnTeammateAsync(lead, "two");

        var sendOne = harness.Tools.Execute(Input(lead, "send_message",
            new { to_session_id = first.SessionId, body = "TASK-ONE" }));
        var sendTwo = harness.Tools.Execute(Input(lead, "send_message",
            new { to_session_id = second.SessionId, body = "TASK-TWO" }));
        var results = await Task.WhenAll(sendOne, sendTwo);

        Assert.All(results, r => Assert.False(r.IsError));
        Assert.Equal("one finished", results[0].Value.GetValueOrDefault().GetProperty("reply").GetString());
        Assert.Equal("two finished", results[1].Value.GetValueOrDefault().GetProperty("reply").GetString());

        var mailbox = TeamService.Mailbox(lead.Session).ToList();
        Assert.Equal(2, mailbox.Count);
        Assert.Contains(mailbox, m => m.To == first.SessionId && m.Body == "TASK-ONE");
        Assert.Contains(mailbox, m => m.To == second.SessionId && m.Body == "TASK-TWO");
    }

    [Fact]
    public async Task ParallelSends_ToTheSameTeammate_SerializeInOrder()
    {
        await using var harness = TestHarness.Create(options =>
        {
            var text = options.Messages.LastOrDefault()?.FlattenText() ?? "";
            if (text.Contains("SLOW-TASK")) return Scripted.Text("slow reply");
            if (text.Contains("FAST-TASK")) return Scripted.Text("fast reply");
            return Scripted.Text("teammate standing by.");
        });
        SubagentService.Mount(harness.Ctx);
        new TeamPlugin().Apply(harness.Ctx);
        var service = TeamService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var spawn = await service.SpawnTeammateAsync(lead, "worker");
        var target = spawn.SessionId;

        // two sends race at the per-child gate; both must deliver exactly one turn each
        var sends = await Task.WhenAll(
            harness.Tools.Execute(Input(lead, "send_message", new { to_session_id = target, body = "SLOW-TASK" })),
            harness.Tools.Execute(Input(lead, "send_message", new { to_session_id = target, body = "FAST-TASK" })));
        Assert.All(sends, r => Assert.False(r.IsError));
        Assert.Equal("slow reply", sends[0].Value.GetValueOrDefault().GetProperty("reply").GetString());
        Assert.Equal("fast reply", sends[1].Value.GetValueOrDefault().GetProperty("reply").GetString());

        var mailboxBodies = TeamService.Mailbox(lead.Session).Select(m => m.Body).ToList();
        Assert.Equal(2, mailboxBodies.Count(t => t is "SLOW-TASK" or "FAST-TASK"));

        // the child settled two user turns total (spawn greeting + two instructions)
        var child = service.LiveAgent(target);
        Assert.NotNull(child);
        var userTurns = child!.Session.Events.Count(e => e.Type == SessionEventTypes.UserMessage);
        Assert.Equal(3, userTurns);
    }
}
