using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Telemetry;

public sealed record TelemetryDay(
    string Date,
    long Turns,
    long AssistantMessages,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    IReadOnlyDictionary<string, long> ToolCalls);

public sealed record TelemetrySnapshot(long GeneratedAt, bool Enabled, IReadOnlyList<TelemetryDay> Days);

/// <summary>
/// ctx.telemetry — LOCAL usage aggregates only: turns, messages, token buckets, and tool
/// call counts, folded from committed session events into per-UTC-day records persisted at
/// a JSON file under the harness home. Nothing ever leaves the machine; the setting toggles
/// collection entirely (dsh telemetry parity without the phone-home).
/// </summary>
public sealed class UsageTelemetryService : IDisposable
{
    public const string ServiceKey = "telemetry";

    private readonly object _gate = new();
    private readonly Dictionary<string, DayAggregate> _days = [];
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public bool Enabled { get; private set; }
    public string StorePath { get; }

    private sealed class DayAggregate
    {
        public long Turns { get; set; }
        public long AssistantMessages { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public Dictionary<string, long> ToolCalls { get; } = new(StringComparer.Ordinal);
    }

    private UsageTelemetryService(string storePath, bool enabled)
    {
        StorePath = storePath;
        Enabled = enabled;
    }

    public static UsageTelemetryService Mount(HarnessContext ctx, string storePath, bool enabled)
    {
        var service = new UsageTelemetryService(storePath, enabled);
        service.Load();
        ctx.Provide(ServiceKey, service);
        ctx.On<SessionEventNotification>("session/event", (notification, _) =>
        {
            service.Observe(notification.Event);
            return Task.CompletedTask;
        });
        return service;
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled) return;
        lock (_gate) Save();
    }

    internal void Observe(SessionEvent @event)
    {
        if (!Enabled) return;
        var day = UtcDateKey(DateTimeOffset.FromUnixTimeMilliseconds(@event.Time));
        lock (_gate)
        {
            var aggregate = GetOrCreate(day);
            switch (@event.Type)
            {
                case SessionEventTypes.TurnEnd:
                    aggregate.Turns++;
                    break;
                case SessionEventTypes.AssistantMessage:
                    aggregate.AssistantMessages++;
                    var usage = SessionEventRead.AssistantMessageOf(@event).Usage;
                    if (usage is not null)
                    {
                        aggregate.InputTokens += usage.InputTokens;
                        aggregate.OutputTokens += usage.OutputTokens;
                        aggregate.CacheReadTokens += usage.CacheReadTokens ?? 0;
                        aggregate.CacheWriteTokens += usage.CacheWriteTokens ?? 0;
                    }
                    break;
                case SessionEventTypes.ToolCall:
                    var name = SessionEventRead.ToolCallOf(@event).Name;
                    aggregate.ToolCalls[name] = aggregate.ToolCalls.TryGetValue(name, out var count) ? count + 1 : 1;
                    break;
            }
            if (@event.Type is SessionEventTypes.TurnEnd or SessionEventTypes.AssistantMessage or SessionEventTypes.ToolCall)
                Save();
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            var days = _days
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new TelemetryDay(
                    kv.Key, kv.Value.Turns, kv.Value.AssistantMessages,
                    kv.Value.InputTokens, kv.Value.OutputTokens,
                    kv.Value.CacheReadTokens, kv.Value.CacheWriteTokens,
                    new Dictionary<string, long>(kv.Value.ToolCalls)))
                .ToList();
            return new TelemetrySnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Enabled, days);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _days.Clear();
            Save();
        }
    }

    private DayAggregate GetOrCreate(string day)
    {
        if (!_days.TryGetValue(day, out var aggregate))
        {
            aggregate = new DayAggregate();
            _days[day] = aggregate;
        }
        return aggregate;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var snapshot = JsonSerializer.Deserialize<TelemetrySnapshot>(File.ReadAllText(StorePath), _json);
            if (snapshot is null) return;
            foreach (var day in snapshot.Days)
            {
                var aggregate = new DayAggregate
                {
                    Turns = day.Turns,
                    AssistantMessages = day.AssistantMessages,
                    InputTokens = day.InputTokens,
                    OutputTokens = day.OutputTokens,
                    CacheReadTokens = day.CacheReadTokens,
                    CacheWriteTokens = day.CacheWriteTokens,
                };
                foreach (var call in day.ToolCalls) aggregate.ToolCalls[call.Key] = call.Value;
                _days[day.Date] = aggregate;
            }
        }
        catch (Exception)
        {
            // corrupt telemetry must never break the harness; start fresh
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Snapshot(), _json));
        }
        catch (IOException)
        {
            // best-effort durability; the next event retries
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Enabled) Save();
        }
    }

    private static string UtcDateKey(DateTimeOffset timestamp)
        => timestamp.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
