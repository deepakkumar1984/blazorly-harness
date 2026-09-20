using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Delegation UX: children live in the parent chat. Progress is durable
/// (<c>subagent/status</c> events in the parent log, folded into the delegations panel),
/// children never block on the human, and the sidebar stays clean of child sessions.
/// </summary>
public class DelegationProgressTests
{
    private static ToolExecutionInput Input(Blazorly.Harness.Core.Agent.Agent agent, string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = $"call_{name}",
        Signal = CancellationToken.None,
        Agent = agent,
    };

    private static IReadOnlyList<SessionPayloads.SubagentStatusPayload> StatusEventsOf(Session session)
        => session.Events
            .Where(e => e.Type == SessionEventTypes.SubagentStatus)
            .Select(SessionEventRead.SubagentStatusOf)
            .ToList();

    [Fact]
    public async Task ForegroundSpawn_RecordsRunningThenFinishedWithSummary()
    {
        await using var harness = TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("child finished the thing")
                : Scripted.Text("lead reply"));
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: "do the thing", Description: "the thing"), CancellationToken.None);

        var events = StatusEventsOf(lead.Session);
        Assert.Equal(2, events.Count);
        Assert.Equal(result.SessionId, events[0].ChildSessionId);
        Assert.Equal("running", events[0].Status);
        Assert.Equal("the thing", events[0].Description);
        Assert.Equal("finished", events[1].Status);
        Assert.Equal("child finished the thing", events[1].Summary);

        // log-only: delegation progress never enters model history
        var derived = lead.Session.DeriveMessages();
        Assert.DoesNotContain(derived, m => m.FlattenText().Contains("subagent/status"));
    }

    [Fact]
    public async Task BackgroundSpawn_RecordsFinishAfterIdle()
    {
        await using var harness = TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("background work done")
                : Scripted.Text("lead reply"));
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var started = subagents.SpawnBackgroundAsync(lead, new SubagentRequest(
            Prompt: "work in the background", Description: "bg worker"));
        Assert.Equal("running", Assert.Single(StatusEventsOf(lead.Session)).Status);

        var child = subagents.GetChild(started.SessionId)!;
        await child.WhenIdleAsync();

        // the finished status is appended by the spawn's own completion continuation — poll for it
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (StatusEventsOf(lead.Session).Count < 2 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);

        var events = StatusEventsOf(lead.Session);
        Assert.Equal(2, events.Count);
        Assert.Equal("finished", events[1].Status);
        Assert.Equal("background work done", events[1].Summary);
        Assert.Equal("bg worker", events[1].Description);
    }

    [Fact]
    public async Task Continue_RecordsSettledOutcomeForTeamAndSubagentSends()
    {
        await using var harness = TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("round two reply")
                : Scripted.Text("lead reply"));
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var first = await subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: "first task", Description: "worker", Continuable: true), CancellationToken.None);
        await subagents.ContinueAsync(lead, first.SessionId, "second task", CancellationToken.None);

        var events = StatusEventsOf(lead.Session);
        Assert.Equal(3, events.Count); // running, finished, finished (continuation)
        Assert.All(events, e => Assert.Equal(first.SessionId, e.ChildSessionId));
        Assert.Equal("finished", events[2].Status);
        Assert.Equal("round two reply", events[2].Summary);
    }

    [Fact]
    public void ConversationFold_DelegationsLatestPerChildWinsAndKeepsDescription()
    {
        var harness = TestHarness.Create();
        var session = new Session(new SessionHeader { Id = "session-delegations", CreatedAt = 1, Cwd = "/tmp" });

        session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-1", "swarm worker: alpha", "running"));
        session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-2", "swarm worker: beta", "running"));
        session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-1", null, "finished", "alpha done"));

        var snapshot = new ConversationAssembler(harness.Tools).Fold(session, agent: null);
        Assert.Equal(2, snapshot.Delegations.Count);

        var alpha = snapshot.Delegations.Single(d => d.ChildSessionId == "child-1");
        Assert.Equal("finished", alpha.Status);
        Assert.Equal("swarm worker: alpha", alpha.Description); // later events dropped it; the fold keeps the first
        Assert.Equal("alpha done", alpha.Summary);

        var beta = snapshot.Delegations.Single(d => d.ChildSessionId == "child-2");
        Assert.Equal("running", beta.Status);
    }

    [Fact]
    public async Task AskUserQuestion_FromDelegatedChild_NeverBlocksAndAnswersAutonomously()
    {
        await using var harness = TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("child reply")
                : Scripted.Text("lead reply"));
        SubagentService.Mount(harness.Ctx);
        harness.Tools.Register(new AskUserTool(harness.Ctx));
        var lead = harness.CreateAgent();

        var child = harness.Loop.Create(new SessionMeta(
            Cwd: lead.Session.Header.Cwd, ParentSession: lead.Id, DelegationDepth: 1));
        child.Followup(Message.CreateUserText("idle yourself"));
        await child.WhenIdleAsync();

        // no UserQuestionsService exists in this harness — without the guard this hangs/fails;
        // the delegated-child guard must answer instantly with standing guidance instead
        var result = await harness.Tools.Execute(Input(child, "ask_user_question", new
        {
            questions = new object[] { new { id = "q1", question = "Which database should I target?" } },
        }));
        Assert.False(result.IsError);
        var answer = result.Value.GetValueOrDefault().GetProperty("answers")[0].GetProperty("text").GetString();
        Assert.Contains("not available to delegated subagents", answer);
    }
}
