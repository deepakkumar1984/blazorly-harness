using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blazorly.Harness.Llm;

/// <summary>A content block in a message. Tool arguments stay raw JSON strings end to end.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ReasoningBlock), "reasoning")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(ToolCallBlock), "tool-call")]
[JsonDerivedType(typeof(ToolResultBlock), "tool-result")]
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>Chain-of-thought output, distinct from visible text.</summary>
public sealed record ReasoningBlock(string Text) : ContentBlock;

public sealed record ImageBlock(string AttachmentId, string MimeType) : ContentBlock;

public sealed record ToolCallBlock(string Id, string Name, string Arguments) : ContentBlock;

public sealed record ToolResultBlock(string ToolCallId, IReadOnlyList<ContentBlock> Content, bool? IsError = null) : ContentBlock;

/// <summary>Producer provenance for a message.</summary>
public sealed record MessageSource(
    string Kind,
    string? Provider = null,
    string? Model = null,
    string? Plugin = null,
    string? CallId = null,
    string? Form = null)
{
    public static MessageSource User() => new("user");
    public static MessageSource FromModel(string provider, string model) => new("model", provider, model);
    public static MessageSource FromTool(string callId) => new("tool", CallId: callId);
    public static MessageSource FromPlugin(string plugin, string? form = null) => new("plugin", Plugin: plugin, Form: form);
}

public sealed record Message(string Id, string Role, IReadOnlyList<ContentBlock> Content, MessageSource Source)
{
    public static Message CreateUser(IReadOnlyList<ContentBlock> content) =>
        new(Ids.NewMessageId(), "user", content, MessageSource.User());

    public static Message CreateUserText(string text) =>
        CreateUser([new TextBlock(text)]);

    public static Message CreateAssistant(string provider, string model, IReadOnlyList<ContentBlock> content) =>
        new(Ids.NewMessageId(), "assistant", content, MessageSource.FromModel(provider, model));

    /// <summary>A tool result is a user-role message whose content is exactly one tool-result block.</summary>
    public static Message CreateToolResult(string callId, IReadOnlyList<ContentBlock> content, bool isError = false) =>
        new(Ids.NewMessageId(), "user", [new ToolResultBlock(callId, content, isError)], MessageSource.FromTool(callId));

    public string FlattenText() => string.Concat(Content.OfType<TextBlock>().Select(b => b.Text));
}

public static class Ids
{
    private static readonly Random Random = new();

    /// <summary>Short unique id, in the spirit of dsh's branded UUIDs but compact for logs.</summary>
    public static string NewMessageId() => $"msg_{New()}";

    public static string NewCallId() => $"call_{New()}";

    public static string NewSessionId() => $"session-{New()}";

    public static string New() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}{Random.NextInt64(0x100000000L):x8}";
}

/// <summary>The model-facing tool description that joins prompt assembly.</summary>
public sealed record ToolSchema(string Name, string Description, JsonElement Parameters);

public sealed class ToolSchemaJson
{
    public static ToolSchema FromJson(string name, string description, string parametersJson)
    {
        using var doc = JsonDocument.Parse(parametersJson);
        return new ToolSchema(name, description, doc.RootElement.Clone());
    }

    public static ToolSchema FromObject<T>(string name, string description, T parameters)
        => new(name, description, JsonSerializer.SerializeToElement(parameters));
}
