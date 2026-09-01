using System.Text.Json;
using System.Text.Json.Serialization;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Sessions;

/// <summary>
/// Typed payload records mirroring dsh's event data shapes, serialized camelCase into SessionEvent.Data.
/// </summary>
public static class SessionPayloads
{
    public sealed record TurnStart(int Turn);
    public sealed record TurnEnd(int Turn, TurnEndReason Reason);
    public sealed record StepStart(int Turn, int Step);
    public sealed record StepEnd(int Turn, int Step);
    public sealed record AssistantChunk(int Turn, int Step, StreamChunk Chunk);
    public sealed record AssistantMessage(int Turn, int Step, Message Message, TokenUsage? Usage = null, bool? Interrupted = null);
    public sealed record ToolCall(int Turn, int Step, string CallId, string Name, string Arguments);
    public sealed record ToolResult(int Turn, int Step, Message Message, ToolErrorInfo? Error = null, JsonElement? Meta = null);
    public sealed record ToolErrorInfo(string Name, string Code);
    public sealed record TodoWrite(IReadOnlyList<TodoItem> Todos);
    public sealed record RequestHeaderPayload(LlmCallConfig Header, string Reason);
    public sealed record RequestContextPayload(string Provider, string Model, int? ContextWindow);
    public sealed record InboxSpliced(string Target, int Start, int? RemovedCount, IReadOnlyList<Message> Inserted, string? Outcome);
    public sealed record SessionTitlePayload(string Title, IReadOnlyList<int> MessageSeqs, string Source);
    public sealed record SandboxModePayload(string Mode, string? Source = null);
    public sealed record CommandRunPayload(string Name, string? Args);
    public sealed record CommandDonePayload(string Kind, string? Text);

    /// <summary>
    /// Log-only continuation state of a continuable subagent: absent from model history and
    /// retained across compaction; the whole reconstruction input for a cold resume.
    /// </summary>
    public sealed record SubagentDescriptorPayload(string Mode, string? Provider = null, string? Model = null, string? Persona = null);

    public const string SubagentModeContinuable = "continuable";
}

public sealed record TodoItem(string Content, string Status)
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
}

/// <summary>Why a turn ended; merge-extensible in dsh, a closed set here.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TurnEndReason.Completed), "completed")]
[JsonDerivedType(typeof(TurnEndReason.Aborted), "aborted")]
[JsonDerivedType(typeof(TurnEndReason.Blocked), "blocked")]
[JsonDerivedType(typeof(TurnEndReason.Error), "error")]
[JsonDerivedType(typeof(TurnEndReason.MaxTokens), "max-tokens")]
[JsonDerivedType(typeof(TurnEndReason.Interrupted), "interrupted")]
public abstract record TurnEndReason
{
    public sealed record Completed : TurnEndReason;
    public sealed record Aborted(string Cause) : TurnEndReason;
    public sealed record Blocked : TurnEndReason;
    public sealed record Error(string Message, string Code) : TurnEndReason;
    public sealed record MaxTokens : TurnEndReason;
    public sealed record Interrupted : TurnEndReason;
}

public static class TurnEndAbortedCauses
{
    public const string User = "user";
    public const string Parent = "parent";
    public const string Disposed = "disposed";
}

/// <summary>Typed read accessors over event payloads.</summary>
public static class SessionEventRead
{
    public static int TurnOf(SessionEvent e) => e.Data.GetProperty("turn").GetInt32();
    public static int StepOf(SessionEvent e) => e.Data.GetProperty("step").GetInt32();

    public static Message MessageOf(SessionEvent e) => SessionJson.FromElement<Message>(e.Data);

    public static SessionPayloads.AssistantMessage AssistantMessageOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.AssistantMessage>(e.Data);

    public static SessionPayloads.ToolCall ToolCallOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.ToolCall>(e.Data);

    public static SessionPayloads.ToolResult ToolResultOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.ToolResult>(e.Data);

    public static IReadOnlyList<TodoItem> TodosOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.TodoWrite>(e.Data).Todos;

    public static SessionPayloads.InboxSpliced InboxSplicedOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.InboxSpliced>(e.Data);

    public static string TitleOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.SessionTitlePayload>(e.Data).Title;

    public static SessionPayloads.SandboxModePayload SandboxModeOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.SandboxModePayload>(e.Data);

    public static SessionPayloads.CommandRunPayload CommandRunOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.CommandRunPayload>(e.Data);

    public static SessionPayloads.CommandDonePayload CommandDoneOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.CommandDonePayload>(e.Data);

    public static SessionPayloads.SubagentDescriptorPayload SubagentDescriptorOf(SessionEvent e)
        => SessionJson.FromElement<SessionPayloads.SubagentDescriptorPayload>(e.Data);

    public static TurnEndReason TurnEndReasonOf(SessionEvent e)
        => SessionJson.FromElement<TurnEndReason>(e.Data.GetProperty("reason"));

    public static bool IsChunkOfType(SessionEvent e, string chunkType)
        => e.Data.TryGetProperty("chunk", out var chunk) && chunk.TryGetProperty("type", out var t) && t.GetString() == chunkType;
}
