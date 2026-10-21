using System.Diagnostics;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// The transcript fold runs on a 120ms timer for as long as the page is open, so any per-tick cost
/// that scales with session length is a UI that gets heavier the longer you chat. These pin the
/// properties that keep a tick O(new events + retained window): latest-wins state folded as events
/// pass instead of re-scanned, the node list ordered by construction instead of re-sorted, tool
/// results paired by index instead of by scan, and retained nodes bounded with an honest total.
/// </summary>
public class ConversationFoldScalingTests : IAsyncLifetime
{
    private TestHarness _harness = null!;

    public Task InitializeAsync()
    {
        _harness = TestHarness.Create();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _harness.DisposeAsync().AsTask();

    /// <summary>One turn of the shape a real log has: question, streamed answer, tool round trip.</summary>
    private static void AppendTurn(Session session, int turn, bool failed = false)
    {
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(turn));
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText($"question {turn}"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(turn, 1));

        var callId = $"call_{turn}";
        var assistant = Message.CreateAssistant("scripted", "test",
            [new TextBlock($"answer {turn}"), new ToolCallBlock(callId, "bash", "{}")]);
        session.Append(SessionEventTypes.AssistantMessage, new SessionPayloads.AssistantMessage(turn, 1, assistant),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.ToolCall, new SessionPayloads.ToolCall(turn, 1, callId, "bash", "{}"));
        session.Append(SessionEventTypes.ToolResult,
            new SessionPayloads.ToolResult(turn, 1, Message.CreateToolResult(callId, [new TextBlock($"output {turn}")])),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));

        session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(turn, 1));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(turn,
            failed ? new TurnEndReason.Error("boom", "INVALID_REQUEST") : new TurnEndReason.Completed()));
    }

    private static Session BuildSession(string id, int turns, bool everySeventhFails = false)
    {
        var session = new Session(new SessionHeader { Id = id, CreatedAt = 1, Cwd = "/tmp" });
        for (var turn = 1; turn <= turns; turn++) AppendTurn(session, turn, everySeventhFails && turn % 7 == 0);
        return session;
    }

    [Fact]
    public void Folder_FoldsLatestWinsState_MatchingTheSessionScans()
    {
        var session = BuildSession("latest-wins", turns: 9, everySeventhFails: true);
        // Title set once near the head — the scan it replaces walked nearly the whole log per tick.
        session.Append(SessionEventTypes.SessionTitle, new SessionPayloads.SessionTitlePayload("first title", [], "user"));
        session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload("plan"));
        session.Append(SessionEventTypes.PlanMode, new PlanModePayload(true, Auto: true));

        var folder = new ConversationAssembler(_harness.Tools).CreateFolder(session);
        var snapshot = folder.Update(null);

        Assert.Equal(session.LatestTitle(), snapshot.Title);
        Assert.Equal(session.LatestSandboxMode(), snapshot.SandboxMode);
        Assert.Equal(ConversationAssembler.LatestFailedTurn(session), folder.LatestFailedTurn);
        Assert.Equal("auto", snapshot.PlanMode);
        Assert.Equal(7, folder.LatestFailedTurn); // newest error turn, not the first one

        // Latest wins on a later tick: the fold must pick these up without a rescan.
        session.Append(SessionEventTypes.SessionTitle, new SessionPayloads.SessionTitlePayload("renamed", [], "user"));
        session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload("danger-full-access"));
        session.Append(SessionEventTypes.PlanMode, new PlanModePayload(false));
        AppendTurn(session, 10, failed: true);
        snapshot = folder.Update(null);

        Assert.Equal("renamed", snapshot.Title);
        Assert.Equal(session.LatestTitle(), snapshot.Title);
        Assert.Equal("danger-full-access", snapshot.SandboxMode);
        Assert.Equal(session.LatestSandboxMode(), snapshot.SandboxMode);
        Assert.Null(snapshot.PlanMode); // inactive plan mode renders no chip
        Assert.Equal(10, folder.LatestFailedTurn);
        Assert.Equal(ConversationAssembler.LatestFailedTurn(session), folder.LatestFailedTurn);
    }

    [Fact]
    public void Folder_PlanMode_ManualEngageReadsAsOn()
    {
        var session = BuildSession("plan-manual", turns: 1);
        session.Append(SessionEventTypes.PlanMode, new PlanModePayload(true));
        var snapshot = new ConversationAssembler(_harness.Tools).CreateFolder(session).Update(null);
        Assert.Equal("on", snapshot.PlanMode);
        Assert.Equal(new PlanModeService().Latest(session)?.Active, true);
    }

    [Fact]
    public void Nodes_StayInSeqOrder_WithoutAResort()
    {
        var session = BuildSession("ordering", turns: 6, everySeventhFails: true);
        session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload("plan"));
        session.Append(SessionEventTypes.CommandRun, new SessionPayloads.CommandRunPayload("title", "x"));

        var nodes = new ConversationAssembler(_harness.Tools).CreateFolder(session).Update(null).Nodes.ToList();

        Assert.NotEmpty(nodes);
        var seqs = nodes.Select(SeqOf).ToList();
        Assert.Equal(seqs.OrderBy(s => s).ToList(), seqs);

        // Every settled tool card carries its result: pairing now goes through an index, not a scan.
        var tools = nodes.Where(n => n.Kind == "tool").ToList();
        Assert.Equal(6, tools.Count);
        Assert.All(tools, t => Assert.Equal("done", t.ToolStatus));
        Assert.All(tools, t => Assert.StartsWith("output ", t.ResultText));
    }

    /// <summary>Node keys are <c>{prefix}-{seq}</c>; the seq is the fold's ordering contract.</summary>
    private static int SeqOf(ConversationNode node)
    {
        var parts = node.Key.Split('-');
        Assert.Equal(2, parts.Length);
        return int.Parse(parts[1]);
    }

    [Fact]
    public void Retention_TrimsOldNodes_ButKeepsTheTotalHonest()
    {
        var session = BuildSession("retention", turns: 60);
        var unbounded = new ConversationAssembler(_harness.Tools).CreateFolder(session, int.MaxValue).Update(null);

        const int retention = 50;
        var folder = new ConversationAssembler(_harness.Tools).CreateFolder(session, retention);
        var trimmed = folder.Update(null);

        Assert.True(unbounded.TotalNodes > retention, "test must build more nodes than the cap");
        Assert.Equal(retention, trimmed.Nodes.Count);
        Assert.Equal(unbounded.Nodes.Count, trimmed.TotalNodes); // nothing is lost, only dropped from memory
        Assert.Equal(unbounded.TotalNodes, trimmed.TotalNodes);

        // Trimming drops the head, so the retained window is the newest nodes.
        Assert.Equal(unbounded.Nodes.Skip(unbounded.Nodes.Count - retention).Select(n => n.Key),
            trimmed.Nodes.Select(n => n.Key));

        // A larger retention re-folds the older nodes back — the scroll-up path in the page.
        var grown = new ConversationAssembler(_harness.Tools).CreateFolder(session, unbounded.TotalNodes).Update(null);
        Assert.Equal(unbounded.Nodes.Select(n => n.Key), grown.Nodes.Select(n => n.Key));
    }

    [Fact]
    public void ToolResult_StillPairsWithItsCard_AfterATrim()
    {
        var session = BuildSession("pair-after-trim", turns: 40);
        var folder = new ConversationAssembler(_harness.Tools).CreateFolder(session, 30);
        folder.Update(null);

        // One open step: a call left pending while filler calls push the trim boundary past its
        // card. The index shift on trim must not strand the result when it finally lands.
        var callIds = Enumerable.Range(0, 10).Select(i => $"filler_{i}").ToList();
        var blocks = new List<ContentBlock> { new TextBlock("working") };
        blocks.Add(new ToolCallBlock("late_call", "bash", "{}"));
        blocks.AddRange(callIds.Select(id => (ContentBlock)new ToolCallBlock(id, "bash", "{}")));

        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(41));
        session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(41, 1));
        session.Append(SessionEventTypes.AssistantMessage,
            new SessionPayloads.AssistantMessage(41, 1, Message.CreateAssistant("scripted", "test", blocks)),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.ToolCall, new SessionPayloads.ToolCall(41, 1, "late_call", "bash", "{}"));
        folder.Update(null);

        foreach (var id in callIds)
        {
            session.Append(SessionEventTypes.ToolCall, new SessionPayloads.ToolCall(41, 1, id, "bash", "{}"));
            session.Append(SessionEventTypes.ToolResult,
                new SessionPayloads.ToolResult(41, 1, Message.CreateToolResult(id, [new TextBlock("ok")])),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        }
        var midTrim = folder.Update(null);
        Assert.True(midTrim.Nodes.Count <= 30, "the fillers should have pushed the retention trim");

        session.Append(SessionEventTypes.ToolResult,
            new SessionPayloads.ToolResult(41, 1, Message.CreateToolResult("late_call", [new TextBlock("late output")])),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        var nodes = folder.Update(null).Nodes.ToList();

        var card = Assert.Single(nodes, n => n.CallId == "late_call");
        Assert.Equal("done", card.ToolStatus);
        Assert.Equal("late output", card.ResultText);
        Assert.False(card.IsError);
        Assert.All(nodes.Where(n => n.Kind == "tool"), n => Assert.Equal("done", n.ToolStatus));
    }

    [Fact]
    public void Update_IdleTick_DoesNotScaleWithSessionLength()
    {
        // The regression this guards: every tick used to copy the whole event log twice
        // (Session.Events allocates a full copy — once for the newest failed turn, once for the
        // plan-mode flag), rescan the log for the title and sandbox mode, then re-sort every node
        // with a string Split per node. Measured on a 16K-event session: ~1.5ms per tick for the
        // scans and copies alone, eight times a second, growing linearly forever. Folded
        // incrementally and bounded by retention, the same tick now measures ~0.001ms.
        var small = BuildSession("idle-small", turns: 20);
        var large = BuildSession("idle-large", turns: 2_000);
        Assert.True(large.Seq > 10_000, $"large session should be long, was {large.Seq} events");

        var assembler = new ConversationAssembler(_harness.Tools);
        var smallFolder = assembler.CreateFolder(small);
        var largeFolder = assembler.CreateFolder(large);
        smallFolder.Update(null);
        largeFolder.Update(null);

        const int ticks = 200;
        var smallMs = TimeIdleTicks(smallFolder, ticks);
        var largeMs = TimeIdleTicks(largeFolder, ticks);

        // ~800x the measured cost, so a loaded CI box will not flake, and well under the ~304ms
        // the pre-fix scans and copies alone took for the same 200 ticks.
        Assert.True(largeMs < 250,
            $"{ticks} idle ticks on a {large.Seq:N0}-event session took {largeMs:N0}ms; the fold is scaling with session length again");
        Assert.True(smallMs < 250, $"{ticks} idle ticks on a small session took {smallMs:N0}ms");
    }

    private static double TimeIdleTicks(ConversationFolder folder, int ticks)
    {
        folder.Update(null); // warm: first call after a fold is not representative
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < ticks; i++) folder.Update(null);
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }
}
