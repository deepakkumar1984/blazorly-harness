using System.Text.Json;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// LM Studio validates chat-completions requests with zod and 400s with
/// <c>invalid_type … path: [n,"function","parameters","properties"]</c> when an argument-less tool
/// ships a bare <c>{"type":"object"}</c> schema. Every wire adapter must emit a properties object.
/// </summary>
public class ToolParameterSchemaTests
{
    private const string ArgumentlessSchema = """{"type":"object"}""";

    private static GenerateOptions Options(string provider, string model, string parametersJson) => new()
    {
        Provider = provider,
        Model = model,
        Messages = [Message.CreateUserText("build a minecraft clone")],
        Tools = [ToolSchemaJson.FromJson("goal_get", "Read the current goal", parametersJson)],
    };

    [Fact]
    public void ChatCompletions_ArgumentlessTool_GetsEmptyPropertiesObject()
    {
        var adapter = new OpenAiCompatibleAdapter("lmstudio", "http://localhost:1234/v1", "", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(Options("lmstudio", "qwen3-30b", ArgumentlessSchema));
        var parameters = FirstToolParameters(body, "function", "parameters");

        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, parameters.GetProperty("properties").ValueKind);
        Assert.Empty(parameters.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public void ChatCompletions_DeclaredProperties_ArePreserved()
    {
        var adapter = new OpenAiCompatibleAdapter("lmstudio", "http://localhost:1234/v1", "", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(
            Options("lmstudio", "qwen3-30b", """{"type":"object","required":["command"],"properties":{"command":{"type":"string"}}}"""));
        var parameters = FirstToolParameters(body, "function", "parameters");

        Assert.Equal(JsonValueKind.String,
            parameters.GetProperty("properties").GetProperty("command").GetProperty("type").ValueKind);
        Assert.Equal("command", parameters.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void ChatCompletions_UnionParameters_AreLeftAlone()
    {
        var adapter = new OpenAiCompatibleAdapter("lmstudio", "http://localhost:1234/v1", "", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(Options("lmstudio", "qwen3-30b",
            """{"oneOf":[{"type":"object","properties":{"email":{"type":"string"}}}]}"""));
        var parameters = FirstToolParameters(body, "function", "parameters");

        Assert.True(parameters.TryGetProperty("oneOf", out _));
        Assert.False(parameters.TryGetProperty("properties", out _));
    }

    [Fact]
    public void Anthropic_ArgumentlessTool_GetsEmptyPropertiesObject()
    {
        var adapter = new AnthropicAdapter("anthropic", "https://api.anthropic.com", "k", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(Options("anthropic", "claude-sonnet-4-5", ArgumentlessSchema));
        var parameters = FirstToolParameters(body, null, "input_schema");

        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, parameters.GetProperty("properties").ValueKind);
    }

    [Fact]
    public void NonObjectParameters_BecomeAnEmptyObjectSchema()
    {
        var adapter = new OpenAiCompatibleAdapter("lmstudio", "http://localhost:1234/v1", "", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(Options("lmstudio", "qwen3-30b", "[1,2]"));
        var parameters = FirstToolParameters(body, "function", "parameters");

        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, parameters.GetProperty("properties").ValueKind);
    }

    /// <summary>Round-trips a wire body through JSON and pulls the first tool's parameters node.</summary>
    private static JsonElement FirstToolParameters(Dictionary<string, object?> body, string? wrapper, string parameterKey)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var tool = doc.RootElement.GetProperty("tools")[0];
        if (wrapper is not null) tool = tool.GetProperty(wrapper);
        return tool.GetProperty(parameterKey).Clone();
    }
}
