using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Sessions;

/// <summary>
/// The append-only session log. Appends validate relational and surface invariants, commit,
/// then notify synchronous observers. Model history derives from the ordered surface only.
/// </summary>
public sealed class Session
{
    private readonly object _gate = new();
    private readonly List<SessionEvent> _log = new();
    private readonly SurfaceManager _surface = new();
    private readonly List<Action<SessionEvent>> _observers = new();

    // Relational state tracked for validation.
    private int _nextTurn = 1;
    private int? _openTurn;
    private int? _openStep;
    private readonly HashSet<string> _pendingCalls = new(StringComparer.Ordinal);

    public Session(SessionHeader header, IEnumerable<SessionEvent>? seed = null)
    {
        Header = header;
        if (seed is not null)
        {
            foreach (var e in seed) Accept(e, publish: false, out _);
        }
    }

    public SessionHeader Header { get; }
    public string Id => Header.Id;

    public int Seq { get { lock (_gate) return _log.Count; } }

    public long LastTime { get { lock (_gate) return _log.Count > 0 ? _log[^1].Time : Header.CreatedAt; } }

    public IReadOnlyList<SessionEvent> Events
    {
        get { lock (_gate) return [.. _log]; }
    }

    /// <summary>Observers must not block; async work belongs behind a queue.</summary>
    public IDisposable Subscribe(Action<SessionEvent> observer)
    {
        lock (_gate) _observers.Add(observer);
        return Kernel.Disposable.Of(() => { lock (_gate) _observers.Remove(observer); });
    }

    public sealed record AppendOptions(int[]? SourceEventSeqs = null, SurfaceOp? SurfaceOp = null, bool? Ignorable = null, long? Time = null);

    public SessionEvent Append(string type, object payload, AppendOptions? options = null)
    {
        Action<SessionEvent>[] observers;
        SessionEvent appended;
        lock (_gate)
        {
            appended = Accept(BuildEvent(type, payload, options), publish: true, observers: out observers);
        }
        foreach (var observer in observers) observer(appended);
        return appended;
    }

    private SessionEvent BuildEvent(string type, object payload, AppendOptions? options)
    {
        return new SessionEvent
        {
            Type = type,
            Seq = _log.Count,
            Time = options?.Time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = SessionJson.ToElement(payload),
            Ignorable = options?.Ignorable,
            SourceEventSeqs = options?.SourceEventSeqs,
            SurfaceOp = options?.SurfaceOp,
        };
    }

    private SessionEvent Accept(SessionEvent e, bool publish, out Action<SessionEvent>[] observers)
    {
        ValidateNext(e);
        _log.Add(e);
        _surface.Apply(e);
        UpdateRelationalState(e);
        observers = publish ? [.. _observers] : [];
        return e;
    }

    private void UpdateRelationalState(SessionEvent e)
    {
        switch (e.Type)
        {
            case SessionEventTypes.TurnStart:
                _openTurn = SessionEventRead.TurnOf(e);
                _nextTurn = _openTurn.Value + 1;
                _openStep = null;
                break;
            case SessionEventTypes.TurnEnd:
                _openTurn = null;
                _openStep = null;
                break;
            case SessionEventTypes.StepStart:
                _openStep = SessionEventRead.StepOf(e);
                break;
            case SessionEventTypes.StepEnd:
                _openStep = null;
                _pendingCalls.Clear();
                break;
        }
    }

    private void ValidateNext(SessionEvent e)
    {
        if (SessionEventTypes.KnownTypes.Contains(e.Type) && e.Ignorable == true)
            throw new SessionValidationException("IGNORABLE_KNOWN", $"known event type '{e.Type}' cannot be ignorable");
        _surface.ValidateNext(e, i => _log[i]);

        switch (e.Type)
        {
            case SessionEventTypes.TurnStart:
            {
                var turn = SessionEventRead.TurnOf(e);
                if (_openTurn is not null) throw new SessionValidationException("TURN_OPEN", "a turn is already open");
                if (turn != _nextTurn) throw new SessionValidationException("TURN_NUMBER", $"turn numbering must be {_nextTurn}");
                break;
            }
            case SessionEventTypes.TurnEnd:
            {
                var turn = SessionEventRead.TurnOf(e);
                if (_openTurn != turn) throw new SessionValidationException("TURN_NOT_OPEN", "turn/end must close the open turn");
                if (_openStep is not null) throw new SessionValidationException("STEP_OPEN", "cannot end a turn with an open step");
                break;
            }
            case SessionEventTypes.StepStart:
            {
                var turn = SessionEventRead.TurnOf(e);
                var step = SessionEventRead.StepOf(e);
                if (_openTurn != turn) throw new SessionValidationException("TURN_NOT_OPEN", "step/start must name the open turn");
                if (_openStep is not null) throw new SessionValidationException("STEP_OPEN", "a step is already open");
                if (step < 1) throw new SessionValidationException("STEP_NUMBER", "step numbering starts at 1");
                break;
            }
            case SessionEventTypes.StepEnd:
            {
                var turn = SessionEventRead.TurnOf(e);
                var step = SessionEventRead.StepOf(e);
                if (_openTurn != turn || _openStep != step) throw new SessionValidationException("STEP_NOT_OPEN", "step/end must close the open step");
                // Pending calls are cleared, not required empty: failure paths may leave
                // dangling calls that resume-time repair closes synthetically.
                break;
            }
            case SessionEventTypes.AssistantChunk:
            case SessionEventTypes.AssistantMessage:
                RequireOpenStep(e);
                break;
            case SessionEventTypes.ToolCall:
            {
                RequireOpenStep(e);
                var call = SessionEventRead.ToolCallOf(e);
                if (!_pendingCalls.Add(call.CallId)) throw new SessionValidationException("CALL_DUP", $"tool call '{call.CallId}' is already pending in this step");
                break;
            }
            case SessionEventTypes.ToolResult:
            {
                RequireOpenStep(e);
                var result = SessionEventRead.ToolResultOf(e);
                var callId = result.Message.Content.OfType<ToolResultBlock>().FirstOrDefault()?.ToolCallId
                    ?? throw new SessionValidationException("TOOL_RESULT_SHAPE", "tool/result must carry exactly one tool-result block");
                if (!_pendingCalls.Remove(callId))
                    throw new SessionValidationException("CALL_NOT_PENDING", "tool/result must answer a pending call of the open step");
                break;
            }
            case SessionEventTypes.TodoWrite:
            case SessionEventTypes.RequestHeader:
            case SessionEventTypes.RequestContext:
                if (_openTurn is null) throw new SessionValidationException("TURN_ENCLOSED", $"'{e.Type}' must be appended inside a turn");
                break;
            case SessionEventTypes.UserMessage:
            case SessionEventTypes.AgentInboxSpliced:
                break;
            default:
                // Unknown (plugin) event types are tolerated; readers skip ignorable ones.
                break;
        }

        void RequireOpenStep(SessionEvent evt)
        {
            var turn = SessionEventRead.TurnOf(evt);
            var step = SessionEventRead.StepOf(evt);
            if (_openTurn != turn || _openStep != step)
                throw new SessionValidationException("STEP_NOT_OPEN", $"'{evt.Type}' must name the open step");
        }
    }

    /// <summary>Model history projected from the surface; the only path into a model request.</summary>
    public IReadOnlyList<Message> DeriveMessages()
    {
        lock (_gate) return _surface.DeriveMessages(seq => _log[seq]);
    }

    /// <summary>Seqs of the current surface, in model-visible order (compaction plans ranges over these).</summary>
    public IReadOnlyList<int> SurfaceSeqs
    {
        get { lock (_gate) return _surface.Surface; }
    }

    /// <summary>Latest request header payload, or null.</summary>
    public SessionPayloads.RequestHeaderPayload? LatestRequestHeader()
    {
        lock (_gate)
        {
            for (var i = _log.Count - 1; i >= 0; i--)
            {
                if (_log[i].Type == SessionEventTypes.RequestHeader)
                    return SessionJson.FromElement<SessionPayloads.RequestHeaderPayload>(_log[i].Data);
            }
            return null;
        }
    }

    /// <summary>Latest todo list (whole-snapshot, latest wins).</summary>
    public IReadOnlyList<TodoItem>? LatestTodos()
    {
        lock (_gate)
        {
            for (var i = _log.Count - 1; i >= 0; i--)
            {
                if (_log[i].Type == SessionEventTypes.TodoWrite)
                    return SessionEventRead.TodosOf(_log[i]);
            }
            return null;
        }
    }

    /// <summary>Latest user/provider title (latest wins), or null when untitled.</summary>
    public string? LatestTitle()
    {
        lock (_gate)
        {
            for (var i = _log.Count - 1; i >= 0; i--)
            {
                if (_log[i].Type == SessionEventTypes.SessionTitle)
                    return SessionEventRead.TitleOf(_log[i]);
            }
            return null;
        }
    }

    /// <summary>Latest session sandbox-mode override, or null when the deployment default applies.</summary>
    public string? LatestSandboxMode()
    {
        lock (_gate)
        {
            for (var i = _log.Count - 1; i >= 0; i--)
            {
                if (_log[i].Type == SessionEventTypes.SandboxMode)
                    return SessionEventRead.SandboxModeOf(_log[i]).Mode;
            }
            return null;
        }
    }

    /// <summary>Boundary seqs where a fork may cut: the last event of each closed turn.</summary>
    public List<int> ForkBoundaries()
    {
        lock (_gate)
        {
            var boundaries = new List<int>();
            for (var i = 0; i < _log.Count; i++)
            {
                if (_log[i].Type == SessionEventTypes.TurnEnd) boundaries.Add(i);
            }
            if (boundaries.Count == 0 || boundaries[^1] != _log.Count - 1) boundaries.Add(_log.Count - 1);
            return boundaries;
        }
    }
}
