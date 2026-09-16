using Blazorly.Harness.Core.Sessions;
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

    /// <summary>Appends an event with a raw JSON payload, including shapes no writer would produce.</summary>
    private static void AppendRaw(Session session, string type, string payloadJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
        session.Append(type, doc.RootElement.Clone());
    }
}
