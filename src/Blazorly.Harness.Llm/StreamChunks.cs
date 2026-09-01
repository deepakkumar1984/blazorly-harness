using System.Text.Json.Serialization;

namespace Blazorly.Harness.Llm;

/// <summary>Disjoint token counts. Billed input = Input + CacheRead + CacheWrite; Reasoning is already inside Output.</summary>
public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    long? ReasoningTokens = null)
{
    public long UsageTokens() => InputTokens + OutputTokens + (CacheReadTokens ?? 0) + (CacheWriteTokens ?? 0);
}

public static class FinishReason
{
    public const string Stop = "stop";
    public const string ToolCalls = "tool-calls";
    public const string MaxTokens = "max-tokens";
    public const string Aborted = "aborted";
    public const string Error = "error";
}

/// <summary>The provider-neutral stream chunk protocol.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BlockStartChunk), "block-start")]
[JsonDerivedType(typeof(TextDeltaChunk), "text-delta")]
[JsonDerivedType(typeof(ReasoningDeltaChunk), "reasoning-delta")]
[JsonDerivedType(typeof(ToolCallDeltaChunk), "tool-call-delta")]
[JsonDerivedType(typeof(BlockEndChunk), "block-end")]
[JsonDerivedType(typeof(UsageChunk), "usage")]
[JsonDerivedType(typeof(FinishChunk), "finish")]
public abstract record StreamChunk;

public sealed record BlockStartChunk(int Index, string BlockType) : StreamChunk;

public sealed record TextDeltaChunk(int Index, string Text) : StreamChunk;

public sealed record ReasoningDeltaChunk(int Index, string Text) : StreamChunk;

public sealed record ToolCallDeltaChunk(int Index, string Id, string? Name, string ArgumentsDelta) : StreamChunk;

public sealed record BlockEndChunk(int Index, ContentBlock Block) : StreamChunk;

public sealed record UsageChunk(TokenUsage Usage) : StreamChunk;

/// <summary>Terminal chunk. Usage must arrive before finish; nothing arrives after.</summary>
public sealed record FinishChunk(string Reason, LlmFailure? Failure = null) : StreamChunk;

/// <summary>Canonical failure payload carried by exceptions and error finishes.</summary>
public sealed record LlmFailure(string Message, string Code, int? Status = null, long? ProviderRetryAfterMs = null);

public sealed class LlmException : Kernel.HarnessException
{
    public LlmFailure Failure { get; }

    public LlmException(LlmFailure failure) : base(failure.Code, failure.Message) => Failure = failure;

    public LlmException(string code, string message) : this(new LlmFailure(message, code)) { }
}

public static class LlmErrorCodes
{
    public const string ContextWindowExceeded = "CONTEXT_WINDOW_EXCEEDED";
    public const string Quota = "QUOTA";
    public const string EmptyResponse = "EMPTY_RESPONSE";
    public const string InvalidCredential = "INVALID_CREDENTIAL";
    public const string MissingCredential = "MISSING_CREDENTIAL";
    public const string Auth = "AUTH";
    public const string RateLimit = "RATE_LIMIT";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Server = "SERVER";
    public const string Transport = "TRANSPORT";
    public const string Timeout = "TIMEOUT";
    public const string Aborted = "ABORTED";
    public const string MalformedResponse = "MALFORMED_RESPONSE";
    public const string StreamClosed = "STREAM_CLOSED";
    public const string NoAdapter = "NO_ADAPTER";
    public const string DuplicateAdapter = "DUPLICATE_ADAPTER";

    public static readonly IReadOnlyList<string> DefaultRetryable = ["EMPTY_RESPONSE", "RATE_LIMIT", "SERVER", "TIMEOUT", "TRANSPORT"];

    public static bool IsRetryable(string code, IReadOnlyList<string>? retryable = null)
        => (retryable ?? DefaultRetryable).Contains(code, StringComparer.Ordinal);
}
