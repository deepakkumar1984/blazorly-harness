using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Sessions;

/// <summary>The persistence seam: backends persist the same SessionEvent vocabulary.</summary>
public interface ISessionPersistence
{
    /// <summary>Writes the header record; may materialize lazily.</summary>
    Task CreateAsync(SessionHeader header, CancellationToken ct = default);

    /// <summary>Appends events; resolves only after durability. First batch seq must equal the stored next-seq.</summary>
    Task AppendAsync(string sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct = default);

    /// <summary>Loads the raw stored log without repair.</summary>
    Task<(SessionHeader Header, IReadOnlyList<SessionEvent> Events)> LoadAsync(string sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<SessionHeader>> ListAsync(CancellationToken ct = default);

    /// <summary>Durability barrier for one session.</summary>
    Task FlushAsync(string sessionId, CancellationToken ct = default);

    Task FlushAllAsync(CancellationToken ct = default);
}

public sealed record SessionMeta(string? Cwd = null, string? ParentSession = null, int DelegationDepth = 0, string? AgentPreset = null);

/// <summary>ctx.sessions — the in-memory session store and durable event feed.</summary>
public sealed class SessionStore
{
    public const string ServiceKey = "sessions";

    private readonly HarnessContext _ctx;
    private readonly Dictionary<string, Session> _live = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SessionStore(HarnessContext ctx, ISessionPersistence? persistence = null)
    {
        _ctx = ctx;
        Persistence = persistence;
    }

    public ISessionPersistence? Persistence { get; }

    public static SessionStore Mount(HarnessContext ctx, ISessionPersistence? persistence = null)
    {
        var store = new SessionStore(ctx, persistence);
        ctx.Provide(ServiceKey, store);
        return store;
    }

    public Session Create(string? id = null, SessionMeta? meta = null)
    {
        meta ??= new SessionMeta();
        var sessionId = id ?? Ids.NewSessionId();
        var header = new SessionHeader
        {
            Id = sessionId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Cwd = meta.Cwd,
            ParentSession = meta.ParentSession,
            DelegationDepth = meta.DelegationDepth,
            AgentPreset = meta.AgentPreset,
        };
        var session = new Session(header);
        if (Persistence is not null) Persistence.CreateAsync(header).GetAwaiter().GetResult();
        Attach(session, created: true);
        return session;
    }

    /// <summary>Opens a persisted session: load, crash-repair an interrupted tail, attach live.</summary>
    public async Task<Session> OpenAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(sessionId, out var existing)) return existing;
        }
        if (Persistence is null)
            throw new Kernel.HarnessException("NO_PERSISTENCE", "no persistence backend is mounted");
        var (header, events) = await Persistence.LoadAsync(sessionId, ct).ConfigureAwait(false);
        var repaired = SessionRepair.Repair(events);
        var session = new Session(header, repaired);
        Attach(session, created: false);
        return session;
    }

    public async Task<IReadOnlyList<SessionHeader>> ListPersistedAsync(CancellationToken ct = default)
        => Persistence is null ? [] : await Persistence.ListAsync(ct).ConfigureAwait(false);

    private void Attach(Session session, bool created)
    {
        lock (_gate) _live[session.Id] = session;
        session.Subscribe(e =>
        {
            if (Persistence is not null)
            {
                _ = Persistence.AppendAsync(session.Id, [e]);
            }
            _ = _ctx.Events.EmitAsync("session/event", new SessionEventNotification(session, e), session);
        });
        _ = _ctx.Events.EmitAsync(created ? "session/created" : "session/resumed", session);
    }

    public Session? Get(string id)
    {
        lock (_gate) return _live.GetValueOrDefault(id);
    }

    public IReadOnlyList<Session> LiveSessions()
    {
        lock (_gate) return [.. _live.Values];
    }

    /// <summary>Forks a session at a boundary (inclusive seq); the child log is the prefix [0..boundary].</summary>
    public Session Fork(string sourceId, int? boundary = null, string? childId = null)
    {
        Session source;
        lock (_gate)
        {
            if (!_live.TryGetValue(sourceId, out source!))
                throw new Kernel.HarnessException("SESSION_NOT_FOUND", $"session '{sourceId}' is not live");
        }
        var events = source.Events;
        var cut = boundary ?? events.Count - 1;
        if (cut < 0 || cut >= events.Count) throw new Kernel.HarnessException("INVALID_BOUNDARY", "fork boundary out of range");
        var prefix = events.Take(cut + 1).ToList();
        if (prefix.Any(e => e.Type == SessionEventTypes.TurnStart) && prefix.Count(e => e.Type == SessionEventTypes.TurnStart) > prefix.Count(e => e.Type == SessionEventTypes.TurnEnd))
            throw new Kernel.HarnessException("OPEN_TURN", "fork boundary lands inside an open turn");

        var child = new Session(new SessionHeader
        {
            Id = childId ?? Ids.NewSessionId(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Cwd = source.Header.Cwd,
            ParentSession = source.Id,
            DelegationDepth = source.Header.DelegationDepth + 1,
            AgentPreset = source.Header.AgentPreset,
            SeedLength = prefix.Count,
        }, prefix);
        if (Persistence is not null)
        {
            Persistence.CreateAsync(child.Header).GetAwaiter().GetResult();
            Persistence.AppendAsync(child.Id, prefix).GetAwaiter().GetResult();
        }
        Attach(child, created: true);
        return child;
    }
}

public sealed record SessionEventNotification(Session Session, SessionEvent Event);

/// <summary>Closes an interrupted log tail with synthetic outcomes so replay stays valid.</summary>
public static class SessionRepair
{
    public static IReadOnlyList<SessionEvent> Repair(IReadOnlyList<SessionEvent> events)
    {
        var repaired = new List<SessionEvent>(events);
        if (repaired.Count == 0) return repaired;

        int? openTurn = null, openStep = null;
        var pendingCalls = new List<SessionPayloads.ToolCall>();
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case SessionEventTypes.TurnStart:
                    openTurn = SessionEventRead.TurnOf(e);
                    openStep = null;
                    pendingCalls.Clear();
                    break;
                case SessionEventTypes.TurnEnd:
                    openTurn = null;
                    openStep = null;
                    pendingCalls.Clear();
                    break;
                case SessionEventTypes.StepStart:
                    openStep = SessionEventRead.StepOf(e);
                    pendingCalls.Clear();
                    break;
                case SessionEventTypes.StepEnd:
                    openStep = null;
                    pendingCalls.Clear();
                    break;
                case SessionEventTypes.ToolCall:
                    pendingCalls.Add(SessionEventRead.ToolCallOf(e));
                    break;
                case SessionEventTypes.ToolResult:
                {
                    var result = SessionEventRead.ToolResultOf(e);
                    var callId = result.Message.Content.OfType<Llm.ToolResultBlock>().FirstOrDefault()?.ToolCallId;
                    if (callId is not null) pendingCalls.RemoveAll(c => c.CallId == callId);
                    break;
                }
            }
        }

        if (openTurn is null) return repaired;
        var time = events[^1].Time;
        var seq = repaired.Count;

        foreach (var call in pendingCalls)
        {
            var message = Llm.Message.CreateToolResult(
                call.CallId,
                [new Llm.TextBlock("Error: the harness stopped before this tool call's outcome was recorded. Treat the outcome as unknown; retry if still needed.")],
                isError: true);
            repaired.Add(new SessionEvent
            {
                Type = SessionEventTypes.ToolResult,
                Seq = seq++,
                Time = time,
                Data = SessionJson.ToElement(new SessionPayloads.ToolResult(call.Turn, call.Step, message,
                    new SessionPayloads.ToolErrorInfo("ToolOutcomeUnknownError", "TOOL_OUTCOME_UNKNOWN"))),
                SurfaceOp = new SurfaceOp.Append(),
                SourceEventSeqs = [FindCallSeq(events, call.CallId)],
            });
        }
        if (openStep is not null)
        {
            repaired.Add(new SessionEvent
            {
                Type = SessionEventTypes.StepEnd,
                Seq = seq++,
                Time = time,
                Data = SessionJson.ToElement(new SessionPayloads.StepEnd(openTurn.Value, openStep.Value)),
            });
        }
        repaired.Add(new SessionEvent
        {
            Type = SessionEventTypes.TurnEnd,
            Seq = seq++,
            Time = time,
            Data = SessionJson.ToElement(new SessionPayloads.TurnEnd(openTurn.Value, new TurnEndReason.Interrupted())),
        });
        return repaired;
    }

    private static int FindCallSeq(IReadOnlyList<SessionEvent> events, string callId)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Type == SessionEventTypes.ToolCall && SessionEventRead.ToolCallOf(events[i]).CallId == callId)
                return events[i].Seq;
        }
        return 0;
    }
}
