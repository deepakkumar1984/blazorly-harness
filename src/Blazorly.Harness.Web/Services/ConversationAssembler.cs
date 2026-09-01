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
    public Blazorly.Harness.Core.TokenMeter.ContextMeterReading? Context { get; init; }
}

/// <summary>
/// Folds the durable event stream into transcript nodes: user messages, streaming
/// assistant steps (chunks folded live), tool call cards, and turn-end notices.
/// </summary>
public sealed record CompactionSummaryPayload(string Summary, IReadOnlyList<int> ShadowedSeqs);

public sealed class ConversationAssembler(ToolRuntime tools, Blazorly.Harness.Core.TokenMeter.TokenMeterService? meter = null)
{
    public ConversationSnapshot Fold(Core.Sessions.Session session, Agent? agent)
    {
        var events = session.Events;
        var nodes = new List<ConversationNode>();
        var assemblers = new Dictionary<(int Turn, int Step), BlockAssembler>();
        var steps = new Dictionary<(int Turn, int Step), (string Status, TokenUsage? Usage)>();
        var turnStart = new Dictionary<int, long>();
        var todos = session.LatestTodos() ?? [];

        foreach (var e in events)
        {
            switch (e.Type)
            {
                case SessionEventTypes.UserMessage:
                {
                    var message = SessionEventRead.MessageOf(e);
                    if (message.Source.Kind == "plugin") continue; // runtime-context snapshots stay hidden
                    nodes.Add(new ConversationNode
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
                    if (!assemblers.TryGetValue(key, out var assembler)) assemblers[key] = assembler = new BlockAssembler();
                    assembler.Push(payload.Chunk);
                    break;
                }
                case SessionEventTypes.AssistantMessage:
                {
                    var payload = SessionEventRead.AssistantMessageOf(e);
                    var key = (payload.Turn, payload.Step);
                    steps[key] = (payload.Interrupted == true ? "interrupted" : "settled", payload.Usage);
                    if (!assemblers.TryGetValue(key, out var assembler)) assemblers[key] = assembler = new BlockAssembler();
                    if (payload.Message.Content.Count > 0)
                    {
                        nodes.Add(new ConversationNode
                        {
                            Key = $"a-{e.Seq}",
                            Kind = "assistant",
                            Turn = payload.Turn,
                            Step = payload.Step,
                            Blocks = payload.Message.Content,
                            StepStatus = payload.Interrupted == true ? "interrupted" : "settled",
                            Usage = payload.Usage,
                        });
                    }
                    break;
                }
                case SessionEventTypes.ToolCall:
                {
                    var call = SessionEventRead.ToolCallOf(e);
                    var definition = tools.Get(call.Name, agent?.ScopeKey);
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
                    nodes.Add(new ConversationNode
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
                    });
                    break;
                }
                case SessionEventTypes.ToolResult:
                {
                    var result = SessionEventRead.ToolResultOf(e);
                    var callId = result.Message.Content.OfType<ToolResultBlock>().First().ToolCallId;
                    var target = nodes.FirstOrDefault(n => n.Kind == "tool" && n.CallId == callId && n.ToolStatus == "running");
                    if (target is not null)
                    {
                        var text = string.Join("\n", result.Message.Content.OfType<ToolResultBlock>().First().Content
                            .OfType<TextBlock>().Select(b => b.Text));
                        nodes[nodes.IndexOf(target)] = target with
                        {
                            ToolStatus = result.Error is not null ? "error" : "done",
                            ResultText = text,
                            IsError = result.Error is not null,
                        };
                    }
                    break;
                }
                case SessionEventTypes.TurnStart:
                    turnStart[SessionEventRead.TurnOf(e)] = e.Time;
                    break;
                case SessionEventTypes.TurnEnd:
                {
                    var turn = SessionEventRead.TurnOf(e);
                    var reason = SessionEventRead.TurnEndReasonOf(e);
                    long? duration = turnStart.TryGetValue(turn, out var started) ? e.Time - started : null;
                    if (reason is TurnEndReason.Completed)
                    {
                        var usage = steps.Values.Select(s => s.Usage).LastOrDefault(u => u is not null);
                        nodes.Add(new ConversationNode { Key = $"te-{e.Seq}", Kind = "turn-ok", Turn = turn, Usage = usage, DurationMs = duration });
                    }
                    else
                    {
                        nodes.Add(new ConversationNode { Key = $"te-{e.Seq}", Kind = "turn-end", Turn = turn, Reason = reason, DurationMs = duration });
                    }
                    break;
                }
                case SessionEventTypes.CommandRun:
                {
                    var run = SessionEventRead.CommandRunOf(e);
                    nodes.Add(new ConversationNode
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
                    var last = nodes.LastOrDefault(n => n.Kind == "command" && n.CommandText is null);
                    if (last is not null)
                    {
                        nodes[nodes.IndexOf(last)] = last with { CommandText = done.Text, CommandOk = done.Kind == "success" };
                    }
                    break;
                }
                case SessionEventTypes.CompactionSummary:
                {
                    var summary = SessionJson.FromElement<CompactionSummaryPayload>(e.Data);
                    nodes.Add(new ConversationNode
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
                    nodes.Add(new ConversationNode
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

        // Any still-streaming step (chunks without a terminal message) renders live at the tail.
        foreach (var ((turn, step), assembler) in assemblers)
        {
            if (steps.ContainsKey((turn, step))) continue;
            var blocks = assembler.Blocks();
            if (blocks.Count == 0 && assembler.InterruptedBlocks().Count == 0) continue;
            nodes.Add(new ConversationNode
            {
                Key = $"live-{turn}-{step}",
                Kind = "assistant",
                Turn = turn,
                Step = step,
                Blocks = blocks.Count > 0 ? blocks : assembler.InterruptedBlocks(),
                StepStatus = "streaming",
            });
        }

        return new ConversationSnapshot
        {
            Nodes = [.. nodes.OrderBy(n => SortKey(n))],
            Todos = todos,
            Status = agent?.Status ?? "idle",
            LastSeq = events.Count > 0 ? events[^1].Seq : -1,
            Title = session.LatestTitle(),
            SandboxMode = session.LatestSandboxMode(),
            Context = meter is not null && agent is not null ? meter.Measure(agent) : null,
        };
    }

    private static int SortKey(ConversationNode node)
    {
        // Nodes carry their originating seq in the key: u-{seq}, a-{seq}, t-{seq}, te-{seq}, live-*
        var parts = node.Key.Split('-');
        if (node.Kind == "assistant" && node.Key.StartsWith("live-")) return int.MaxValue - 1;
        return parts.Length == 2 && int.TryParse(parts[1], out var seq) ? seq : int.MaxValue;
    }
}
