using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blazorly.Harness.Core.Sessions;

public static class SessionEventTypes
{
    public const string TurnStart = "turn/start";
    public const string TurnEnd = "turn/end";
    public const string StepStart = "step/start";
    public const string StepEnd = "step/end";
    public const string UserMessage = "user/message";
    public const string AssistantChunk = "assistant/chunk";
    public const string AssistantMessage = "assistant/message";
    public const string ToolCall = "tool/call";
    public const string ToolResult = "tool/result";
    public const string TodoWrite = "todo/write";
    public const string RequestHeader = "request/header";
    public const string RequestContext = "request/context";
    public const string AgentInboxSpliced = "agent/inbox/spliced";
    public const string SessionTitle = "session/title";
    public const string SandboxMode = "sandbox/mode";
    public const string CommandRun = "command/run";
    public const string CommandDone = "command/done";
    public const string CompactionStart = "compaction/start";
    public const string CompactionSummary = "compaction/summary";
    public const string CompactionEnd = "compaction/end";
    public const string CompactionPrune = "compaction/prune";
    public const string GoalChange = "goal/change";
    public const string PlanMode = "plan/mode";
    public const string HookInvoked = "hook/invoked";
    public const string HookResult = "hook/result";
    public const string LlmRetry = "llm/retry";
    public const string LlmRetryStarted = "llm/retry-started";
    public const string ScheduleChange = "schedule/change";
    public const string TeamMember = "team/member";
    public const string TeamTask = "team/task";
    public const string TeamMessageQueued = "team/message/queued";
    public const string TeamMessageDelivered = "team/message/delivered";
    public const string SubagentDescriptor = "subagent/descriptor";

    /// <summary>Surface events: the only types that may carry surfaceOp and project into model history.</summary>
    public static readonly IReadOnlySet<string> SurfaceTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        UserMessage, AssistantMessage, ToolResult,
    };

    public static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        TurnStart, TurnEnd, StepStart, StepEnd, UserMessage, AssistantChunk, AssistantMessage,
        ToolCall, ToolResult, TodoWrite, RequestHeader, RequestContext, AgentInboxSpliced,
        SessionTitle, SandboxMode, CommandRun, CommandDone,
        CompactionStart, CompactionSummary, CompactionEnd, CompactionPrune, GoalChange, PlanMode,
        HookInvoked, HookResult, LlmRetry, LlmRetryStarted, ScheduleChange,
        TeamMember, TeamTask, TeamMessageQueued, TeamMessageDelivered,

        // SubagentDescriptor is deliberately NOT a known type: like dsh's log-only descriptor,
        // it is an ignorable plugin event — required knowledge lives in SubagentService only.
    };
}

/// <summary>How an event entered the model-visible surface.</summary>
[JsonConverter(typeof(SurfaceOpJsonConverter))]
public abstract record SurfaceOp
{
    public sealed record Append : SurfaceOp;

    public sealed record Replace(int Start, int End) : SurfaceOp;
}

public sealed class SurfaceOpJsonConverter : JsonConverter<SurfaceOp>
{
    public override SurfaceOp? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() == "append") return new SurfaceOp.Append();
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.TryGetProperty("op", out var op) && op.GetString() == "replace"
                && root.TryGetProperty("start", out var start) && root.TryGetProperty("end", out var end))
            {
                return new SurfaceOp.Replace(start.GetInt32(), end.GetInt32());
            }
        }
        throw new JsonException("invalid surfaceOp");
    }

    public override void Write(Utf8JsonWriter writer, SurfaceOp value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case SurfaceOp.Append:
                writer.WriteStringValue("append");
                break;
            case SurfaceOp.Replace replace:
                writer.WriteStartObject();
                writer.WriteString("op", "replace");
                writer.WriteNumber("start", replace.Start);
                writer.WriteNumber("end", replace.End);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException("unknown surfaceOp");
        }
    }
}

/// <summary>
/// A durable session event: the single source of truth. Seq is contiguous from 0;
/// model-visible facts live only on the three surface types.
/// </summary>
public sealed record SessionEvent
{
    public required string Type { get; init; }
    public int Seq { get; init; }
    public long Time { get; init; }
    public required JsonElement Data { get; init; }

    /// <summary>True when a reader may safely skip this unknown event type; absent means required.</summary>
    public bool? Ignorable { get; init; }

    public int[]? SourceEventSeqs { get; init; }
    public SurfaceOp? SurfaceOp { get; init; }
}

/// <summary>Durable header describing a session (the first line of a persisted log).</summary>
public sealed record SessionHeader
{
    public const int FormatVersion = 1;

    public int Version { get; init; } = FormatVersion;
    public required string Id { get; init; }
    public long CreatedAt { get; init; }
    public string? Cwd { get; init; }
    public string? ParentSession { get; init; }
    public int SeedLength { get; init; }
    public int DelegationDepth { get; init; }
    public string? AgentPreset { get; init; }
}

public static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T FromElement<T>(JsonElement element) => element.Deserialize<T>(Options)
        ?? throw new InvalidOperationException($"failed to deserialize {typeof(T).Name}");
}

public sealed class SessionValidationException : Kernel.HarnessException
{
    public SessionValidationException(string code, string message) : base(code, message) { }
}
