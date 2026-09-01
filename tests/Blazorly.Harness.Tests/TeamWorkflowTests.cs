using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Tools;

namespace Blazorly.Harness.Tests;

public class TeamTests
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

    private static bool IsChildSession(GenerateOptions options, string? leadId)
        => options.SessionId is { Length: > 0 } id && id != leadId;

    [Fact]
    public async Task SpawnTeammate_AppendsRosterMemberAndStampsChildLineage()
    {
        string? leadId = null;
        await using var harness = TestHarness.Create(options =>
            IsChildSession(options, leadId)
                ? ReplayScript.Text("Teammate ready for assignments.")
                : ReplayScript.Text("lead reply"));
        var subagents = SubagentService.Mount(harness.Ctx);
        new TeamPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();
        leadId = lead.Id;

        var result = await harness.Tools.Execute(Input(lead, "spawn_teammate", new { label = "researcher" }));
        Assert.False(result.IsError);

        var member = Assert.Single(TeamService.Roster(lead.Session));
        Assert.Equal("researcher", member.Label);
        Assert.Equal(TeamMemberStatus.Idle, member.Status);
        Assert.Equal(member.SessionId, result.Value.GetValueOrDefault().GetProperty("sessionId").GetString());

        var childSession = harness.Sessions.Get(member.SessionId);
        Assert.NotNull(childSession);
        Assert.Equal(lead.Id, childSession!.Header.ParentSession);
        Assert.Equal(1, childSession.Header.DelegationDepth);
        Assert.Same(childSession, subagents.GetChild(member.SessionId)!.Session);
    }

    [Fact]
    public async Task ReportTool_QueuesDeliversAndInjectsIntoLeadInbox()
    {
        string? leadId = null;
        await using var harness = TestHarness.Create(options =>
            IsChildSession(options, leadId)
                ? ReplayScript.Text("Teammate standing by.")
                : ReplayScript.Text("lead reply"));
        var subagents = SubagentService.Mount(harness.Ctx);
        new TeamPlugin().Apply(harness.Ctx);
        var service = TeamService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();
        leadId = lead.Id;

        var spawn = await service.SpawnTeammateAsync(lead, "writer");
        var child = subagents.GetChild(spawn.SessionId);
        Assert.NotNull(child);

        var report = harness.Tools.Get("report", child!.ScopeKey);
        Assert.NotNull(report);
        var result = await harness.Tools.Execute(Input(child, "report", new { summary = "chapter one drafted" }));
        Assert.False(result.IsError);

        Assert.Contains(lead.Session.Events, e => e.Type == SessionEventTypes.TeamMessageQueued);
        Assert.Contains(lead.Session.Events, e => e.Type == SessionEventTypes.TeamMessageDelivered);
        var message = Assert.Single(TeamService.Mailbox(lead.Session));
        Assert.Equal("chapter one drafted", message.Body);
        Assert.Equal(spawn.SessionId, message.From);
        Assert.Equal(lead.Id, message.To);

        Assert.Contains(lead.Session.Events, e => e.Type == SessionEventTypes.AgentInboxSpliced);
        Assert.Contains(lead.Inbox.NextStep, m => m.FlattenText() == "[teammate writer] chapter one drafted");
    }

    [Fact]
    public async Task SendMessage_ContinuesChildAndRecordsMailboxDelivery()
    {
        await using var harness = TestHarness.Create(options =>
            options.Messages.LastOrDefault()?.FlattenText().Contains("FIND-THE-ANSWER") == true
                ? ReplayScript.Text("pong: answer found")
                : ReplayScript.Text("teammate standing by."));
        SubagentService.Mount(harness.Ctx);
        new TeamPlugin().Apply(harness.Ctx);
        var service = TeamService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var spawn = await service.SpawnTeammateAsync(lead, "scout");
        var result = await harness.Tools.Execute(Input(lead, "send_message",
            new { to_session_id = spawn.SessionId, body = "FIND-THE-ANSWER and report back" }));
        Assert.False(result.IsError);
        Assert.Equal("pong: answer found", result.Value.GetValueOrDefault().GetProperty("reply").GetString());

        var mailbox = Assert.Single(TeamService.Mailbox(lead.Session));
        Assert.Equal("FIND-THE-ANSWER and report back", mailbox.Body);
        Assert.Equal(spawn.SessionId, mailbox.To);
        Assert.Contains(lead.Session.Events, e => e.Type == SessionEventTypes.TeamMessageDelivered);
        Assert.Equal(TeamMemberStatus.Idle, Assert.Single(TeamService.Roster(lead.Session)).Status);
    }

    [Fact]
    public async Task TeamTasks_CreateUpdateList_FoldLatestWins()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("unused"));
        SubagentService.Mount(harness.Ctx);
        new TeamPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var create = await harness.Tools.Execute(Input(lead, "team_task_create", new { title = "write tests" }));
        Assert.False(create.IsError);
        var taskId = create.Value.GetValueOrDefault().GetProperty("id").GetString();
        Assert.Equal("open", create.Value.GetValueOrDefault().GetProperty("status").GetString());

        var update = await harness.Tools.Execute(Input(lead, "team_task_update",
            new { task_id = taskId, status = "in_progress", assignee = "session-sub-1" }));
        Assert.False(update.IsError);
        Assert.Equal("in_progress", update.Value.GetValueOrDefault().GetProperty("status").GetString());

        var list = await harness.Tools.Execute(Input(lead, "team_task_list", new { }));
        Assert.False(list.IsError);
        Assert.Equal(1, list.Value.GetValueOrDefault().GetProperty("tasks").GetArrayLength());

        var task = Assert.Single(TeamService.Tasks(lead.Session));
        Assert.Equal(taskId, task.Id);
        Assert.Equal("write tests", task.Title);
        Assert.Equal("in_progress", task.Status);
        Assert.Equal("session-sub-1", task.Assignee);

        var missing = await harness.Tools.Execute(Input(lead, "team_task_update",
            new { task_id = "task-999", status = "done" }));
        Assert.True(missing.IsError);
        Assert.Equal("TASK_NOT_FOUND", missing.Error!.Info!.Code);

        var done = await harness.Tools.Execute(Input(lead, "team_task_update", new { task_id = taskId, status = "done" }));
        Assert.False(done.IsError);
        Assert.Equal("done", Assert.Single(TeamService.Tasks(lead.Session)).Status);
        Assert.Equal("session-sub-1", Assert.Single(TeamService.Tasks(lead.Session)).Assignee); // omitted fields keep values
        Assert.Contains("(done)", Rendered(done));
    }
}

public class WorkflowTests
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

    [Fact]
    public async Task Workflow_ChainsStepSummariesIntoTheNextPrompt()
    {
        var calls = new List<GenerateOptions>();
        await using var harness = TestHarness.Create(options =>
        {
            calls.Add(options);
            var last = options.Messages.LastOrDefault()?.FlattenText() ?? "";
            if (last.Contains("STEP-ONE")) return ReplayScript.Text("did step A");
            if (last.Contains("STEP-TWO")) return ReplayScript.Text("did step B");
            return ReplayScript.Text("lead fallback");
        });
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(lead, "workflow", new
        {
            name = "pipeline",
            steps = new object[]
            {
                new { prompt = "STEP-ONE: survey the code" },
                new { prompt = "STEP-TWO: write the patch", description = "second stage" },
            },
        }));
        Assert.False(result.IsError);

        var text = Rendered(result);
        Assert.Contains("did step A", text);
        Assert.Contains("did step B", text);
        Assert.Equal("completed", result.Value.GetValueOrDefault().GetProperty("status").GetString());
        var steps = result.Value.GetValueOrDefault().GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal("did step A", steps[0].GetProperty("summary").GetString());

        var children = subagents.ChildrenOf(lead.Id);
        Assert.Equal(2, children.Count);
        Assert.All(children, h => Assert.Equal(lead.Id, h.ParentSession));
        Assert.All(children, h => Assert.Equal(1, h.DelegationDepth));
        Assert.Contains(steps[0].GetProperty("session_id").GetString(), children.Select(c => c.Id));

        // the second child's prompt carried the first step's summary as context
        var second = calls.Single(o => Flatten(o).Contains("STEP-TWO"));
        Assert.Contains("did step A", Flatten(second));
    }

    [Fact]
    public async Task Ralph_CompleteMarker_StopsAfterOneRound()
    {
        string? leadId = null;
        await using var harness = TestHarness.Create(options =>
            IsChild(options, leadId)
                ? ReplayScript.Text("working\nOBJECTIVE_COMPLETE")
                : ReplayScript.Text("lead reply"));
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();
        leadId = lead.Id;

        var result = await harness.Tools.Execute(Input(lead, "ralph", new { objective = "clean the repo", max_rounds = 3 }));
        Assert.False(result.IsError);
        Assert.Single(subagents.ChildrenOf(lead.Id));

        Assert.Equal("complete", result.Value.GetValueOrDefault().GetProperty("status").GetString());
        var rounds = result.Value.GetValueOrDefault().GetProperty("rounds");
        Assert.Equal(1, rounds.GetArrayLength());
        Assert.Equal("complete", rounds[0].GetProperty("outcome").GetString());
        Assert.Equal("working", rounds[0].GetProperty("summary").GetString());
        Assert.Contains("Final status: complete", Rendered(result));
    }

    [Fact]
    public async Task Ralph_BlockedThenComplete_RunsTwoRoundsWithBlockerHandoff()
    {
        var childCalls = new List<GenerateOptions>();
        string? leadId = null;
        await using var harness = TestHarness.Create(options =>
        {
            if (IsChild(options, leadId))
            {
                childCalls.Add(options);
                return childCalls.Count == 1
                    ? ReplayScript.Text("stuck\nOBJECTIVE_BLOCKED: missing file")
                    : ReplayScript.Text("done\nOBJECTIVE_COMPLETE");
            }
            return ReplayScript.Text("lead reply");
        });
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();
        leadId = lead.Id;

        var result = await harness.Tools.Execute(Input(lead, "ralph", new { objective = "restore the build" }));
        Assert.False(result.IsError);
        Assert.Equal(2, subagents.ChildrenOf(lead.Id).Count);
        Assert.Equal(2, childCalls.Count);

        Assert.Equal("complete", result.Value.GetValueOrDefault().GetProperty("status").GetString());
        var rounds = result.Value.GetValueOrDefault().GetProperty("rounds");
        Assert.Equal(2, rounds.GetArrayLength());
        Assert.Equal("blocked", rounds[0].GetProperty("outcome").GetString());
        Assert.Equal("stuck", rounds[0].GetProperty("summary").GetString());
        Assert.Equal("complete", rounds[1].GetProperty("outcome").GetString());

        // round 2 was handed the blocker to clear
        Assert.Contains("missing file", Flatten(childCalls[1]));
        Assert.Contains("Previous round handoff", Flatten(childCalls[1]));
    }

    [Fact]
    public async Task Ralph_ExhaustedRounds_ReturnsPartialReport()
    {
        var childCalls = new List<GenerateOptions>();
        string? leadId = null;
        await using var harness = TestHarness.Create(options =>
        {
            if (IsChild(options, leadId))
            {
                childCalls.Add(options);
                return ReplayScript.Text($"round {childCalls.Count}: still working");
            }
            return ReplayScript.Text("lead reply");
        });
        var subagents = SubagentService.Mount(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();
        leadId = lead.Id;

        var result = await harness.Tools.Execute(Input(lead, "ralph", new { objective = "make every test pass", max_rounds = 3 }));
        Assert.False(result.IsError);
        Assert.Equal(3, subagents.ChildrenOf(lead.Id).Count);
        Assert.Equal(3, childCalls.Count);

        Assert.Equal("partial", result.Value.GetValueOrDefault().GetProperty("status").GetString());
        var rounds = result.Value.GetValueOrDefault().GetProperty("rounds");
        Assert.Equal(3, rounds.GetArrayLength());
        Assert.All(rounds.EnumerateArray(), r => Assert.Equal("progress", r.GetProperty("outcome").GetString()));

        // each unfinished round's summary became the next round's handoff
        Assert.Contains("round 1: still working", Flatten(childCalls[1]));
        Assert.Contains("round 2: still working", Flatten(childCalls[2]));
        Assert.Contains("Final status: partial", Rendered(result));
    }

    private static bool IsChild(GenerateOptions options, string? leadId)
        => options.SessionId is { Length: > 0 } id && id != leadId;
}
