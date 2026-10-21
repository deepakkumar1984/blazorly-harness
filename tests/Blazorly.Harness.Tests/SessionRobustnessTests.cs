using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Robustness around the session-log durability and delegation-status seams:
/// a crash-repaired tail must stay writable, failed writes must surface instead of
/// vanishing, and orphaned "running" delegation rows must heal once the child settles.
/// </summary>
public class SessionRobustnessTests
{
    /// <summary>In-memory backend mirroring the SQLite seq contract (batches must continue the stored log).</summary>
    private sealed class SeqEnforcingPersistence : ISessionPersistence
    {
        private readonly Dictionary<string, List<SessionEvent>> _logs = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public bool FailAppends { get; set; }
        public Func<string, Task>? OnFlushAsync { get; set; }

        public int Count(string id)
        {
            lock (_gate) return _logs.GetValueOrDefault(id)?.Count ?? 0;
        }

        public Task CreateAsync(SessionHeader header, CancellationToken ct = default)
        {
            lock (_gate) _logs.TryAdd(header.Id, []);
            return Task.CompletedTask;
        }

        public Task AppendAsync(string sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct = default)
        {
            if (FailAppends) throw new HarnessException("PERSIST_DOWN", "injected write failure");
            lock (_gate)
            {
                if (!_logs.TryGetValue(sessionId, out var log))
                    throw new HarnessException("SESSION_NOT_FOUND", $"no persisted session '{sessionId}'");
                if (events.Count > 0 && events[0].Seq != log.Count)
                    throw new HarnessException("SESSION_SEQ_MISMATCH",
                        $"session '{sessionId}' expects next seq {log.Count} but the batch starts at {events[0].Seq}");
                log.AddRange(events);
            }
            return Task.CompletedTask;
        }

        public Task<(SessionHeader Header, IReadOnlyList<SessionEvent> Events)> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_logs.TryGetValue(sessionId, out var log))
                    throw new HarnessException("SESSION_NOT_FOUND", $"no persisted session '{sessionId}'");
                return Task.FromResult((new SessionHeader { Id = sessionId, CreatedAt = 1, Cwd = "/tmp" },
                    (IReadOnlyList<SessionEvent>)[.. log]));
            }
        }

        public Task<IReadOnlyList<SessionHeader>> ListAsync(CancellationToken ct = default)
        {
            lock (_gate)
                return Task.FromResult((IReadOnlyList<SessionHeader>)[.. _logs.Keys
                    .Select(id => new SessionHeader { Id = id, CreatedAt = 1, Cwd = "/tmp" })]);
        }

        public Task DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_gate) _logs.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task FlushAsync(string sessionId, CancellationToken ct = default)
            => OnFlushAsync is { } hook ? hook(sessionId) : Task.CompletedTask;

        public Task FlushAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task PollAsync(Func<bool> done, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!done() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(done(), $"timed out waiting for: {what}");
    }

    private static IReadOnlyList<SessionPayloads.SubagentStatusPayload> StatusRows(Session session)
        => [.. session.Events
            .Where(e => e.Type == SessionEventTypes.SubagentStatus)
            .Select(SessionEventRead.SubagentStatusOf)];

    [Fact]
    public async Task ReopenedSessionWithRepairedTail_StaysWritable()
    {
        var persistence = new SeqEnforcingPersistence();
        await using var ctx1 = HarnessContext.CreateRoot();
        var store1 = SessionStore.Mount(ctx1, persistence);
        var session = store1.Create("session-repair-tail", new SessionMeta(Cwd: "/tmp"));
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("hi"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        await PollAsync(() => persistence.Count("session-repair-tail") == 3, "initial appends to land");

        // Simulated restart: a fresh store over the same backend reopens the open tail.
        await using var ctx2 = HarnessContext.CreateRoot();
        var store2 = SessionStore.Mount(ctx2, persistence);
        var reopened = await store2.OpenAsync("session-repair-tail");

        // The repair closes (step/end + turn/end) must be durable: without them the stored
        // next-seq lags the live log and every later write is rejected, so the session
        // silently stops persisting while the UI keeps working.
        Assert.Equal(5, persistence.Count("session-repair-tail"));

        reopened.Append(SessionEventTypes.SessionTitle,
            new SessionPayloads.SessionTitlePayload("repaired", [], "test"));
        await PollAsync(() => persistence.Count("session-repair-tail") == 6, "post-reopen append to land");
        Assert.Equal(0, store2.PersistFailureCount("session-repair-tail"));
    }

    [Fact]
    public async Task FailedAppend_EmitsEventAndCounts()
    {
        var persistence = new SeqEnforcingPersistence { FailAppends = true };
        await using var ctx = HarnessContext.CreateRoot();
        var seen = new List<SessionPersistFailed>();
        ctx.Events.On<SessionPersistFailed>("session/persist-failed",
            (p, _) => { seen.Add(p); return Task.CompletedTask; });
        var store = SessionStore.Mount(ctx, persistence);
        var session = store.Create("session-failing", new SessionMeta(Cwd: "/tmp"));
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));

        await PollAsync(() => store.PersistFailureCount("session-failing") == 1, "failure to surface");
        var failure = Assert.Single(seen);
        Assert.Equal("session-failing", failure.SessionId);
        Assert.Equal("turn/start", failure.EventType);
    }

    [Fact]
    public async Task Reconcile_RecordsSettledOutcomeForOrphanedRunningChild()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("unused"));
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();
        var parent = lead.Session;
        var child = harness.Sessions.Create("session-sub-reconciled",
            new SessionMeta(Cwd: parent.Header.Cwd, ParentSession: parent.Id, DelegationDepth: 1));
        parent.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload(child.Id, "reviewer", "running"));
        child.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        child.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        child.Append(SessionEventTypes.AssistantMessage,
            new SessionPayloads.AssistantMessage(1, 1,
                Message.CreateAssistant("scripted", "test", [new TextBlock("review verdict: pass")])),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        child.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(1, 1));
        child.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));

        var healed = await subagents.ReconcileAsync(parent.Id);

        Assert.Equal(1, healed);
        var rows = StatusRows(parent);
        Assert.Equal(2, rows.Count);
        Assert.Equal("finished", rows[1].Status);
        Assert.Contains("review verdict: pass", rows[1].Summary);
    }

    [Fact]
    public async Task Reconcile_LeavesUnsettledChildRunning()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("unused"));
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();
        var parent = lead.Session;
        var child = harness.Sessions.Create("session-sub-pending",
            new SessionMeta(Cwd: parent.Header.Cwd, ParentSession: parent.Id, DelegationDepth: 1));
        parent.Append(SessionEventTypes.SubagentStatus,
            new SessionPayloads.SubagentStatusPayload(child.Id, "worker", "running"));
        child.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));

        var healed = await subagents.ReconcileAsync(parent.Id);

        Assert.Equal(0, healed);
        Assert.Equal("running", Assert.Single(StatusRows(parent)).Status);
    }

    [Fact]
    public async Task BackgroundSpawn_MonitorFailure_RecordsErrorInsteadOfHanging()
    {
        var persistence = new SeqEnforcingPersistence
        {
            OnFlushAsync = _ => Task.FromException(new HarnessException("FLUSH_DOWN", "injected flush failure")),
        };
        await using var harness = TestHarness.Create(
            options => options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("background work done")
                : Scripted.Text("lead reply"),
            persistence: persistence);
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        _ = subagents.SpawnBackgroundAsync(lead, new SubagentRequest(
            Prompt: "work in the background", Description: "bg worker"));

        await PollAsync(() => StatusRows(lead.Session).Count >= 2, "monitor to record an outcome");
        var rows = StatusRows(lead.Session);
        Assert.Equal("running", rows[0].Status);
        Assert.Equal("error", rows[1].Status);
        Assert.Contains("subagent monitor failed", rows[1].Summary);
    }
}
