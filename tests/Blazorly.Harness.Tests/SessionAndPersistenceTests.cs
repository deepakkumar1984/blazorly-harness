using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Tools;
using System.Text.Json;
using Blazorly.Harness.Persistence;
using Blazorly.Harness.Tools;
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
        var assistant = Message.CreateAssistant("replay", "demo", [new TextBlock("hi")]);
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
        var empty = Message.CreateAssistant("replay", "demo", []);
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
