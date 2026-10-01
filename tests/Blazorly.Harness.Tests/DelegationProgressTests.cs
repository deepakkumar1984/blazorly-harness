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
        var calls = 0;
        await using var harness = TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text(Interlocked.Increment(ref calls) == 1 ? "round one reply" : "round two reply")
                : Scripted.Text("lead reply"));
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var first = await subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: "first task", Description: "worker", Continuable: true), CancellationToken.None);
        await subagents.ContinueAsync(lead, first.SessionId, "second task", CancellationToken.None);

        var events = StatusEventsOf(lead.Session);
        Assert.Equal(3, events.Count); // running, finished, finished (continuation)
        Assert.All(events, e => Assert.Equal(first.SessionId, e.ChildSessionId));
        Assert.Equal("finished", events[1].Status);
        Assert.Equal("round one reply", events[1].Summary);
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
    public void DismissDelegation_HidesRowAndIsIdempotent()
    {
        var harness = TestHarness.Create();
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();
        lead.Session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-1", "worker", "finished", "done"));

        subagents.DismissDelegation(lead.Id, "child-1");
        subagents.DismissDelegation(lead.Id, "child-1");

        var events = StatusEventsOf(lead.Session);
        Assert.Equal(2, events.Count); // finished + a single dismissed (the second dismiss dedups)
        Assert.Equal(SubagentService.StatusDismissed, events[1].Status);

        var snapshot = new ConversationAssembler(harness.Tools).Fold(lead.Session, agent: null);
        Assert.Empty(snapshot.Delegations);
    }

    [Fact]
    public void DismissSettledDelegations_KeepsRunningRows()
    {
        var harness = TestHarness.Create();
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();
        lead.Session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-1", "settled worker", "finished", "done"));
        lead.Session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-2", "live worker", "running"));

        var dismissed = subagents.DismissSettledDelegations(lead.Id);

        Assert.Equal(1, dismissed);
        var snapshot = new ConversationAssembler(harness.Tools).Fold(lead.Session, agent: null);
        var remaining = Assert.Single(snapshot.Delegations);
        Assert.Equal("child-2", remaining.ChildSessionId);
        Assert.Equal("running", remaining.Status);
    }

    [Fact]
    public void ConversationFold_DismissedRowReappearsOnNewActivity()
    {
        var harness = TestHarness.Create();
        var session = new Session(new SessionHeader { Id = "session-redeliver", CreatedAt = 1, Cwd = "/tmp" });
        session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-1", "worker", "finished", "done"));
        session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-1", null, SubagentService.StatusDismissed));
        session.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload("child-1", null, "running"));

        var snapshot = new ConversationAssembler(harness.Tools).Fold(session, agent: null);
        var row = Assert.Single(snapshot.Delegations);
        Assert.Equal("running", row.Status);
    }

    [Fact]
    public async Task ContinueAsync_TimeoutReturnsQueuedAndKeepsMessage()
    {
        await using var harness = TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("slow reply")
                : Scripted.Text("lead reply"));
        harness.ScriptedLlm.ChunkDelayMs = 30_000; // the child's first turn cannot settle during the test
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var started = subagents.SpawnBackgroundAsync(lead, new SubagentRequest(
            Prompt: "slow work", Description: "slow worker", Continuable: true));
        var child = subagents.GetChild(started.SessionId)!;
        await PollAsync(() => child.Status == Blazorly.Harness.Core.Agent.AgentStatus.Running, "child to start running");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        var result = await subagents.ContinueAsync(lead, started.SessionId, "poke", cts.Token);

        Assert.Equal(SubagentService.FinishKindQueued, result.FinishKind);
        Assert.Contains("queued", result.Summary);
        Assert.Single(child.Inbox.NextTurn); // the delivery stands: the poke runs in turn order
        var runningRows = StatusEventsOf(lead.Session).Count(e => e.Status == "running");
        Assert.Equal(2, runningRows); // spawn + the timeout re-mark that keeps reconcile honest

        child.Cancel(Blazorly.Harness.Core.Agent.AgentCancelCause.User());
        await child.WhenIdleAsync();
    }

    [Fact]
    public async Task ContinueAsync_AbortStillThrows()
    {
        await using var harness = TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("slow reply")
                : Scripted.Text("lead reply"));
        harness.ScriptedLlm.ChunkDelayMs = 30_000;
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var started = subagents.SpawnBackgroundAsync(lead, new SubagentRequest(
            Prompt: "slow work", Description: "slow worker", Continuable: true));
        var child = subagents.GetChild(started.SessionId)!;
        await PollAsync(() => child.Status == Blazorly.Harness.Core.Agent.AgentStatus.Running, "child to start running");

        using var abort = new CancellationTokenSource();
        abort.CancelAfter(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            subagents.ContinueAsync(lead, started.SessionId, "poke", abort.Token, abort.Token));

        child.Cancel(Blazorly.Harness.Core.Agent.AgentCancelCause.User());
        await child.WhenIdleAsync();
    }

    [Fact]
    public async Task SubagentList_ReportsRunningIdleAndPending()
    {
        await using var harness = TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("child work")
                : Scripted.Text("lead reply"));
        harness.ScriptedLlm.ChunkDelayMs = 30_000;
        var subagents = SubagentService.Mount(harness.Ctx);
        harness.Tools.Register(new SubagentListTool(subagents));
        var lead = harness.CreateAgent();

        var started = subagents.SpawnBackgroundAsync(lead, new SubagentRequest(
            Prompt: "slow work", Description: "slow worker", Continuable: true));
        var child = subagents.GetChild(started.SessionId)!;
        await PollAsync(() => child.Status == Blazorly.Harness.Core.Agent.AgentStatus.Running, "child to start running");

        var running = await harness.Tools.Execute(Input(lead, "subagent_list", new { }));
        Assert.False(running.IsError);
        var entry = running.Value.GetValueOrDefault().GetProperty("children")[0];
        Assert.True(entry.GetProperty("running").GetBoolean());
        Assert.Equal(0, entry.GetProperty("pending").GetInt32());
        Assert.Contains("running", running.Content.OfType<TextBlock>().Single().Text);

        child.Followup(Message.CreateUserText("poke"));
        var queued = await harness.Tools.Execute(Input(lead, "subagent_list", new { }));
        Assert.Equal(1, queued.Value.GetValueOrDefault().GetProperty("children")[0].GetProperty("pending").GetInt32());
        Assert.Contains("1 pending", queued.Content.OfType<TextBlock>().Single().Text);

        child.Cancel(Blazorly.Harness.Core.Agent.AgentCancelCause.User());
        await child.WhenIdleAsync();
        var idle = await harness.Tools.Execute(Input(lead, "subagent_list", new { }));
        Assert.False(idle.Value.GetValueOrDefault().GetProperty("children")[0].GetProperty("running").GetBoolean());
        Assert.Contains("idle", idle.Content.OfType<TextBlock>().Single().Text);
    }

    [Fact]
    public async Task InterruptedChild_MapsAbortedDelegationStatus()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("unused"));
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();
        var parent = lead.Session;
        var child = harness.Sessions.Create("session-sub-interrupted",
            new SessionMeta(Cwd: parent.Header.Cwd, ParentSession: parent.Id, DelegationDepth: 1));
        parent.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload(child.Id, "worker", "running"));
        // An interrupt landing mid-request surfaces as a cancelled request, not Aborted.
        child.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        child.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        child.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(1, 1));
        child.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1,
            new TurnEndReason.Error("request cancelled", LlmErrorCodes.Aborted)));

        var healed = await subagents.ReconcileAsync(parent.Id);

        Assert.Equal(1, healed);
        var rows = StatusEventsOf(parent);
        Assert.Equal(2, rows.Count);
        Assert.Equal("aborted", rows[1].Status);

        // The healer never double-records: a second pass finds nothing running.
        Assert.Equal(0, await subagents.ReconcileAsync(parent.Id));
        Assert.Equal(2, StatusEventsOf(parent).Count);
    }

    private static async Task PollAsync(Func<bool> ready, string what, int timeoutMs = 5000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (!ready() && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert.True(ready(), $"timed out waiting for {what}");
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
