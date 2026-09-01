using System.Collections.Concurrent;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Schedule;

public sealed record ScheduleOptions
{
    /// <summary>Fixed-rate floor in seconds (dsh: 5 minutes; tests lower it).</summary>
    public int MinEverySeconds { get; set; } = 300;
    public int TickMs { get; set; } = 2_000;
}

public sealed record ScheduleRecord(
    string Id,
    string Kind, // after | at | every
    string Text,
    long CreatedAtMs,
    long DueMs,
    int? EverySeconds,
    bool Done,
    long? LastDeliveredMs);

/// <summary>
/// Session-local reminders: three rule kinds (after_seconds / at / every_seconds), durable
/// state folded from schedule/change events, delivery as ordinary followup turns when the
/// owning agent goes idle. Forked sessions do not inherit (the fold starts at SeedLength);
/// delivery is at-least-once; recurring catch-up contributes only the latest overdue tick.
/// </summary>
public sealed class ScheduleService : IAsyncDisposable
{
    public const string ServiceKey = "schedule";

    private readonly HarnessContext _ctx;
    private readonly AgentRuntime _agents;
    private readonly SessionStore _sessions;
    private readonly ConcurrentDictionary<string, (int Seq, Dictionary<string, ScheduleRecord> Records)> _folds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _loopCts = new();
    private int _counter;

    public ScheduleService(HarnessContext ctx, ScheduleOptions options)
    {
        _ctx = ctx;
        _agents = ctx.Get<AgentRuntime>(AgentRuntime.ServiceKey);
        _sessions = ctx.Get<SessionStore>(SessionStore.ServiceKey);
        Options = options;
    }

    public ScheduleOptions Options { get; set; }

    public static ScheduleService Mount(HarnessContext ctx, ScheduleOptions? options = null)
    {
        var service = new ScheduleService(ctx, options ?? new ScheduleOptions());
        ctx.Provide(ServiceKey, service);
        SchedulePlugin.Apply(ctx, service);
        _ = Task.Run(() => service.DeliveryLoop(service._loopCts.Token));
        return service;
    }

    public IReadOnlyList<ScheduleRecord> List(string sessionId)
        => [.. Fold(Session(sessionId)).Records.Values.OrderBy(r => r.DueMs)];

    public ScheduleRecord Create(string sessionId, string text, int? afterSeconds, string? at, int? everySeconds)
    {
        var rules = new[] { afterSeconds.HasValue, !string.IsNullOrWhiteSpace(at), everySeconds.HasValue }.Count(v => v);
        if (rules != 1)
            throw new ToolException("SCHEDULE_RULE_REQUIRED", "provide exactly one of after_seconds, at, or every_seconds");
        if (everySeconds is { } every && every < Options.MinEverySeconds)
            throw new ToolException("SCHEDULE_TOO_FREQUENT", $"every_seconds must be at least {Options.MinEverySeconds}");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long due;
        string kind;
        if (afterSeconds is { } seconds)
        {
            kind = "after";
            due = now + seconds * 1000L;
        }
        else if (!string.IsNullOrWhiteSpace(at))
        {
            if (!DateTimeOffset.TryParse(at, out var when))
                throw new ToolException("SCHEDULE_INVALID_AT", $"'{at}' is not a parseable ISO-8601 timestamp");
            kind = "at";
            due = when.ToUnixTimeMilliseconds();
        }
        else
        {
            kind = "every";
            due = now + everySeconds!.Value * 1000L;
        }

        var record = new ScheduleRecord($"sched_{++_counter}", kind, text, now, due, everySeconds, Done: false, LastDeliveredMs: null);
        Append(sessionId, "create", record);
        return record;
    }

    public void Delete(string sessionId, string id)
    {
        var current = Fold(Session(sessionId)).Records.GetValueOrDefault(id)
            ?? throw new ToolException("SCHEDULE_NOT_FOUND", $"no schedule named '{id}'");
        Append(sessionId, "delete", current);
    }

    private Session Session(string sessionId)
        => _sessions.Get(sessionId) ?? throw new ToolException("SCHEDULE_NO_SESSION", $"session '{sessionId}' is not live");

    private void Append(string sessionId, string action, ScheduleRecord record)
    {
        _folds.TryRemove(sessionId, out _); // force a re-fold from the log
        Session(sessionId).Append(SessionEventTypes.ScheduleChange, new { action, record });
    }

    /// <summary>Folds the durable schedule/change log for one session (fork-safe: seed events excluded).</summary>
    public (int Seq, Dictionary<string, ScheduleRecord> Records) Fold(Session session)
    {
        var events = session.Events;
        if (_folds.GetValueOrDefault(session.Id) is { } cached && cached.Seq == events.Count) return cached;

        var records = new Dictionary<string, ScheduleRecord>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Seq < session.Header.SeedLength) continue; // forked sessions do not inherit
            if (e.Type != SessionEventTypes.ScheduleChange) continue;
            var action = e.Data.GetProperty("action").GetString();
            var record = e.Data.GetProperty("record").Deserialize<ScheduleRecord>(SessionJson.Options)!;
            switch (action)
            {
                case "create":
                case "deliver":
                    records[record.Id] = record;
                    break;
                case "delete":
                    records.Remove(record.Id);
                    break;
            }
        }
        var state = (events.Count, records);
        _folds[session.Id] = state;
        return state;
    }

    private async Task DeliveryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Options.TickMs, ct).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var agent in _agents.LiveAgents())
                {
                    if (agent.Status != AgentStatus.Idle) continue;
                    foreach (var record in Fold(agent.Session).Records.Values.ToList())
                    {
                        if (record.Done || record.DueMs > now) continue;
                        var due = record.DueMs;
                        if (record.Kind == "every" && record.LastDeliveredMs is not null)
                        {
                            // Catch-up advances to the latest overdue tick, anchored on the period.
                            var period = record.EverySeconds!.Value * 1000L;
                            var ticks = Math.Max(1, (now - record.DueMs) / period);
                            due = record.DueMs + ticks * period;
                        }
                        var updated = record with { DueMs = due, Done = record.Kind != "every", LastDeliveredMs = now };
                        Append(agent.Session.Id, "deliver", updated);
                        agent.Followup(Message.CreateUserText($"Scheduled reminder: {record.Text}"));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // a failed delivery tick retries on the next tick
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _loopCts.Cancel();
        _loopCts.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Model-facing schedule tools: schedule_create, schedule_list, schedule_delete.</summary>
public static class SchedulePlugin
{
    public static void Apply(HarnessContext ctx, ScheduleService service)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);
        ctx.Effect(tools.Register(new ScheduleCreateTool(service)).Dispose);
        ctx.Effect(tools.Register(new ScheduleListTool(service)).Dispose);
        ctx.Effect(tools.Register(new ScheduleDeleteTool(service)).Dispose);
    }
}

public sealed class ScheduleCreateTool(ScheduleService service) : ToolDefinition<ScheduleCreateTool.Args, ScheduleRecord>
{
    public sealed record Args(string Text, int? AfterSeconds = null, string? At = null, int? EverySeconds = null);

    public override string Name => "schedule_create";

    public override string Description =>
        "Create a session-local reminder that is delivered as a follow-up turn when this session is idle. "
        + "Provide exactly one rule: after_seconds (one-shot delay), at (ISO-8601 timestamp), or "
        + "every_seconds (fixed rate, minimum "
        + service.Options.MinEverySeconds + ").";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["text"] = JsonSchema.String("What the reminder should say."),
            ["after_seconds"] = JsonSchema.Integer("One-shot delay in seconds."),
            ["at"] = JsonSchema.String("Absolute ISO-8601 timestamp."),
            ["every_seconds"] = JsonSchema.Integer("Fixed-rate period in seconds."),
        },
        required: ["text"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object();

    protected override Task<ScheduleRecord> ExecuteTyped(Args args, ToolRunContext exec)
        => Task.FromResult(service.Create(exec.Agent!.Session.Id, args.Text, args.AfterSeconds, args.At, args.EverySeconds));

    protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, ScheduleRecord value)
        => [new TextBlock($"scheduled {value.Id} ({value.Kind}) due at {DateTimeOffset.FromUnixTimeMilliseconds(value.DueMs):u}: {value.Text}")];
}

public sealed class ScheduleListTool(ScheduleService service) : ToolDefinition<object, IReadOnlyList<ScheduleRecord>>
{
    public override string Name => "schedule_list";

    public override string Description => "List this session's pending schedules.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();

    public override JsonSchema.Schema Output { get; } = JsonSchema.Array(JsonSchema.Object());

    protected override Task<IReadOnlyList<ScheduleRecord>> ExecuteTyped(object args, ToolRunContext exec)
        => Task.FromResult(service.List(exec.Agent!.Session.Id));

    protected override IReadOnlyList<ContentBlock> RenderTyped(object args, IReadOnlyList<ScheduleRecord> value)
        => [new TextBlock(value.Count == 0 ? "(no schedules)" : string.Join("\n",
            value.Select(r => $"{r.Id} {r.Kind} due {DateTimeOffset.FromUnixTimeMilliseconds(r.DueMs):u}{(r.Done ? " done" : "")}: {r.Text}")))];
}

public sealed class ScheduleDeleteTool(ScheduleService service) : ToolDefinition<ScheduleDeleteTool.Args, string>
{
    public sealed record Args(string Id);

    public override string Name => "schedule_delete";

    public override string Description => "Delete a pending schedule by id.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["id"] = JsonSchema.String("The schedule id from schedule_list."),
        },
        required: ["id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.String();

    protected override Task<string> ExecuteTyped(Args args, ToolRunContext exec)
    {
        service.Delete(exec.Agent!.Session.Id, args.Id);
        return Task.FromResult($"deleted {args.Id}");
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, string value) => [new TextBlock(value)];
}
