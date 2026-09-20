using System.Text.Json.Serialization;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Web.Services;
/// <summary>View nodes the transcript renders, folded from durable session events.</summary>
public sealed record ConversationNode
{
    public required string Key { get; init; }
    public required string Kind { get; init; }

    // user
    public Message? Message { get; init; }
    public string Source { get; init; } = "user";

    // assistant step
    public int Turn { get; init; }
    public int Step { get; init; }
    public IReadOnlyList<ContentBlock>? Blocks { get; init; }
    public string StepStatus { get; init; } = "streaming"; // streaming | settled | interrupted
    public TokenUsage? Usage { get; init; }

    // tool activity
    public string? ToolName { get; init; }
    public string? CallId { get; init; }
    public string? ArgsJson { get; init; }
    public string ToolStatus { get; init; } = "running"; // running | done | error
    public string? ResultText { get; init; }
    public ToolCallView? CallView { get; init; }
    public bool IsError { get; init; }
    public long? StartedAt { get; init; }

    // turn end
    public TurnEndReason? Reason { get; init; }
    public long? DurationMs { get; init; }

    /// <summary>Live elapsed label for in-flight activity (running tool rows, the thinking
    /// placeholder): <c>12s</c>, <c>4m 05s</c>. Recomputed on render; the page ticks while running.</summary>
    public static string ElapsedLabel(long startedAtUnixMs)
    {
        var ms = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAtUnixMs);
        return ms >= 60_000 ? $"{ms / 60_000}m {ms / 1_000 % 60:D2}s" : $"{ms / 1_000}s";
    }

    // command row
    public string? CommandName { get; init; }
    public string? CommandArgs { get; init; }
    public string? CommandText { get; init; }
    public bool CommandOk { get; init; }
}

public sealed class ConversationSnapshot
{
    public required ICollection<ConversationNode> Nodes { get; init; }
    public required IReadOnlyList<TodoItem> Todos { get; init; }
    /// <summary>Live delegation progress folded from subagent/status events: latest per child.</summary>
    public IReadOnlyList<DelegationView> Delegations { get; init; } = [];
    public required string Status { get; init; }
    public int LastSeq { get; init; }
    public string? Title { get; init; }
    public string? SandboxMode { get; init; }
    /// <summary>Plan-mode state chip: null (off), "on" (manual), or "auto" (auto-engaged).</summary>
    public string? PlanMode { get; init; }
    public Blazorly.Harness.Core.TokenMeter.ContextMeterReading? Context { get; init; }
}

/// <summary>One delegation row for the parent chat's progress panel: a child agent at work.</summary>
public sealed record DelegationView(
    string ChildSessionId,
    string? Description,
    string Status, // running | finished | error | aborted
    string? Summary);

/// <summary>
/// Folds the durable event stream into transcript nodes: user messages, streaming
/// assistant steps (chunks folded live), tool call cards, and turn-end notices.
/// </summary>

/// <summary>
/// The <c>compaction/summary</c> wire shape written by CompactionService. Every member is optional
/// and every accessor is null-tolerant: this payload is read back from logs written by older builds
/// (which used <c>shadowedSeqs</c>), and a fold that throws blanks the whole session view instead of
/// degrading one chip.
/// </summary>
public sealed record CompactionSummaryPayload(
    string? CompactionId = null,
    string? Summary = null,
    CompactionShadowedRange? ShadowedRange = null,
    IReadOnlyList<int>? ShadowSeqs = null,
    long ShadowedTokenCount = 0,
    string? Provider = null,
    string? Model = null)
{
    /// <summary>The pre-rename spelling, still present in older session logs.</summary>
    [JsonPropertyName("shadowedSeqs")]
    public IReadOnlyList<int>? LegacyShadowedSeqs { get; init; }

    /// <summary>Shadowed seqs under either spelling; never null.</summary>
    public IReadOnlyList<int> Shadowed => ShadowSeqs ?? LegacyShadowedSeqs ?? [];

    /// <summary>Chip label: message count when known, otherwise the token estimate.</summary>
    public string CountLabel => Shadowed.Count > 0
        ? $"{Shadowed.Count} messages"
        : $"{ShadowedTokenCount} tokens";
}

/// <summary>Inclusive surface range the summary replaced.</summary>
public sealed record CompactionShadowedRange(int Start = 0, int End = 0);

public sealed class ConversationAssembler(ToolRuntime tools, Blazorly.Harness.Core.TokenMeter.TokenMeterService? meter = null)
{
    /// <summary>One-shot fold: fresh folder, all events processed.</summary>
    public ConversationSnapshot Fold(Core.Sessions.Session session, Agent? agent)
        => CreateFolder(session).Update(agent);

    /// <summary>Stateful folder for live pages: each Update processes only new events,
    /// so a 100K-event session costs the same per tick as a fresh one.</summary>
    public ConversationFolder CreateFolder(Core.Sessions.Session session) => new(this, session, tools, meter);

    /// <summary>Newest turn that ended in error, if any: failures land out of view when the
    /// reader scrolled up, so the page force-scrolls to newly failed turns like approvals.</summary>
    public static int? LatestFailedTurn(Core.Sessions.Session session)
    {
        int? failed = null;
        foreach (var e in session.Events)
        {
            if (e.Type != SessionEventTypes.TurnEnd) continue;
            if (!e.Data.TryGetProperty("turn", out var turnValue)
                || turnValue.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            var kind = e.Data.TryGetProperty("reason", out var reason)
                && reason.ValueKind == System.Text.Json.JsonValueKind.Object
                && reason.TryGetProperty("kind", out var kindValue)
                && kindValue.ValueKind == System.Text.Json.JsonValueKind.String
                ? kindValue.GetString()
                : null;
            if (kind == "error") failed = failed is null ? turnValue.GetInt32() : Math.Max(failed.Value, turnValue.GetInt32());
        }
        return failed;
    }

    private static int SortKey(ConversationNode node)
    {
        // Nodes carry their originating seq in the key: u-{seq}, a-{seq}, t-{seq}, te-{seq}, live-*
        var parts = node.Key.Split('-');
        if (node.Kind == "assistant" && node.Key.StartsWith("live-")) return int.MaxValue - 1;
        return parts.Length == 2 && int.TryParse(parts[1], out var seq) ? seq : int.MaxValue;
    }
}

/// <summary>Incremental fold state for one live session page.</summary>
public sealed class ConversationFolder
{
    private readonly ConversationAssembler _owner;
    private readonly ToolRuntime _tools;
    private readonly Blazorly.Harness.Core.TokenMeter.TokenMeterService? _meter;
    private readonly Core.Sessions.Session _session;

    private readonly List<ConversationNode> _nodes = [];
    private readonly Dictionary<(int Turn, int Step), BlockAssembler> _assemblers = [];
    private readonly Dictionary<(int Turn, int Step), (string Status, TokenUsage? Usage)> _steps = [];
    private readonly Dictionary<int, (long Time, int Seq)> _turnStart = [];
    private readonly Dictionary<int, int> _turnLastSeen = [];
    private readonly HashSet<int> _endedTurns = [];
    private readonly List<(int Turn, int Step)> _liveKeys = [];
    private int _runningTools;
    /// <summary>Time of the newest event folded so far — the anchor for the thinking
    /// placeholder, so each silent phase measures from the last thing that happened, not
    /// from turn start (a turn's later silences would otherwise inherit a stale timer).</summary>
    private long _lastEventTime;

    private int _processed;
    private int _lastSeq = -1;

    // Context-chip cache: Measure() is O(surface) and ran on every UI tick before this.
    private (long In, long Out, long CacheRead, long CacheWrite) _contextTotals;
    private (int Count, int Generation) _contextDigest;
    private long _contextComputedAt;
    private Blazorly.Harness.Core.TokenMeter.ContextMeterReading? _contextReading;

    /// <summary>Events that could not be folded; each one is rendered as a visible error chip.</summary>
    public int FoldFailures { get; private set; }

    private IReadOnlyList<TodoItem> _todos = [];
    private readonly Dictionary<string, DelegationView> _delegations = new(StringComparer.Ordinal); // child id → latest status
    private long _usageIn, _usageOut, _usageCacheRead, _usageCacheWrite;
    private long? _declaredWindow;
    private ConversationSnapshot? _last;

    internal ConversationFolder(ConversationAssembler owner, Core.Sessions.Session session, ToolRuntime tools,
        Blazorly.Harness.Core.TokenMeter.TokenMeterService? meter)
    {
        _owner = owner;
        _session = session;
        _tools = tools;
        _meter = meter;
    }

    public ConversationSnapshot Update(Agent? agent)
    {
        // Read only the new events: Session.Events copies the whole log on every access,
        // which a 20K-event session pays on every 120ms tick.
        var fresh = false;
        var total = _session.Seq;
        if (_processed < total)
        {
            var batch = _session.ReadEvents(_processed, total - _processed);
            foreach (var e in batch)
            {
                try
                {
                    ProcessEvent(e, agent);
                }
                catch (Exception exception)
                {
                    // One unreadable event must not blank the session: degrade it to a visible chip and
                    // keep folding. The page re-renders on a timer, so a throw here would repeat forever.
                    FoldFailures++;
                    _nodes.Add(new ConversationNode
                    {
                        Key = $"fold-error-{e.Seq}",
                        Kind = "command",
                        CommandName = "ui",
                        CommandArgs = e.Type,
                        CommandText = $"[ui] this {e.Type} event could not be rendered "
                            + $"({exception.GetType().Name}: {exception.Message})",
                        CommandOk = false,
                    });
                }
                _processed++;
                _lastSeq = e.Seq;
                fresh = true;
            }
        }
        if (fresh) _todos = _session.LatestTodos() ?? [];

        // Live tail nodes are regenerated from the retained assemblers each update:
        // drop the previous generation, then re-add the still-streaming steps.
        if (_liveKeys.Count > 0)
        {
            var live = _liveKeys.ToHashSet();
            _nodes.RemoveAll(n => n.Kind == "assistant" && live.Contains((n.Turn, n.Step)) && n.Key.StartsWith("live-"));
            _liveKeys.Clear();
        }
        var agentRunning = agent?.Status == Core.Agent.AgentStatus.Running;
        foreach (var ((turn, step), assembler) in _assemblers.ToList())
        {
            if (_steps.ContainsKey((turn, step))) continue;
            var dead = _endedTurns.Contains(turn) || (!agentRunning && _turnStart.ContainsKey(turn));
            var blocks = assembler.Blocks();
            var interruptedBlocks = assembler.InterruptedBlocks();
            if (blocks.Count == 0 && interruptedBlocks.Count == 0) continue;
            var visible = (blocks.Count > 0 ? blocks : interruptedBlocks)
                .Where(b => b is not ToolCallBlock).ToList();
            if (visible.Count == 0) continue;
            var key = (turn, step);
            if (dead)
            {
                // Aborted mid-stream: no settled message will ever arrive, so whatever streamed
                // (typically a half-finished reasoning block) is an orphan. Rendering it kept a
                // fragment floating at the tail of the transcript below every later turn — drop
                // it and stop tracking the step.
                _assemblers.Remove(key);
                continue;
            }
            _liveKeys.Add(key);
            _nodes.Add(new ConversationNode
            {
                Key = $"live-{turn}-{step}",
                Kind = "assistant",
                Turn = turn,
                Step = step,
                Blocks = visible,
                StepStatus = "streaming",
            });
        }

        // Pre-first-token silence: reasoning models can think for minutes before any block
        // arrives, and the turn otherwise renders nothing at all. While the agent runs with
        // nothing streaming and no tool in flight, keep a live "thinking" placeholder on the
        // tail — the page shows it ticking instead of looking frozen or hung. The timer anchors
        // to the last folded event so each silent phase (between steps, after a tool, after a
        // message) restarts at zero instead of accumulating the whole turn.
        _nodes.RemoveAll(n => n.Key.StartsWith("live-think-", StringComparison.Ordinal));
        if (agentRunning && _liveKeys.Count == 0 && _runningTools == 0 && _turnStart.Count > 0)
        {
            var activeTurn = _turnStart.Keys.Max();
            if (!_endedTurns.Contains(activeTurn) && _turnStart.TryGetValue(activeTurn, out var start))
            {
                var anchor = _lastEventTime > 0 ? _lastEventTime : start.Time;
                _nodes.Add(new ConversationNode
                {
                    Key = $"live-think-{activeTurn}",
                    Kind = "assistant",
                    Turn = activeTurn,
                    Step = 0,
                    Blocks = [],
                    StepStatus = "thinking",
                    StartedAt = anchor,
                });
            }
        }

        // The context reading is expensive (system-prompt assembly + full surface derivation +
        // token estimation). Recompute only when something it depends on changed: a new surface
        // message, a compaction replace, or new usage totals — with a staleness bound for prompt
        // drift (title/todo changes alter the system prompt without touching the surface).
        var totals = (_usageIn, _usageOut, _usageCacheRead, _usageCacheWrite);
        var digest = _session.SurfaceDigest;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_meter is not null && agent is not null
            && (_contextReading is null || digest != _contextDigest || totals != _contextTotals || now - _contextComputedAt > 2000))
        {
            _contextReading = _meter.Measure(agent, totals, _declaredWindow);
            _contextDigest = digest;
            _contextTotals = totals;
            _contextComputedAt = now;
        }
        var context = _meter is not null && agent is not null ? _contextReading : null;

        var plan = new Blazorly.Harness.Tools.PlanModeService().Latest(_session);
        _last = new ConversationSnapshot
        {
            Nodes = [.. _nodes.OrderBy(ConversationAssemblerSort.Key)],
            Todos = _todos,
            Delegations = [.. _delegations.Values],
            Status = agent?.Status ?? "idle",
            LastSeq = _lastSeq,
            Title = _session.LatestTitle(),
            SandboxMode = _session.LatestSandboxMode(),
            PlanMode = plan is { Active: true } ? (plan.Auto == true ? "auto" : "on") : null,
            Context = context,
        };
        return _last;
    }

    private void ProcessEvent(SessionEvent e, Agent? agent)
    {
        if (e.Time > _lastEventTime) _lastEventTime = e.Time;
        if (e.Data.ValueKind == System.Text.Json.JsonValueKind.Object
            && e.Data.TryGetProperty("turn", out var turnValue)
            && turnValue.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            _turnLastSeen[turnValue.GetInt32()] = e.Seq;
        }

        switch (e.Type)
        {
            case SessionEventTypes.SubagentStatus:
            {
                // Log-only delegation progress (children are hidden from the sidebar): the panel
                // in this chat shows the latest state per child. Later events may drop the
                // description — keep the first non-null one.
                var payload = SessionEventRead.SubagentStatusOf(e);
                _delegations[payload.ChildSessionId] = new DelegationView(
                    payload.ChildSessionId,
                    _delegations.TryGetValue(payload.ChildSessionId, out var prior) && prior.Description is not null
                        ? prior.Description
                        : payload.Description,
                    payload.Status,
                    payload.Summary);
                break;
            }
            case SessionEventTypes.UserMessage:
            {
                var message = SessionEventRead.MessageOf(e);
                // plugin = runtime-context snapshots; tool = compaction-pruner surface replacements
                // (ToolResultBlock-only content — FlattenText is "", which used to render as a
                // blank "You" bubble per pruned result). Both are mechanical, never human input:
                // the UI already shows the original tool cards and the real conversation.
                if (message.Source.Kind is "plugin" or "tool") break;
                _nodes.Add(new ConversationNode
                {
                    Key = $"u-{e.Seq}",
                    Kind = "user",
                    Message = message,
                    Source = message.Source.Kind,
                });
                break;
            }
            case SessionEventTypes.AssistantChunk:
            {
                var payload = SessionJson.FromElement<SessionPayloads.AssistantChunk>(e.Data);
                var key = (payload.Turn, payload.Step);
                if (!_assemblers.TryGetValue(key, out var assembler)) _assemblers[key] = assembler = new BlockAssembler();
                assembler.Push(payload.Chunk);
                break;
            }
            case SessionEventTypes.AssistantMessage:
            {
                var payload = SessionEventRead.AssistantMessageOf(e);
                var key = (payload.Turn, payload.Step);
                _steps[key] = (payload.Interrupted == true ? "interrupted" : "settled", payload.Usage);
                if (!_assemblers.TryGetValue(key, out var assembler)) _assemblers[key] = assembler = new BlockAssembler();
                // Tool calls render as their own tool cards; repeating them here is noise.
                var content = payload.Message.Content.Where(b => b is not ToolCallBlock).ToList();
                if (payload.Usage is { } usage)
                {
                    _usageIn += usage.InputTokens;
                    _usageOut += usage.OutputTokens;
                    _usageCacheRead += usage.CacheReadTokens ?? 0;
                    _usageCacheWrite += usage.CacheWriteTokens ?? 0;
                }
                if (content.Count > 0)
                {
                    _nodes.Add(new ConversationNode
                    {
                        Key = $"a-{e.Seq}",
                        Kind = "assistant",
                        Turn = payload.Turn,
                        Step = payload.Step,
                        Blocks = content,
                        StepStatus = payload.Interrupted == true ? "interrupted" : "settled",
                        Usage = payload.Usage,
                    });
                }
                break;
            }
            case SessionEventTypes.ToolCall:
            {
                var call = SessionEventRead.ToolCallOf(e);
                var definition = _tools.Get(call.Name, agent?.ScopeKey);
                ToolCallView? view = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments);
                    view = definition?.PresentCall(doc.RootElement.Clone());
                }
                catch
                {
                    view = null;
                }
                _nodes.Add(new ConversationNode
                {
                    Key = $"t-{e.Seq}",
                    Kind = "tool",
                    Turn = call.Turn,
                    Step = call.Step,
                    ToolName = call.Name,
                    CallId = call.CallId,
                    ArgsJson = call.Arguments,
                    ToolStatus = "running",
                    CallView = view,
                    StartedAt = e.Time,
                });
                _runningTools++;
                break;
            }
            case SessionEventTypes.ToolResult:
            {
                var result = SessionEventRead.ToolResultOf(e);
                var callId = result.Message.Content.OfType<ToolResultBlock>().First().ToolCallId;
                var target = _nodes.FirstOrDefault(n => n.Kind == "tool" && n.CallId == callId && n.ToolStatus == "running");
                if (target is not null)
                {
                    var text = string.Join("\n", result.Message.Content.OfType<ToolResultBlock>().First().Content
                        .OfType<TextBlock>().Select(b => b.Text));
                    _nodes[_nodes.IndexOf(target)] = target with
                    {
                        ToolStatus = result.Error is not null ? "error" : "done",
                        ResultText = text,
                        IsError = result.Error is not null,
                        DurationMs = target.StartedAt is { } started ? e.Time - started : null,
                    };
                    _runningTools--;
                }
                break;
            }
            case SessionEventTypes.TurnStart:
                _turnStart[SessionEventRead.TurnOf(e)] = (e.Time, e.Seq);
                break;
            case SessionEventTypes.TurnEnd:
            {
                var turn = SessionEventRead.TurnOf(e);
                _endedTurns.Add(turn);
                var reason = SessionEventRead.TurnEndReasonOf(e);
                long? duration = _turnStart.TryGetValue(turn, out var started) ? e.Time - started.Time : null;
                if (reason is TurnEndReason.Completed)
                {
                    var usage = _steps.Values.Select(s => s.Usage).LastOrDefault(u => u is not null);
                    _nodes.Add(new ConversationNode { Key = $"te-{e.Seq}", Kind = "turn-ok", Turn = turn, Usage = usage, DurationMs = duration });
                }
                else
                {
                    _nodes.Add(new ConversationNode { Key = $"te-{e.Seq}", Kind = "turn-end", Turn = turn, Reason = reason, DurationMs = duration });
                }
                break;
            }
            case SessionEventTypes.CommandRun:
            {
                var run = SessionEventRead.CommandRunOf(e);
                _nodes.Add(new ConversationNode
                {
                    Key = $"cr-{e.Seq}",
                    Kind = "command",
                    CommandName = run.Name,
                    CommandArgs = run.Args,
                });
                break;
            }
            case SessionEventTypes.CommandDone:
            {
                var done = SessionEventRead.CommandDoneOf(e);
                var last = _nodes.LastOrDefault(n => n.Kind == "command" && n.CommandText is null);
                if (last is not null)
                {
                    _nodes[_nodes.IndexOf(last)] = last with { CommandText = done.Text, CommandOk = done.Kind == "success" };
                }
                break;
            }
            case SessionEventTypes.RequestContext:
            {
                var payload = SessionJson.FromElement<SessionPayloads.RequestContextPayload>(e.Data);
                _declaredWindow = payload.ContextWindow; // latest declaration wins
                break;
            }
            case SessionEventTypes.CompactionSummary:
            {
                var summary = SessionJson.FromElement<CompactionSummaryPayload>(e.Data);
                _nodes.Add(new ConversationNode
                {
                    Key = $"cp-{e.Seq}",
                    Kind = "command",
                    CommandName = "compaction",
                    CommandArgs = summary.CountLabel,
                    CommandText = $"Context compacted: {summary.CountLabel} were summarized to stay within the window"
                        + (summary.ShadowedTokenCount > 0 ? $" (~{summary.ShadowedTokenCount} tokens)." : "."),
                    CommandOk = true,
                });
                break;
            }
            case SessionEventTypes.SandboxMode:
            {
                var mode = SessionEventRead.SandboxModeOf(e);
                _nodes.Add(new ConversationNode
                {
                    Key = $"sm-{e.Seq}",
                    Kind = "command",
                    CommandName = "permission",
                    CommandArgs = mode.Mode,
                    CommandText = $"permission preset switched to {mode.Mode}",
                    CommandOk = true,
                });
                break;
            }
        }
    }
}

internal static class ConversationAssemblerSort
{
    /// <summary>Live tail nodes render last; everything else orders by originating seq.</summary>
    public static int Key(ConversationNode node)
    {
        var parts = node.Key.Split('-');
        if (node.Kind == "assistant" && node.Key.StartsWith("live-")) return int.MaxValue - 1;
        return parts.Length == 2 && int.TryParse(parts[1], out var seq) ? seq : int.MaxValue;
    }
}
