using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Tools;
using System.IO.Compression;
using System.Text.Json;
using Blazorly.Harness.Persistence;
using Blazorly.Harness.Tools;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

public class SessionTests
{
    private static (Session Session, List<SessionEvent> Appended) NewSession()
    {
        var session = new Session(new SessionHeader
        {
            Id = "session-test",
            CreatedAt = 1_000,
            Cwd = "/tmp",
        });
        var appended = new List<SessionEvent>();
        session.Subscribe(appended.Add);
        return (session, appended);
    }

    private static Session OpenTurn(Session session)
    {
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        return session;
    }

    [Fact]
    public void AppendsAreSequentialAndPublished()
    {
        var (session, appended) = NewSession();
        var e = session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        Assert.Equal(0, e.Seq);
        Assert.Equal([e], appended);
    }

    [Fact]
    public void RejectsTurnStartWhenTurnOpen()
    {
        var (session, _) = NewSession();
        OpenTurn(session);
        Assert.Throws<SessionValidationException>(() =>
            session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(2)));
    }

    [Fact]
    public void RejectsToolResultWithoutPendingCall()
    {
        var (session, _) = NewSession();
        OpenTurn(session);
        session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        var result = Message.CreateToolResult("call_missing", [new TextBlock("x")], isError: true);
        Assert.Throws<SessionValidationException>(() =>
            session.Append(SessionEventTypes.ToolResult, new SessionPayloads.ToolResult(1, 1, result),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append())));
    }

    [Fact]
    public void DeriveMessages_ProjectsSurfaceOnly()
    {
        var (session, _) = NewSession();
        OpenTurn(session);
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("hello"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));

        var callId = "call_1";
        session.Append(SessionEventTypes.AssistantChunk,
            new SessionPayloads.AssistantChunk(1, 1, new TextDeltaChunk(0, "hi")));
        var assistant = Message.CreateAssistant("scripted", "demo", [new TextBlock("hi")]);
        session.Append(SessionEventTypes.AssistantMessage, new SessionPayloads.AssistantMessage(1, 1, assistant),
            new Session.AppendOptions(SourceEventSeqs: [2], SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.ToolCall, new SessionPayloads.ToolCall(1, 1, callId, "bash", "{}"));
        var toolMessage = Message.CreateToolResult(callId, [new TextBlock("ok")]);
        session.Append(SessionEventTypes.ToolResult, new SessionPayloads.ToolResult(1, 1, toolMessage),
            new Session.AppendOptions(SourceEventSeqs: [4], SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(1, 1));
        session.Append(SessionEventTypes.TodoWrite, new SessionPayloads.TodoWrite([new TodoItem("t", TodoItem.Pending)]));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));

        var derived = session.DeriveMessages();
        Assert.Equal(3, derived.Count);
        Assert.Equal("user", derived[0].Role);
        Assert.Equal("assistant", derived[1].Role);
        Assert.Equal("user", derived[2].Role);
        var toolResult = Assert.IsType<ToolResultBlock>(derived[2].Content.Single());
        Assert.Equal(callId, toolResult.ToolCallId);
    }

    [Fact]
    public void DeriveMessages_DropsEmptyAssistantContent()
    {
        var (session, _) = NewSession();
        OpenTurn(session);
        session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        var empty = Message.CreateAssistant("scripted", "demo", []);
        session.Append(SessionEventTypes.AssistantMessage,
            new SessionPayloads.AssistantMessage(1, 1, empty, new TokenUsage(5, 0)),
            new Session.AppendOptions(SourceEventSeqs: Array.Empty<int>(), SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(1, 1));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.MaxTokens()));

        Assert.Empty(session.DeriveMessages()); // the event hosts usage but never enters the transcript
    }

    [Fact]
    public void SurfaceReplace_SplicesHistory()
    {
        var (session, _) = NewSession();
        OpenTurn(session);
        var first = session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("first"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        var second = session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("second"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("summary of both"),
            new Session.AppendOptions(SourceEventSeqs: [first.Seq, second.Seq], SurfaceOp: new SurfaceOp.Replace(0, 1)));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));

        var derived = session.DeriveMessages();
        Assert.Single(derived);
        Assert.Equal("summary of both", derived[0].FlattenText());
    }

    [Fact]
    public void Repair_ClosesInterruptedTailWithSyntheticResults()
    {
        var (session, _) = NewSession();
        OpenTurn(session);
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("go"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        var callId = "call_1";
        session.Append(SessionEventTypes.ToolCall, new SessionPayloads.ToolCall(1, 1, callId, "bash", "{}"));
        // crash: no tool/result, no step/end, no turn/end

        var repaired = SessionRepair.Repair(session.Events);
        Assert.Equal(session.Seq + 3, repaired.Count);
        Assert.Equal(SessionEventTypes.ToolResult, repaired[^3].Type);
        Assert.Equal(SessionEventTypes.StepEnd, repaired[^2].Type);
        Assert.Equal(SessionEventTypes.TurnEnd, repaired[^1].Type);
        Assert.IsType<TurnEndReason.Interrupted>(SessionEventRead.TurnEndReasonOf(repaired[^1]));

        // the repaired seed passes validation and derives cleanly
        var reopened = new Session(session.Header, repaired);
        Assert.Equal(2, reopened.DeriveMessages().Count); // user message + synthetic tool result
    }
}

public class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-tests-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task RoundTripsSessions()
    {
        var persistence = new JsonlSessionPersistence(_root);
        var session = new Session(new SessionHeader { Id = "session-p1", CreatedAt = 5_000, Cwd = "/tmp/proj" });
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("persisted"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));

        await persistence.CreateAsync(session.Header);
        await persistence.AppendAsync(session.Id, session.Events);

        var (header, events) = await persistence.LoadAsync(session.Id);
        Assert.Equal(session.Id, header.Id);
        Assert.Equal(session.Events.Count, events.Count);
        Assert.Equal("turn/start", events[0].Type);
        Assert.Equal("persisted", SessionEventRead.MessageOf(events[1]).FlattenText());
        Assert.NotNull(events[1].SurfaceOp);
    }

    [Fact]
    public async Task TornTailIsDiscarded()
    {
        var persistence = new JsonlSessionPersistence(_root);
        var header = new SessionHeader { Id = "session-torn", CreatedAt = 6_000, Cwd = "/tmp/proj" };
        await persistence.CreateAsync(header);
        var file = Directory.EnumerateFiles(_root, "session.jsonl", SearchOption.AllDirectories)
            .Single(f => f.Contains("session-torn"));
        await File.AppendAllTextAsync(file, "{\"type\":\"turn/start\""); // no trailing newline → torn
        var (_, events) = await persistence.LoadAsync(header.Id);
        Assert.Empty(events);
    }

    [Fact]
    public async Task StorePersistsAppendsAndLists()
    {
        await using var ctx = Kernel.HarnessContext.CreateRoot();
        var store = SessionStore.Mount(ctx, new JsonlSessionPersistence(_root));
        var session = store.Create("session-listed", new SessionMeta(Cwd: "/tmp/proj"));
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        await Task.Delay(50); // appends are queued fire-and-forget into the persistence seam
        var listed = await store.ListPersistedAsync();
        Assert.Contains(listed, h => h.Id == "session-listed");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class WorkspaceSandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-ws-" + Guid.NewGuid().ToString("N")[..8]);

    private (TestHarness Harness, Agent Agent, string Cwd) Create(string defaultMode = SandboxPolicy.WorkspaceWrite)
    {
        var cwd = Path.Combine(_root, "project-a");
        Directory.CreateDirectory(cwd);
        var harness = TestHarness.Create(cwd: cwd);
        harness.Sandbox.DefaultMode = defaultMode;
        var agent = harness.CreateAgent(cwd);
        return (harness, agent, cwd);
    }

    private static JsonElement ArgsFor(string path) => JsonSerializer.SerializeToElement(new { file_path = path, content = "x" });

    [Fact]
    public async Task WorkspaceWrite_ConfinesToSessionWorkspace()
    {
        var (harness, agent, cwd) = Create();
        var other = Path.Combine(_root, "project-b");
        Directory.CreateDirectory(other);

        var inside = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "write", Arguments = ArgsFor(Path.Combine(cwd, "inside.txt")), CallId = "c1", Signal = CancellationToken.None, Agent = agent,
        });
        Assert.False(inside.IsError, inside.Error?.Message ?? "no error");

        var outside = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "write", Arguments = ArgsFor(Path.Combine(other, "outside.txt")), CallId = "c2", Signal = CancellationToken.None, Agent = agent,
        });
        Assert.True(outside.IsError);
        Assert.Equal("SANDBOX_DENIED", outside.Error!.Info!.Code);
        Assert.Contains("confined", outside.Error.Message);
    }

    [Fact]
    public async Task SessionOverrideBeatsDeploymentDefault()
    {
        var (harness, agent, cwd) = Create(defaultMode: SandboxPolicy.ReadOnly);
        var blocked = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "write", Arguments = ArgsFor(Path.Combine(cwd, "a.txt")), CallId = "c1", Signal = CancellationToken.None, Agent = agent,
        });
        Assert.True(blocked.IsError);

        // A durable sandbox/mode event flips the preset for this session alone.
        agent.Session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload(SandboxPolicy.DangerFullAccess));
        var allowed = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "write", Arguments = ArgsFor(Path.Combine(cwd, "a.txt")), CallId = "c2", Signal = CancellationToken.None, Agent = agent,
        });
        Assert.False(allowed.IsError, allowed.Error?.Message ?? "no error");
        Assert.Equal(SandboxPolicy.DangerFullAccess, agent.Session.LatestSandboxMode());
    }

    [Fact]
    public void TitleAndModeFold_LatestWins()
    {
        var session = new Session(new SessionHeader { Id = "session-t", CreatedAt = 1, Cwd = "/tmp" });
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.SessionTitle, new SessionPayloads.SessionTitlePayload("first", [], "user"));
        session.Append(SessionEventTypes.SessionTitle, new SessionPayloads.SessionTitlePayload("second", [], "user"));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
        Assert.Equal("second", session.LatestTitle());
    }

    [Fact]
    public async Task CommandEventsRoundTripThroughTheLog()
    {
        var session = new Session(new SessionHeader { Id = "session-c", CreatedAt = 1, Cwd = "/tmp" });
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.CommandRun, new SessionPayloads.CommandRunPayload("/permission", "read-only"));
        session.Append(SessionEventTypes.CommandDone, new SessionPayloads.CommandDonePayload("success", "permission preset switched to read-only"));
        session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload("read-only"));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
        Assert.Equal("read-only", session.LatestSandboxMode());
        Assert.Equal("/permission", SessionEventRead.CommandRunOf(session.Events[1]).Name);
        Assert.Equal("success", SessionEventRead.CommandDoneOf(session.Events[2]).Kind);
    }

    [Fact]
    public async Task QueueMessagesCanBeEditedAndPromoted()
    {
        await using var harness = TestHarness.Create();
        var agent = harness.CreateAgent();
        var first = Message.CreateUserText("draft one");
        agent.Inject(first);
        agent.Inbox.Replace(first, "edited text");
        Assert.Equal("edited text", agent.Inbox.NextStep[0].FlattenText());

        var followup = Message.CreateUserText("queued followup");
        // Insert into next-turn without waking the driver, so the following inbox assertions
        // observe a deterministic snapshot instead of racing the background turn.
        agent.Send(followup, InboxTarget.NextTurn, wakeup: false);
        agent.Inbox.PromoteToNextStep(followup);
        Assert.Empty(agent.Inbox.NextTurn);
        Assert.Equal(2, agent.Inbox.NextStep.Count);

        // The durable splices replay into a rebuilt inbox projection.
        // every mutation is a durable splice: inject + followup + replace(remove+insert) + promote(remove+insert)
        Assert.True(agent.Session.Events.Count(e => e.Type == SessionEventTypes.AgentInboxSpliced) >= 5);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class SessionProjectionTests
{
    private static SessionProjectionService Projections(TestHarness harness)
    {
        var store = harness.Ctx.Get<SessionStore>(SessionStore.ServiceKey);
        return SessionProjectionService.Mount(harness.Ctx, store);
    }

    private static async Task<Agent> RunAgent(TestHarness harness, string userText)
    {
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText(userText));
        await agent.WhenIdleAsync();
        return agent;
    }

    [Fact]
    public async Task Stats_CountsTurnsAndTools()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("done"));
        var agent = await RunAgent(harness, "hello");
        var projections = Projections(harness);

        var stats = projections.Stats(agent.Session);
        Assert.Equal(1, stats.Turns);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(0, stats.Errored);
        Assert.Equal(0, stats.Cancelled);
        Assert.Empty(stats.Tools);
        Assert.True(stats.Events > 0);

        var turns = projections.Turns(agent.Session);
        var first = Assert.Single(turns);
        Assert.Equal(1, first.Turn);
        Assert.Equal("completed", first.Reason);
        Assert.NotNull(first.DurationMs);
    }

    [Fact]
    public async Task Stats_InvalidatesCacheOnNewEvents()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("done"));
        var agent = await RunAgent(harness, "first");
        var projections = Projections(harness);
        Assert.Equal(1, projections.Stats(agent.Session).Turns);

        agent.Followup(Message.CreateUserText("second"));
        await agent.WhenIdleAsync();
        Assert.Equal(2, projections.Stats(agent.Session).Turns);
    }

    [Fact]
    public async Task ProjectAsync_ServesNamedFoldsAndRejectsUnknown()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("done"));
        var agent = await RunAgent(harness, "hello");
        var projections = Projections(harness);

        var (value, through) = await projections.ProjectAsync(agent.Session.Id, "stats");
        Assert.Equal(agent.Session.Events.Count, through);
        Assert.Equal(1, value.GetProperty("turns").GetInt32());

        var missing = await Assert.ThrowsAsync<HarnessException>(
            () => projections.ProjectAsync("session-missing", "stats"));
        Assert.Equal("SESSION_NOT_FOUND", missing.Code);
        var unknown = await Assert.ThrowsAsync<HarnessException>(
            () => projections.ProjectAsync(agent.Session.Id, "nope"));
        Assert.Equal("UNKNOWN_PROJECTION", unknown.Code);
    }

    [Fact]
    public async Task ProjectAsync_FallsBackToPersistedSessions()
    {
        var harness = TestHarness.Create();
        await using var child = harness.Ctx.Extend();
        var persistence = new SessionQueryTests.MemoryPersistence();
        SessionStore.Mount(child, persistence);
        var header = new SessionHeader { Id = "session-projected", CreatedAt = 1_000, Cwd = Directory.GetCurrentDirectory() };
        persistence.Store[header.Id] = (header, new List<SessionEvent>
        {
            new() { Type = SessionEventTypes.TurnStart, Seq = 0, Time = 1_000, Data = SessionJson.ToElement(new SessionPayloads.TurnStart(1)) },
            new() { Type = SessionEventTypes.TurnEnd, Seq = 1, Time = 2_500, Data = SessionJson.ToElement(new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed())) },
        });
        var projections = SessionProjectionService.Mount(child, child.Get<SessionStore>(SessionStore.ServiceKey));
        await using var _ = harness;

        var (value, through) = await projections.ProjectAsync(header.Id, "stats");
        Assert.Equal(2, through);
        Assert.Equal(1, value.GetProperty("turns").GetInt32());
        Assert.Equal(1, value.GetProperty("completed").GetInt32());
        Assert.Equal(1500, value.GetProperty("conversationMs").GetInt64());
    }
}

public class SessionExportTests
{
    private static (SessionHeader Header, List<SessionEvent> Events) SampleLog() => (
        new SessionHeader { Id = "session-export", CreatedAt = 1_000, Cwd = "/tmp/proj" },
        new List<SessionEvent>
        {
            new() { Type = SessionEventTypes.TurnStart, Seq = 0, Time = 1_000, Data = SessionJson.ToElement(new SessionPayloads.TurnStart(1)) },
            new() { Type = SessionEventTypes.UserMessage, Seq = 1, Time = 1_001, Data = SessionJson.ToElement(Message.CreateUserText("export this please")) },
            new()
            {
                Type = SessionEventTypes.ToolCall, Seq = 2, Time = 1_002,
                Data = SessionJson.ToElement(new SessionPayloads.ToolCall(1, 1, "call_1", "bash", """{"command":"echo hi"}""")),
            },
            new()
            {
                Type = SessionEventTypes.ToolResult, Seq = 3, Time = 1_003,
                Data = SessionJson.ToElement(new SessionPayloads.ToolResult(1, 1, Message.CreateToolResult("call_1", [new TextBlock("hi")]), null)),
            },
            new() { Type = SessionEventTypes.TurnEnd, Seq = 4, Time = 2_000, Data = SessionJson.ToElement(new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed())) },
        });

    [Fact]
    public void BuildZip_ContainsLogAndTranscript()
    {
        var (header, events) = SampleLog();
        using var archive = new ZipArchive(new MemoryStream(SessionExport.BuildZip(header, events)));

        var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Equal(["session.jsonl", "transcript.md"], names);

        using var logReader = new StreamReader(archive.GetEntry("session.jsonl")!.Open());
        var lines = logReader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(events.Count + 1, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("session-export", first.RootElement.GetProperty("id").GetString());
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("turn/start", second.RootElement.GetProperty("type").GetString());

        using var mdReader = new StreamReader(archive.GetEntry("transcript.md")!.Open());
        var transcript = mdReader.ReadToEnd();
        Assert.Contains("export this please", transcript);
        Assert.Contains("`bash`", transcript);
        Assert.Contains("hi", transcript);
        Assert.Contains("completed", transcript);
    }
}

public class SessionSearchIndexTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "blazorly-fts-" + Guid.NewGuid().ToString("N")[..8] + ".db");

    public void Dispose()
    {
        try { File.Delete(_db); } catch (IOException) { }
    }

    private static SessionEvent TextEvent(string type, int seq, string text) => new()
    {
        Type = type,
        Seq = seq,
        Time = 1_000 + seq,
        Data = SessionJson.ToElement(Message.CreateUserText(text)),
    };

    private static List<SessionEvent> SampleLog() => new()
    {
        TextEvent(SessionEventTypes.UserMessage, 0, "how do I plant a garden"),
        TextEvent(SessionEventTypes.AssistantMessage, 1, "start with good soil"),
        new()
        {
            Type = SessionEventTypes.ToolCall,
            Seq = 2,
            Time = 1_002,
            Data = SessionJson.ToElement(new SessionPayloads.ToolCall(1, 1, "call_1", "bash", "{}")),
        },
    };

    [Fact]
    public async Task SyncThenSearch_FindsPhrasesWithSeqAndTitle()
    {
        using var index = new SessionSearchIndex(_db);
        var title = await index.SyncSessionAsync("session-fts", SampleLog());
        Assert.Equal("how do I plant a garden", title);

        var hits = await index.SearchAsync("garden", "session-fts");
        Assert.Single(hits);
        Assert.Equal(0, hits[0].Seq);
        Assert.Equal(SessionEventTypes.UserMessage, hits[0].Type);

        var tool = await index.SearchAsync("bash", "session-fts");
        Assert.Single(tool);
        Assert.Equal(2, tool[0].Seq);

        Assert.Empty(await index.SearchAsync("nope-nothing", "session-fts"));
        Assert.Empty(await index.SearchAsync("   ", "session-fts"));
    }

    [Fact]
    public async Task Sync_IsIncrementalWithoutDuplicates()
    {
        using var index = new SessionSearchIndex(_db);
        var log = SampleLog();
        await index.SyncSessionAsync("session-fts", log.Take(2).ToList());
        Assert.Empty(await index.SearchAsync("bash", "session-fts"));

        await index.SyncSessionAsync("session-fts", log);
        Assert.Single(await index.SearchAsync("bash", "session-fts"));
        Assert.Single(await index.SearchAsync("garden", "session-fts"));

        // Re-syncing the same prefix indexes nothing twice.
        await index.SyncSessionAsync("session-fts", log);
        Assert.Single(await index.SearchAsync("garden", "session-fts"));
    }

    [Fact]
    public async Task Search_SurvivesQuotedPhrases()
    {
        using var index = new SessionSearchIndex(_db);
        await index.SyncSessionAsync("session-fts", SampleLog());
        var hits = await index.SearchAsync("plant \"a\" garden", "session-fts");
        Assert.NotNull(hits);
    }

    [Fact]
    public async Task SessionTitleEvent_UpdatesStoredTitle()
    {
        using var index = new SessionSearchIndex(_db);
        var log = SampleLog();
        await index.SyncSessionAsync("session-fts", log);
        log.Add(new SessionEvent
        {
            Type = SessionEventTypes.SessionTitle,
            Seq = 3,
            Time = 1_003,
            Data = SessionJson.ToElement(new SessionPayloads.SessionTitlePayload("Garden tips", [], "generated")),
        });
        var title = await index.SyncSessionAsync("session-fts", log);
        Assert.Equal("Garden tips", title);
    }

    [Fact]
    public async Task PruneSession_RemovesRows()
    {
        using var index = new SessionSearchIndex(_db);
        await index.SyncSessionAsync("session-fts", SampleLog());
        Assert.Single(await index.SearchAsync("garden", "session-fts"));
        await index.PruneSessionAsync("session-fts");
        Assert.Empty(await index.SearchAsync("garden", "session-fts"));
    }

    [Fact]
    public async Task SessionSearch_UsesIndexWhenMounted()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("done"));
        var store = harness.Ctx.Get<SessionStore>(SessionStore.ServiceKey);
        using var index = SessionSearchIndex.Mount(harness.Ctx, store, _db);
        new SessionQueryPlugin().Apply(harness.Ctx);

        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("index the garden needle"));
        await agent.WhenIdleAsync();

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "session_search",
            Arguments = JsonSerializer.SerializeToElement(new { query = "needle" }),
            CallId = "call_test",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.False(result.IsError);
        var match = Assert.Single(result.Value!.Value.GetProperty("matches").EnumerateArray());
        Assert.Equal(agent.Session.Id, match.GetProperty("sessionId").GetString());
        Assert.Contains("needle", match.GetProperty("snippet").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
