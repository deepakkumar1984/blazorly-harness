using System.Text.Json;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Tools;

/// <summary>Pure, replay-safe UI views computed from logged args/results; never persisted as truth.</summary>
public sealed record ToolCallView
{
    public string Card { get; init; } = "generic"; // generic | terminal | diff
    public string Kind { get; init; } = "other";   // read | edit | delete | move | search | execute | fetch | other
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Path { get; init; }
    public int? Line { get; init; }
    public FileDiff? Diff { get; init; }
}

public sealed record FileDiff(string Path, string? OldText, string NewText);

public sealed record ToolResultView
{
    public string Card { get; init; } = "generic"; // generic | terminal | diff | search | read | web
    public string Title { get; init; } = "";
    public string? Text { get; init; }
    public FileDiff? Diff { get; init; }
    public IReadOnlyList<SearchResultLine>? SearchLines { get; init; }
    public IReadOnlyList<ReadLine>? ReadLines { get; init; }
    public string? Language { get; init; }
}

public sealed record SearchResultLine(string File, int Line, string Text, bool Matched);

public sealed record ReadLine(int Number, string Text);

public sealed record ToolFailure(string Message, ToolErrorInfo? Info = null);

public sealed record ToolErrorInfo(string Name, string Code);

/// <summary>The final, frozen outcome of one tool execution.</summary>
public sealed record ToolExecutionResult
{
    public required bool IsError { get; init; }
    /// <summary>Canonical value — execution-local; never persisted.</summary>
    public JsonElement? Value { get; init; }
    /// <summary>Model-facing content blocks.</summary>
    public required IReadOnlyList<ContentBlock> Content { get; init; }
    public ToolFailure? Error { get; init; }
    /// <summary>Replay-safe presentation payload persisted beside tool/result.</summary>
    public JsonElement? Meta { get; init; }
    public IReadOnlyList<Message> AdditionalContexts { get; init; } = [];
    public bool ConcludesTurn { get; init; }
}

/// <summary>The execution context handed to a tool body; args are frozen, the signal may be wrapped upstream.</summary>
public sealed class ToolRunContext
{
    public required JsonElement Args { get; init; }
    public required CancellationToken Signal { get; set; }
    public Agent.Agent? Agent { get; init; }
    public string? CallId { get; init; }
    public required Func<Message, Task> DeferContextAsync { get; init; }
    public required Action ConcludeTurn { get; init; }

    public Sessions.Session Session => Agent?.Session ?? throw new Kernel.HarnessException("NO_AGENT", "this tool requires an owning agent");
}

/// <summary>
/// A registered tool. Execute returns only the canonical JSON value; model-facing content is
/// derived through the output contract (schema + render) and validated against the schema.
/// </summary>
public abstract class ToolDefinition
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonSchema.Schema Parameters { get; }
    public abstract JsonSchema.Schema Output { get; }
    public abstract Task<JsonElement> Execute(JsonElement args, ToolRunContext exec);
    public abstract IReadOnlyList<ContentBlock> Render(JsonElement args, JsonElement value);

    public virtual int? TimeoutMs => null;
    public virtual bool IsConcurrencySafe(JsonElement args) => false;
    public virtual ToolCallView? PresentCall(JsonElement args) => null;
    public virtual ToolResultView? PresentResult(JsonElement args, ToolExecutionResult result) => null;

    public virtual JsonElement? PresentationMeta(JsonElement args, JsonElement value) => null;
}

/// <summary>Typed author layer: args deserialize to TArgs, execute returns TValue, render receives both.</summary>
public abstract class ToolDefinition<TArgs, TValue> : ToolDefinition
{
    public override async Task<JsonElement> Execute(JsonElement args, ToolRunContext exec)
    {
        var violation = JsonSchema.Validate(args, Parameters);
        if (violation is not null)
            throw new ToolException("INVALID_ARGS", violation);
        var typed = args.Deserialize<TArgs>(Sessions.SessionJson.Options)
            ?? throw new ToolException("INVALID_ARGS", "arguments failed to deserialize");
        var value = await ExecuteTyped(typed, exec).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(value, Sessions.SessionJson.Options);
    }

    public override IReadOnlyList<ContentBlock> Render(JsonElement args, JsonElement value)
    {
        var typedArgs = args.Deserialize<TArgs>(Sessions.SessionJson.Options)!;
        var typedValue = value.Deserialize<TValue>(Sessions.SessionJson.Options)!;
        return RenderTyped(typedArgs, typedValue);
    }

    public override bool IsConcurrencySafe(JsonElement args)
    {
        try
        {
            var typed = args.Deserialize<TArgs>(Sessions.SessionJson.Options);
            return typed is not null && IsConcurrencySafeTyped(typed);
        }
        catch
        {
            return false;
        }
    }

    public override ToolCallView? PresentCall(JsonElement args)
    {
        try
        {
            var typed = args.Deserialize<TArgs>(Sessions.SessionJson.Options);
            return typed is null ? null : PresentCallTyped(typed);
        }
        catch
        {
            return null;
        }
    }

    public override ToolResultView? PresentResult(JsonElement args, ToolExecutionResult result)
    {
        try
        {
            var typed = args.Deserialize<TArgs>(Sessions.SessionJson.Options);
            return typed is null ? null : PresentResultTyped(typed, result);
        }
        catch
        {
            return null;
        }
    }

    public override JsonElement? PresentationMeta(JsonElement args, JsonElement value)
    {
        var typed = args.Deserialize<TArgs>(Sessions.SessionJson.Options);
        var typedValue = value.Deserialize<TValue>(Sessions.SessionJson.Options);
        return typed is null || typedValue is null ? null : PresentationMetaTyped(typed, typedValue);
    }

    protected abstract Task<TValue> ExecuteTyped(TArgs args, ToolRunContext exec);
    protected abstract IReadOnlyList<ContentBlock> RenderTyped(TArgs args, TValue value);
    protected virtual bool IsConcurrencySafeTyped(TArgs args) => false;
    protected virtual ToolCallView? PresentCallTyped(TArgs args) => null;
    protected virtual ToolResultView? PresentResultTyped(TArgs args, ToolExecutionResult result) => null;
    protected virtual JsonElement? PresentationMetaTyped(TArgs args, TValue value) => null;
}

public sealed class ToolException : Kernel.HarnessException
{
    public ToolException(string code, string message) : base(code, message) { }
}

public static class ToolErrorCodes
{
    public const string InvalidArgs = "INVALID_ARGS";
    public const string UnknownTool = "UNKNOWN_TOOL";
    public const string InvalidToolOutput = "INVALID_TOOL_OUTPUT";
    public const string ToolTimeout = "TOOL_TIMEOUT";
    public const string Aborted = "ABORTED";
    public const string AbortedBeforeDispatch = "ABORTED_BEFORE_DISPATCH";
    public const string Denied = "DENIED";
}
