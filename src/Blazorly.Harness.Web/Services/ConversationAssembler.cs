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

    // command row
    public string? CommandName { get; init; }
    public string? CommandArgs { get; init; }
    public string? CommandText { get; init; }
    public bool CommandOk { get; init; }
}

public sealed class ConversationSnapshot
{
    public required IReadOnlyList<ConversationNode> Nodes { get; init; }
    public required IReadOnlyList<TodoItem> Todos { get; init; }
    public required string Status { get; init; }
    public int LastSeq { get; init; }
    public string? Title { get; init; }
    public string? SandboxMode { get; init; }
    /// <summary>Plan-mode state chip: null (off), "on" (manual), or "auto" (auto-engaged).</summary>
    public string? PlanMode { get; init; }
    public Blazorly.Harness.Core.TokenMeter.ContextMeterReading? Context { get; init; }
}

/// <summary>
/// Folds the durable event stream into transcript nodes: user messages, streaming
/// assistant steps (chunks folded live), tool call cards, and turn-end notices.
/// </summary>
public sealed record CompactionSummaryPayload(string Summary, IReadOnlyList<int> ShadowedSeqs);

public sealed class ConversationAssembler(ToolRuntime tools, Blazorly.Harness.Core.TokenMeter.TokenMeterService? meter = null)
{
    /// <summary>One-shot fold: fresh folder, all events processed.</summary>
    public ConversationSnapshot Fold(Core.Sessions.Session session, Agent? agent)
        => CreateFolder(session).Update(agent);

    /// <summary>Stateful folder for live pages: each Update processes only new events,
    /// so a 100K-event session costs the same per tick as a fresh one.</summary>
    public ConversationFolder CreateFolder(Core.Sessions.Session session) => new(this, session, tools, meter);

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

    private int _processed;
    private IReadOnlyList<TodoItem> _todos = [];
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
        var events = _session.Events;
        var fresh = false;
        while (_processed < events.Count)
        {
            ProcessEvent(events[_processed], agent);
            _processed++;
            fresh = true;
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
        foreach (var ((turn, step), assembler) in _assemblers)
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
            _liveKeys.Add(key);
            _nodes.Add(new ConversationNode
            {
                Key = $"live-{turn}-{step}",
                Kind = "assistant",
                Turn = turn,
                Step = step,
                Blocks = visible,
                StepStatus = dead ? "interrupted" : "streaming",
            });
        }

        var context = _meter is not null && agent is not null
            ? _meter.Measure(agent, (_usageIn, _usageOut, _usageCacheRead, _usageCacheWrite), _declaredWindow)
            : null;

        var plan = new Blazorly.Harness.Tools.PlanModeService().Latest(_session);
        _last = new ConversationSnapshot
        {
            Nodes = [.. _nodes.OrderBy(ConversationAssemblerSort.Key)],
            Todos = _todos,
            Status = agent?.Status ?? "idle",
            LastSeq = events.Count > 0 ? events[^1].Seq : -1,
            Title = _session.LatestTitle(),
            SandboxMode = _session.LatestSandboxMode(),
            PlanMode = plan is { Active: true } ? (plan.Auto == true ? "auto" : "on") : null,
            Context = context,
        };
        return _last;
    }

    private void ProcessEvent(SessionEvent e, Agent? agent)
    {
        if (e.Data.ValueKind == System.Text.Json.JsonValueKind.Object
            && e.Data.TryGetProperty("turn", out var turnValue)
            && turnValue.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            _turnLastSeen[turnValue.GetInt32()] = e.Seq;
        }

        switch (e.Type)
        {
            case SessionEventTypes.UserMessage:
            {
                var message = SessionEventRead.MessageOf(e);
                if (message.Source.Kind == "plugin") break; // runtime-context snapshots stay hidden
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
                    CommandArgs = $"{summary.ShadowedSeqs.Count} messages",
                    CommandText = "Context compacted: earlier conversation was summarized to keep working within the window.",
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
