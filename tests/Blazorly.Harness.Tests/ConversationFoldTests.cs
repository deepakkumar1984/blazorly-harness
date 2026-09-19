using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// The transcript fold reads durable events written by whatever build produced them, so a payload
/// shape drift is a blank session page, not a rendering nit: CompactionService writes
/// <c>shadowSeqs</c> while the reader once declared <c>ShadowedSeqs</c>, which bound null and threw
/// a NullReferenceException on every 120ms fold tick for any session that had ever compacted.
/// </summary>
public class ConversationFoldTests
{
    private static (TestHarness Harness, Session Session) Create()
    {
        var harness = TestHarness.Create();
        var session = new Session(new SessionHeader { Id = "session-fold", CreatedAt = 1, Cwd = "/tmp" });
        return (harness, session);
    }

    [Fact]
    public void RawTrajectoryPage_BoundsLargeLogsAndReusesPreviews()
    {
        var session = new Session(new SessionHeader { Id = "raw-large", CreatedAt = 1, Cwd = "/tmp" });
        for (var i = 0; i < 10_000; i++)
            session.Append(SessionEventTypes.SessionTitle, new { title = new string('x', 200) });
        var page = new RawTrajectoryPage();

        var rows = page.Read(session, 0);
        var first = rows[0];
        Assert.Equal(RawTrajectoryPage.PageSize, rows.Count);
        Assert.Equal(161, first.Preview.Length);
        Assert.Same(first, page.Read(session, 0)[0]);

        var last = page.Read(session, 199);
        Assert.Equal(Enumerable.Range(9_950, 50), last.Select(row => row.Seq));
        Assert.Empty(page.Read(session, 200));
        Assert.Equal(0, page.Read(session, 0)[0].Seq);
    }

    [Fact]
    public void RawTrajectoryPage_AppendsOnlyWithinCurrentPageAndResetsForSession()
    {
        var session = new Session(new SessionHeader { Id = "raw-live", CreatedAt = 1, Cwd = "/tmp" });
        var page = new RawTrajectoryPage();
        Assert.Empty(page.Read(session, 0));
        session.Append(SessionEventTypes.SessionTitle, new { title = "first" });
        var first = Assert.Single(page.Read(session, 0));
        Assert.Equal("{\"title\":\"first\"}", first.Preview);

        for (var i = 0; i < 50; i++)
            session.Append(SessionEventTypes.SessionTitle, new { title = "next" });
        Assert.Equal(50, page.Read(session, 0).Count);
        Assert.Same(first, page.Read(session, 0)[0]);
        Assert.Equal(50, Assert.Single(page.Read(session, 1)).Seq);

        var other = new Session(new SessionHeader { Id = "raw-other", CreatedAt = 1, Cwd = "/tmp" });
        Assert.Empty(page.Read(other, 1));
        other.Append(SessionEventTypes.SessionTitle, new { title = "other" });
        Assert.Contains("other", Assert.Single(page.Read(other, 0)).Preview);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Read(other, -1));
    }

    private static ConversationNode? Fold(TestHarness harness, Session session, string kind)
    {
        var snapshot = new ConversationAssembler(harness.Tools).Fold(session, agent: null);
        return snapshot.Nodes.FirstOrDefault(n => n.Kind == kind && n.CommandName is "compaction" or "ui");
    }

    [Fact]
    public async Task CompactionSummary_WireShape_FoldsWithoutThrowing()
    {
        var (harness, session) = Create();
        await using var _ = harness;
        await Task.CompletedTask;
        // Exactly what CompactionService appends (see the dirt1 session log that reproduced this).
        session.Append(SessionEventTypes.CompactionSummary, new
        {
            compactionId = "compact_1",
            summary = "The user asked for a form builder; the server and UI scaffolding were written.",
            shadowedRange = new { start = 0, end = 230 },
            shadowSeqs = Enumerable.Range(1, 231).ToList(),
            shadowedTokenCount = 169_226,
            provider = "deepseek",
            model = "deepseek-flash",
        });

        var node = Fold(harness, session, "command");

        Assert.NotNull(node);
        Assert.Equal("compaction", node!.CommandName);
        Assert.Equal("231 messages", node.CommandArgs);
        Assert.True(node.CommandOk);
        Assert.Contains("169226 tokens", node.CommandText);
    }

    [Fact]
    public async Task CompactionSummary_LegacySeqsName_StillFolds()
    {
        var (harness, session) = Create();
        await using var _ = harness;
        await Task.CompletedTask;
        session.Append(SessionEventTypes.CompactionSummary, new
        {
            summary = "older log",
            shadowedSeqs = new[] { 3, 4, 5 },
        });

        var node = Fold(harness, session, "command");

        Assert.Equal("3 messages", node!.CommandArgs);
    }

    [Theory]
    [InlineData("""{"compactionId":"compact_1"}""")]                       // no seq list at all
    [InlineData("""{"shadowSeqs":null,"shadowedTokenCount":0}""")]         // explicit nulls
    [InlineData("""{}""")]                                                 // empty payload
    public async Task CompactionSummary_MissingFields_DegradeInsteadOfThrowing(string payloadJson)
    {
        var (harness, session) = Create();
        await using var _ = harness;
        await Task.CompletedTask;
        AppendRaw(session, SessionEventTypes.CompactionSummary, payloadJson);

        var folder = new ConversationAssembler(harness.Tools).CreateFolder(session);
        var snapshot = folder.Update(agent: null);

        Assert.Equal(0, folder.FoldFailures);
        var node = snapshot.Nodes.Single(n => n.CommandName == "compaction");
        Assert.Equal("0 tokens", node.CommandArgs); // no seqs known → fall back to the token count
        Assert.True(node.CommandOk);
    }

    [Fact]
    public async Task UnreadableEvent_BecomesAVisibleChip_AndTheRestOfTheSessionStillFolds()
    {
        var (harness, session) = Create();
        await using var _ = harness;
        await Task.CompletedTask;
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        AppendRaw(session, SessionEventTypes.CompactionSummary, """{"shadowedTokenCount":"not-a-number"}""");
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));

        var folder = new ConversationAssembler(harness.Tools).CreateFolder(session);
        var snapshot = folder.Update(agent: null);

        Assert.Equal(1, folder.FoldFailures);
        var chip = snapshot.Nodes.Single(n => n.CommandName == "ui");
        Assert.False(chip.CommandOk);
        Assert.Contains("could not be rendered", chip.CommandText);
        // The surrounding events still folded: a bad payload costs one chip, not the transcript.
        Assert.Contains(snapshot.Nodes, n => n.Kind == "turn-ok" && n.Turn == 1);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (var i = 0; i < Math.Max(1, timeoutMs / 25); i++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "condition never became true before the timeout");
    }

    [Fact]
    public async Task LiveReasoning_StreamsIntoAFoldedReasoningBlock()
    {
        // Reasoning models stream thinking before any text: mid-turn, the live node must carry
        // the reasoning block (the UI renders it auto-expanded), not the empty placeholder.
        await using var harness = TestHarness.Create(_ => new StreamChunk[]
        {
            new BlockStartChunk(1, "reasoning"),
            new ReasoningDeltaChunk(1, "pondering the request carefully"),
            new FinishChunk(FinishReason.Stop),
        });
        harness.ScriptedLlm.ChunkDelayMs = 1500;
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("hi"));
        await WaitForAsync(() => agent.Session.Events.Any(e =>
            e.Type == SessionEventTypes.AssistantChunk && SessionJson.FromElement<SessionPayloads.AssistantChunk>(e.Data).Chunk is ReasoningDeltaChunk));

        var folder = new ConversationAssembler(harness.Tools).CreateFolder(agent.Session);
        var mid = folder.Update(agent);
        var live = mid.Nodes.SingleOrDefault(n => n.Kind == "assistant");
        Assert.NotNull(live);
        Assert.Equal("streaming", live!.StepStatus);
        var reasoning = Assert.IsType<ReasoningBlock>(live.Blocks!.Single());
        Assert.Contains("pondering", reasoning.Text);
        Assert.DoesNotContain(mid.Nodes, n => n.StepStatus == "thinking");

        await agent.WhenIdleAsync();
        var done = folder.Update(agent);
        Assert.Contains(done.Nodes, n => n.Kind == "assistant" && n.StepStatus == "settled");
    }

    [Fact]
    public async Task ThinkingPlaceholder_ShowsBeforeTheFirstChunk_AndClearsOnceContentStreams()
    {
        // Reasoning models can think for minutes before the first block arrives; the page
        // used to render nothing at all during that window and look hung.
        await using var harness = TestHarness.Create(_ => Scripted.Text("finally!"));
        harness.ScriptedLlm.ChunkDelayMs = 3000;
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("hi"));
        await WaitForAsync(() => agent.Status == AgentStatus.Running
            && agent.Session.Events.Any(e => e.Type == SessionEventTypes.TurnStart));

        var folder = new ConversationAssembler(harness.Tools).CreateFolder(agent.Session);
        var mid = folder.Update(agent);
        var thinking = mid.Nodes.SingleOrDefault(n => n.StepStatus == "thinking");
        Assert.NotNull(thinking);
        Assert.Empty(thinking!.Blocks ?? []);
        Assert.NotNull(thinking.StartedAt);

        await agent.WhenIdleAsync();
        var done = folder.Update(agent);
        Assert.DoesNotContain(done.Nodes, n => n.StepStatus == "thinking");
        Assert.Contains(done.Nodes, n => n.Kind == "assistant" && n.StepStatus == "settled");
    }

    [Fact]
    public async Task ThinkingPlaceholder_HidesWhileAToolRuns_TheToolRowCarriesTheProgress()
    {
        var calls = 0;
        await using var harness = TestHarness.Create(_ =>
            ++calls == 1
                ? Scripted.ToolCall("bash", new { command = "sleep 2", description = "Hold the tool open" })
                : Scripted.Text("done"));
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("run it"));
        await WaitForAsync(() => agent.Session.Events.Any(e => e.Type == SessionEventTypes.ToolCall));

        var folder = new ConversationAssembler(harness.Tools).CreateFolder(agent.Session);
        var mid = folder.Update(agent);
        Assert.DoesNotContain(mid.Nodes, n => n.StepStatus == "thinking");
        var tool = mid.Nodes.Single(n => n.Kind == "tool");
        Assert.Equal("running", tool.ToolStatus);
        Assert.NotNull(tool.StartedAt);

        await agent.WhenIdleAsync();
        var done = folder.Update(agent);
        Assert.Equal("done", done.Nodes.Single(n => n.Kind == "tool").ToolStatus);
        Assert.DoesNotContain(done.Nodes, n => n.StepStatus == "thinking");
    }

    [Fact]
    public async Task RepeatedUpdates_DoNotRefoldOrDuplicateTheErrorChip()
    {
        var (harness, session) = Create();
        await using var _ = harness;
        await Task.CompletedTask;
        AppendRaw(session, SessionEventTypes.CompactionSummary, """{"shadowedTokenCount":"not-a-number"}""");

        var folder = new ConversationAssembler(harness.Tools).CreateFolder(session);
        folder.Update(agent: null);
        folder.Update(agent: null); // the page timer ticks whether or not new events arrived
        var snapshot = folder.Update(agent: null);

        Assert.Equal(1, folder.FoldFailures);
        Assert.Equal(1, snapshot.Nodes.Count(n => n.CommandName == "ui"));
    }

    /// <summary>Regression: the compaction pruner re-splices each oversized tool result as a
    /// user/message carrying a ToolResultBlock (Source kind "tool"). Those used to render as
    /// blank "You" bubbles — one per pruned result — right where compaction ran.</summary>
    [Fact]
    public void PrunerSurfaceReplacement_DoesNotRenderAsBlankUserBubble()
    {
        var (harness, session) = Create();
        session.Append(SessionEventTypes.UserMessage, Blazorly.Harness.Llm.Message.CreateUserText("real user text"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));

        // Exactly what CompactionService.PruneAsync appends per pruned result.
        var replacement = Blazorly.Harness.Llm.Message.CreateToolResult("call-1",
            [new Blazorly.Harness.Llm.TextBlock("[tool output pruned: 9001 chars from 'read' to reduce context; re-run the tool if you need it]")]);
        session.Append(SessionEventTypes.UserMessage, replacement, new Session.AppendOptions(
            SourceEventSeqs: [0],
            SurfaceOp: new SurfaceOp.Replace(0, 0)));

        var snapshot = new ConversationAssembler(harness.Tools).Fold(session, agent: null);

        var users = snapshot.Nodes.Where(n => n.Kind == "user").ToList();
        var node = Assert.Single(users);
        Assert.Equal("real user text", node.Message?.FlattenText());
    }

    /// <summary>Appends an event with a raw JSON payload, including shapes no writer would produce.</summary>
    private static void AppendRaw(Session session, string type, string payloadJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
        session.Append(type, doc.RootElement.Clone());
    }

    [Fact]
    public void LatestFailedTurn_NoneFailed_ReturnsNull()
    {
        var session = new Session(new SessionHeader { Id = "s", CreatedAt = 1, Cwd = "/tmp" });
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
        Assert.Null(ConversationAssembler.LatestFailedTurn(session));
    }

    [Fact]
    public void LatestFailedTurn_ReturnsNewestErrorTurn()
    {
        var session = new Session(new SessionHeader { Id = "s", CreatedAt = 1, Cwd = "/tmp" });
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Error("boom", "INVALID_REQUEST")));
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(2));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(2, new TurnEndReason.Completed()));
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(3));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(3, new TurnEndReason.Error("again", "INVALID_REQUEST")));
        Assert.Equal(3, ConversationAssembler.LatestFailedTurn(session));
    }
}
