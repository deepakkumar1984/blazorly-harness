using System.Text.Json;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Sessions;

/// <summary>One tool-call count in a stats projection.</summary>
public sealed record ToolUseCount(string Name, long Count);

/// <summary>Fold of a session log: turn outcomes, wall time, pressure signals, tool usage.</summary>
public sealed record SessionStatsProjection(
    int Turns,
    int Completed,
    int Errored,
    int Cancelled,
    long ConversationMs,
    int Events,
    int Compactions,
    int Retries,
    IReadOnlyList<ToolUseCount> Tools);

/// <summary>One turn's outcome; a null duration means the turn is still running.</summary>
public sealed record TurnSummary(int Turn, string Reason, long? DurationMs);

/// <summary>
/// ctx.projections — named folds over the durable session log with count-keyed caching.
/// The log is append-only, so (sessionId, event count) is a sound cache key; forks get new
/// ids. The same folds back the web stats dock and the /api/session.projection endpoint,
/// so the UI and the API can never disagree.
/// </summary>
public sealed class SessionProjectionService
{
    public const string ServiceKey = "projections";

    private readonly SessionStore _store;
    private readonly Dictionary<string, Func<IReadOnlyList<SessionEvent>, JsonElement>> _folds = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SessionId, string Name), (int Count, object Value)> _cache = new();
    private readonly object _gate = new();

    public SessionProjectionService(SessionStore store)
    {
        _store = store;
        Register("stats", events => SessionJson.ToElement(StatsOf(events)));
        Register("turns", events => SessionJson.ToElement(TurnsOf(events)));
    }

    public static SessionProjectionService Mount(HarnessContext ctx, SessionStore store)
    {
        var service = new SessionProjectionService(store);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    public void Register(string name, Func<IReadOnlyList<SessionEvent>, JsonElement> fold)
    {
        lock (_gate) _folds[name] = fold;
    }

    /// <summary>Named projection over a live or persisted session; cached by event count.</summary>
    public async Task<(JsonElement Value, int ThroughEvents)> ProjectAsync(
        string sessionId, string name, CancellationToken ct = default)
    {
        Func<IReadOnlyList<SessionEvent>, JsonElement> fold;
        lock (_gate)
        {
            if (!_folds.TryGetValue(name, out fold!))
                throw new HarnessException("UNKNOWN_PROJECTION", $"no projection named '{name}'");
        }
        var events = _store.Get(sessionId)?.Events;
        events ??= await LoadPersistedAsync(sessionId, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_cache.TryGetValue((sessionId, name), out var cached) && cached.Count == events.Count)
                return ((JsonElement)cached.Value, events.Count);
            var value = fold(events);
            _cache[(sessionId, name)] = (events.Count, value);
            return (value, events.Count);
        }
    }

    /// <summary>Typed stats fold over a live session (backs the web stats dock); cached.</summary>
    public SessionStatsProjection Stats(Session session)
        => (SessionStatsProjection)Typed(session, "stats", StatsOf);

    /// <summary>Typed per-turn outcomes over a live session; cached.</summary>
    public IReadOnlyList<TurnSummary> Turns(Session session)
        => (IReadOnlyList<TurnSummary>)Typed(session, "turns", TurnsOf);

    private object Typed<T>(Session session, string name, Func<IReadOnlyList<SessionEvent>, T> fold)
    {
        var events = session.Events;
        lock (_gate)
        {
            if (_cache.TryGetValue((session.Id, name), out var cached) && cached.Count == events.Count)
                return cached.Value;
            var value = fold(events)!;
            _cache[(session.Id, name)] = (events.Count, value);
            return value;
        }
    }

    private async Task<IReadOnlyList<SessionEvent>> LoadPersistedAsync(string sessionId, CancellationToken ct)
    {
        if (_store.Persistence is null)
            throw new HarnessException("SESSION_NOT_FOUND", $"session '{sessionId}' is not live and no persistence backend is mounted");
        try
        {
            (_, var events) = await _store.Persistence.LoadAsync(sessionId, ct).ConfigureAwait(false);
            return events;
        }
        catch (HarnessException)
        {
            throw;
        }
        catch
        {
            throw new HarnessException("SESSION_NOT_FOUND", $"session '{sessionId}' could not be loaded");
        }
    }

    internal static SessionStatsProjection StatsOf(IReadOnlyList<SessionEvent> events)
    {
        var turnEnds = 0;
        var completed = 0;
        var errored = 0;
        var cancelled = 0;
        var compactions = 0;
        var retries = 0;
        long durationTotal = 0;
        long turnStart = 0;
        var tools = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case SessionEventTypes.TurnStart:
                    turnStart = e.Time;
                    break;
                case SessionEventTypes.TurnEnd:
                    turnEnds++;
                    durationTotal += Math.Max(0, e.Time - turnStart);
                    switch (SessionEventRead.TurnEndReasonOf(e))
                    {
                        case TurnEndReason.Completed: completed++; break;
                        case TurnEndReason.Error: errored++; break;
                        case TurnEndReason.Interrupted or TurnEndReason.Aborted: cancelled++; break;
                    }
                    break;
                case SessionEventTypes.ToolCall:
                    var name = SessionEventRead.ToolCallOf(e).Name;
                    tools[name] = tools.TryGetValue(name, out var count) ? count + 1 : 1;
                    break;
                case SessionEventTypes.CompactionStart:
                    compactions++;
                    break;
                case SessionEventTypes.LlmRetryStarted:
                    retries++;
                    break;
            }
        }
        return new SessionStatsProjection(turnEnds, completed, errored, cancelled, durationTotal,
            events.Count, compactions, retries,
            [.. tools.OrderByDescending(kv => kv.Value).Take(8).Select(kv => new ToolUseCount(kv.Key, kv.Value))]);
    }

    internal static IReadOnlyList<TurnSummary> TurnsOf(IReadOnlyList<SessionEvent> events)
    {
        var summaries = new List<TurnSummary>();
        var open = new Dictionary<int, long>();
        foreach (var e in events)
        {
            if (e.Type == SessionEventTypes.TurnStart && e.Data.ValueKind == JsonValueKind.Object
                && e.Data.TryGetProperty("turn", out var t) && t.TryGetInt32(out var turn))
            {
                open[turn] = e.Time;
            }
            else if (e.Type == SessionEventTypes.TurnEnd && e.Data.ValueKind == JsonValueKind.Object
                && e.Data.TryGetProperty("turn", out var t2) && t2.TryGetInt32(out var turn2))
            {
                open.TryGetValue(turn2, out var started);
                summaries.Add(new TurnSummary(turn2, ReasonOf(SessionEventRead.TurnEndReasonOf(e)),
                    started > 0 ? Math.Max(0, e.Time - started) : null));
                open.Remove(turn2);
            }
        }
        foreach (var (turn, started) in open.OrderBy(kv => kv.Key))
            summaries.Add(new TurnSummary(turn, "running", null));
        return summaries;
    }

    private static string ReasonOf(TurnEndReason reason) => reason switch
    {
        TurnEndReason.Completed => "completed",
        TurnEndReason.Error => "error",
        TurnEndReason.Interrupted => "interrupted",
        TurnEndReason.Aborted => "aborted",
        TurnEndReason.MaxTokens => "max-tokens",
        TurnEndReason.Blocked => "blocked",
        _ => "unknown",
    };
}
