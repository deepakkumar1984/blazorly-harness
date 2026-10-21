using System.Reflection;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Every tool publishes a JSON Schema to the model but binds the response with
/// <see cref="SessionJson.Options"/>, which is camelCase. A schema property named
/// <c>old_string</c> therefore only reaches the CLR record when it carries an explicit
/// <c>JsonPropertyName</c> — otherwise the model's argument is silently dropped and the tool
/// fails on a null it was never given (this is how <c>edit</c> broke: it published snake_case
/// and bound camelCase). The audit round-trips schema-shaped arguments through the real
/// serializer for every registered tool, so a rename on either side fails the build.
/// </summary>
public class ToolSchemaBindingAudit
{
    private static readonly Assembly ToolsAssembly = typeof(BuiltInToolsPlugin).Assembly;

    [Fact]
    public async Task RegisteredTools_BindTheirOwnPublishedSchemas()
    {
        await using var harness = TestHarness.Create();
        var checkedTools = new List<string>();
        var problems = new List<string>();

        foreach (var schema in harness.Tools.Schemas())
        {
            var tool = harness.Tools.Get(schema.Name);
            Assert.NotNull(tool);
            checkedTools.Add(schema.Name);
            problems.AddRange(CheckTool(tool!, schema.Parameters));
        }

        // The audit is only useful if it actually saw the tool family; a registration rename
        // must not silently shrink it to nothing.
        Assert.Contains("edit", checkedTools);
        Assert.Contains("write", checkedTools);
        Assert.Contains("bash", checkedTools);
        Assert.True(checkedTools.Count >= 7, $"expected the built-in family, saw: {string.Join(", ", checkedTools)}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryToolInTheAssembly_BindsItsOwnPublishedSchema()
    {
        var problems = new List<string>();
        var checkedTypes = new List<string>();
        var skipped = new List<string>();

        foreach (var type in ToolsAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false } && typeof(ToolDefinition).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (TryCreate(type) is not { } tool)
            {
                skipped.Add(type.Name);
                continue;
            }
            checkedTypes.Add(type.Name);
            problems.AddRange(CheckTool(tool, tool.Parameters.ToJson()).Select(p => $"{type.Name}: {p}"));
        }

        Assert.True(checkedTypes.Count >= 20, $"audit covered too few tools: {checkedTypes.Count} (skipped {skipped.Count})");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Builds arguments shaped exactly like the published schema, deserializes them the way the
    /// pipeline does, and re-serializes: any required property that did not bind comes back
    /// missing or null, which is the failure the model experiences.
    /// </summary>
    private static IEnumerable<string> CheckTool(ToolDefinition tool, JsonElement schema)
    {
        var argsType = ArgsTypeOf(tool.GetType());
        string? deserializeFailure = null;
        if (argsType is null) yield break; // untyped tool: nothing to bind
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties))
            yield break;

        var required = schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray().Select(e => e.GetString()!).ToList()
            : new List<string>();

        var sample = new Dictionary<string, object?>();
        foreach (var property in properties.EnumerateObject())
            sample[property.Name] = SampleValue(property.Value);

        object? bound;
        try
        {
            bound = JsonSerializer.Deserialize(JsonSerializer.SerializeToElement(sample), argsType, SessionJson.Options);
        }
        catch (Exception exception)
        {
            deserializeFailure = $"{tool.Name}: schema-shaped arguments failed to deserialize into {argsType.Name}: {exception.Message}";
            bound = null;
        }
        if (deserializeFailure is not null)
        {
            yield return deserializeFailure;
            yield break;
        }
        if (bound is null)
        {
            yield return $"{tool.Name}: schema-shaped arguments deserialized to null";
            yield break;
        }

        var roundTrip = JsonSerializer.SerializeToElement(bound, SessionJson.Options);
        foreach (var name in required)
        {
            if (!roundTrip.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                yield return $"{tool.Name}: required schema property '{name}' does not bind to {argsType.Name} "
                    + "(add [JsonPropertyName(\"" + name + "\")] — the serializer is camelCase)";
        }

        // Optional arguments must survive the round-trip under their published name too: a dropped
        // optional is a silent no-op the model cannot see.
        foreach (var name in sample.Keys.Where(k => !required.Contains(k)))
        {
            if (!roundTrip.TryGetProperty(name, out _))
                yield return $"{tool.Name}: optional schema property '{name}' does not bind to {argsType.Name} "
                    + "(add [JsonPropertyName(\"" + name + "\")], or drop it from the published schema)";
        }
    }

    private static Type? ArgsTypeOf(Type toolType)
    {
        for (var type = toolType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ToolDefinition<,>))
                return type.GetGenericArguments()[0];
        }
        return null;
    }

    private static object? SampleValue(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return "sample";
        var type = schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (type)
        {
            case "string":
                return schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0
                    ? JsonDocument.Parse(values[0].GetRawText()).RootElement.Clone()
                    : "sample";
            case "integer":
            case "number":
                return 1;
            case "boolean":
                return true;
            case "array":
                return schema.TryGetProperty("items", out var items)
                    ? new List<object?> { SampleValue(items) }
                    : new List<object?>();
            case "object":
                var nested = new Dictionary<string, object?>();
                if (schema.TryGetProperty("properties", out var nestedProperties))
                {
                    foreach (var property in nestedProperties.EnumerateObject())
                        nested[property.Name] = SampleValue(property.Value);
                }
                return nested;
            default:
                return "sample";
        }
    }

    /// <summary>
    /// Tools take their services as primary-constructor parameters and only store them, so nulls
    /// are enough to read a schema. Anything that needs real dependencies is skipped rather than
    /// faked — the registered-tool audit above covers the built-in family end to end.
    /// </summary>
    private static ToolDefinition? TryCreate(Type type)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) return null;
        try
        {
            var arguments = ctor.GetParameters()
                .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                .ToArray();
            return Activator.CreateInstance(type, arguments) as ToolDefinition;
        }
        catch
        {
            return null;
        }
    }
}
