using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Web.Services;

public sealed record TrajectoryRow(
    string Kind,
    string Glyph,
    string Label,
    string? Detail,
    string? Status = null,
    double? DurationMs = null,
    string? ArgsJson = null,
    string? ResultText = null,
    string? Meta = null);

public sealed record TrajectoryStepRow(int Turn, int Step, IReadOnlyList<TrajectoryRow> Rows);

public sealed record TrajectoryTurnRow(int Turn, string Status, double DurationMs, IReadOnlyList<TrajectoryStepRow> Steps);

public sealed record TrajectoryModel(IReadOnlyList<TrajectoryRow> Preamble, IReadOnlyList<TrajectoryTurnRow> Turns);

/// <summary>
/// Folds the durable event log into a trajectory timeline: pre-turn preamble, then
/// turns → steps → rows, with tool call/result pairs joined into one row. Pure fold —
/// no agent or service access — so it is safe to rebuild on every coalesced refresh.
/// </summary>
public static class TrajectoryBuilder
{
    private const int PreviewLimit = 140;

    public static TrajectoryModel Build(IReadOnlyList<SessionEvent> events)
    {
        var preamble = new List<TrajectoryRow>();
        var turns = new List<TurnBuild>();
        TurnBuild? turn = null;
        StepBuild? step = null;
        var pendingCalls = new Dictionary<string, (SessionEvent Call, StepBuild Step)>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            switch (e.Type)
            {
                case SessionEventTypes.TurnStart:
                    turn = new TurnBuild(e.Data.GetProperty("turn").GetInt32(), e.Time);
                    turns.Add(turn);
                    step = null;
                    continue;
                case SessionEventTypes.StepStart:
                    if (turn is null) continue;
                    step = new StepBuild(e.Data.GetProperty("step").GetInt32());
                    turn.Steps.Add(step);
                    continue;
                case SessionEventTypes.StepEnd:
                    continue;
                case SessionEventTypes.TurnEnd:
                    if (turn is not null)
                    {
                        turn.Status = DescribeReason(SessionEventRead.TurnEndReasonOf(e));
                        turn.DurationMs = Math.Max(0, e.Time - turn.StartTime);
                    }
                    turn = null;
                    step = null;
                    continue;
                case SessionEventTypes.ToolCall:
                {
                    var call = SessionEventRead.ToolCallOf(e);
                    if (step is not null && call.CallId is { Length: > 0 })
                        pendingCalls[call.CallId] = (e, step);
                    continue;
                }
                case SessionEventTypes.ToolResult:
                {
                    var target = step ?? turn?.OpenTail();
                    if (target is null) continue;
                    var payload = SessionEventRead.ToolResultOf(e);
                    var resultBlock = payload.Message.Content.OfType<ToolResultBlock>().FirstOrDefault();
                    var callId = resultBlock?.ToolCallId ?? "";
                    var failed = resultBlock?.IsError == true || payload.Error is not null;
                    var text = string.Join("\n", resultBlock?.Content.OfType<TextBlock>().Select(b => b.Text) ?? []);
                    var name = callId;
                    string? argsJson = null;
                    double? duration = null;
                    if (pendingCalls.Remove(callId, out var pending))
                    {
                        var call = SessionEventRead.ToolCallOf(pending.Call);
                        name = call.Name;
                        argsJson = call.Arguments;
                        duration = Math.Max(0, e.Time - pending.Call.Time);
                    }
                    target.Rows.Add(new TrajectoryRow(
                        "tool",
                        failed ? "✕" : "✓",
                        name,
                        OneLine(text),
                        failed ? "error" : "completed",
                        duration,
                        argsJson,
                        text.Length > 0 ? text : null));
                    continue;
                }
                case SessionEventTypes.UserMessage:
                {
                    var target = step ?? turn?.OpenTail();
                    if (target is null) continue;
                    var message = SessionEventRead.MessageOf(e);
                    target.Rows.Add(new TrajectoryRow("user", "❯", "you", OneLine(message.FlattenText())));
                    continue;
                }
                case SessionEventTypes.AssistantMessage:
                {
                    var target = step ?? turn?.OpenTail();
                    if (target is null) continue;
                    var payload = SessionEventRead.AssistantMessageOf(e);
                    var reasoning = string.Join(" ", payload.Message.Content.OfType<ReasoningBlock>().Select(b => b.Text)).Trim();
                    if (reasoning.Length > 0)
                        target.Rows.Add(new TrajectoryRow("thought", "◌", "thought", OneLine(reasoning)));
                    var text = string.Join("\n", payload.Message.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
                    if (text.Length > 0 || payload.Interrupted == true)
                    {
                        var meta = payload.Usage is { } usage ? $"{usage.InputTokens} in · {usage.OutputTokens} out" : null;
                        target.Rows.Add(new TrajectoryRow(
                            "assistant",
                            "◆",
                            "assistant",
                            OneLine(text) is { Length: > 0 } line ? line : "(interrupted)",
                            payload.Interrupted == true ? "interrupted" : null,
                            Meta: meta));
                    }
                    continue;
                }
                default:
                {
                    if (e.Ignorable == true && !SessionEventTypes.KnownTypes.Contains(e.Type)) continue;
                    var chip = ChipOf(e);
                    if (chip is null) continue;
                    var target = step ?? turn?.OpenTail();
                    if (target is null) preamble.Add(chip);
                    else target.Rows.Add(chip);
                    continue;
                }
            }
        }

        // Tool calls still unanswered (running or lost): surface them where they were made.
        foreach (var pending in pendingCalls.Values)
        {
            var call = SessionEventRead.ToolCallOf(pending.Call);
            pending.Step.Rows.Add(new TrajectoryRow(
                "tool", "…", call.Name, OneLine(call.Arguments), "running", ArgsJson: call.Arguments));
        }

        return new TrajectoryModel(
            preamble,
            turns.Select(t => new TrajectoryTurnRow(
                t.Turn,
                t.Status,
                t.DurationMs,
                t.Steps.Select(s => new TrajectoryStepRow(t.Turn, s.Step, s.Rows)).ToList())).ToList());
    }

    private static TrajectoryRow? ChipOf(SessionEvent e) => e.Type switch
    {
        SessionEventTypes.CompactionStart => new TrajectoryRow("chip", "✷", "compaction started", null),
        SessionEventTypes.CompactionSummary => new TrajectoryRow("chip", "✷", "compaction summary", null),
        SessionEventTypes.CompactionEnd => new TrajectoryRow("chip", "✷", "compaction done", null),
        SessionEventTypes.CompactionPrune => new TrajectoryRow("chip", "✷", "tool outputs pruned", NumberDetail(e, "prunedChars", "chars")),
        SessionEventTypes.LlmRetry => new TrajectoryRow("chip", "↻", "retry scheduled", NumberDetail(e, "delayMs", "ms delay")),
        SessionEventTypes.LlmRetryStarted => new TrajectoryRow("chip", "↻", "retrying request", null),
        SessionEventTypes.SessionTitle => new TrajectoryRow("chip", "✎", "titled", SafeTitle(e)),
        SessionEventTypes.SandboxMode => new TrajectoryRow("chip", "⛨", "sandbox", SafeDetail(e, "mode")),
        SessionEventTypes.TodoWrite => new TrajectoryRow("chip", "☰", "todos updated", $"{SessionEventRead.TodosOf(e).Count} entries"),
        SessionEventTypes.GoalChange => new TrajectoryRow("chip", "◎", "goal changed", null),
        SessionEventTypes.PlanMode => new TrajectoryRow("chip", "▣", "plan mode", SafeDetail(e, "enabled")),
        SessionEventTypes.ScheduleChange => new TrajectoryRow("chip", "⏰", "schedule changed", null),
        _ => null,
    };

    private static string? NumberDetail(SessionEvent e, string property, string suffix)
        => e.Data.TryGetProperty(property, out var value) ? $"{value.GetInt64():N0} {suffix}" : null;

    private static string? SafeDetail(SessionEvent e, string property)
        => e.Data.TryGetProperty(property, out var value) ? value.GetRawText().Trim('"') : null;

    private static string SafeTitle(SessionEvent e)
    {
        try { return SessionEventRead.TitleOf(e); }
        catch { return ""; }
    }

    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > PreviewLimit ? line[..PreviewLimit] + "…" : line;
    }

    private static string DescribeReason(TurnEndReason reason) => reason switch
    {
        TurnEndReason.Completed => "completed",
        TurnEndReason.Aborted aborted => $"aborted ({aborted.Cause})",
        TurnEndReason.Blocked => "blocked",
        TurnEndReason.Error error => $"error: {error.Message}",
        TurnEndReason.MaxTokens => "max tokens",
        TurnEndReason.Interrupted => "cancelled",
        _ => "ended",
    };

    private sealed class TurnBuild(int turn, long startTime)
    {
        public int Turn { get; } = turn;
        public long StartTime { get; } = startTime;
        public string Status { get; set; } = "running";
        public double DurationMs { get; set; }
        public List<StepBuild> Steps { get; } = [];

        public StepBuild OpenTail()
        {
            if (Steps.Count == 0) Steps.Add(new StepBuild(1));
            return Steps[^1];
        }
    }

    private sealed class StepBuild(int step)
    {
        public int Step { get; } = step;
        public List<TrajectoryRow> Rows { get; } = [];
    }
}
