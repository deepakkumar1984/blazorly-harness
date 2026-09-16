using System.Text.Json;

namespace Blazorly.Harness.Llm.Adapters;

/// <summary>
/// Providers validate function tool schemas more strictly than JSON Schema itself does:
/// xAI (Responses) compiles tools into a grammar, LM Studio (Chat Completions) validates the
/// request with zod, and both answer 400 <c>invalid_type</c> at <c>function.parameters.properties</c>
/// when an argument-less tool serializes as <c>{"type":"object"}</c>. Normalizing once keeps every
/// wire adapter on the same contract.
/// </summary>
internal static class ToolParameterSchemas
{
    /// <summary>Guarantees <c>parameters</c> is an object schema carrying a <c>properties</c> object.</summary>
    public static JsonElement Normalize(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            return EmptyObjectParameters();
        // Unions are already object-shaped branches; adding a top-level properties next to
        // oneOf/anyOf would change what the tool accepts.
        if (parameters.TryGetProperty("oneOf", out _) || parameters.TryGetProperty("anyOf", out _))
            return parameters;
        if (parameters.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            return parameters;

        var map = new Dictionary<string, object?>();
        foreach (var property in parameters.EnumerateObject())
            map[property.Name] = property.Value.Clone();
        map.TryAdd("type", "object");
        map["properties"] = new Dictionary<string, object?>();
        return JsonSerializer.SerializeToElement(map);
    }

    private static JsonElement EmptyObjectParameters()
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
        });
}
