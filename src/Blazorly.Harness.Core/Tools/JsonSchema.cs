using System.Text.Json;

namespace Blazorly.Harness.Core.Tools;

/// <summary>
/// A small JSON Schema authoring DSL plus a validator for the subset tools use:
/// type, properties, required, additionalProperties, items, enum, const, oneOf, minItems/maxItems, minLength.
/// </summary>
public static class JsonSchema
{
    public sealed class Schema
    {
        public string? Type { get; init; }
        public string? Description { get; init; }
        public Dictionary<string, Schema>? Properties { get; init; }
        public List<string>? Required { get; init; }
        public bool? AdditionalProperties { get; init; }
        public Schema? Items { get; init; }
        public List<JsonElement>? Enum { get; init; }
        public JsonElement? Const { get; init; }
        public List<Schema>? OneOf { get; init; }
        public int? MinItems { get; init; }
        public int? MaxItems { get; init; }
        public int? MinLength { get; init; }
        public double? Minimum { get; init; }
        public double? Maximum { get; init; }
        /// <summary>Opaque schema passthrough (external contracts, e.g. MCP inputSchema); emitted verbatim, validated loosely.</summary>
        public JsonElement? Raw { get; init; }

        public JsonElement ToJson()
        {
            if (Raw is { } raw) return raw;
            var map = new Dictionary<string, object?>();
            if (Type is not null) map["type"] = Type;
            if (Description is not null) map["description"] = Description;
            if (Properties is not null)
                map["properties"] = Properties.ToDictionary(p => p.Key, p => (object?)p.Value.ToJson());
            else if (Type == "object" && OneOf is null)
                map["properties"] = new Dictionary<string, object?>();
            if (Required is not null) map["required"] = Required;
            if (AdditionalProperties is not null) map["additionalProperties"] = AdditionalProperties;
            if (Items is not null) map["items"] = Items.ToJson();
            if (Enum is not null) map["enum"] = Enum;
            if (Const is not null) map["const"] = Const;
            if (OneOf is not null) map["oneOf"] = OneOf.Select(s => (object?)s.ToJson()).ToList();
            if (MinItems is not null) map["minItems"] = MinItems;
            if (MaxItems is not null) map["maxItems"] = MaxItems;
            if (MinLength is not null) map["minLength"] = MinLength;
            if (Minimum is not null) map["minimum"] = Minimum;
            if (Maximum is not null) map["maximum"] = Maximum;
            return JsonSerializer.SerializeToElement(map, Sessions.SessionJson.Options);
        }
    }

    public static Schema Object(Dictionary<string, Schema>? properties = null, List<string>? required = null, bool additionalProperties = false, string? description = null)
        => new() { Type = "object", Properties = properties ?? [], Required = required, AdditionalProperties = additionalProperties, Description = description };

    public static Schema String(string? description = null, List<JsonElement>? values = null)
        => new() { Type = "string", Description = description, Enum = values };

    public static Schema Number(string? description = null) => new() { Type = "number", Description = description };
    public static Schema Integer(string? description = null) => new() { Type = "integer", Description = description };
    public static Schema Boolean(string? description = null) => new() { Type = "boolean", Description = description };
    public static Schema Array(Schema items, string? description = null, int? minItems = null, int? maxItems = null)
        => new() { Type = "array", Items = items, Description = description, MinItems = minItems, MaxItems = maxItems };
    public static Schema OneOf(params Schema[] branches) => new() { OneOf = [.. branches] };
    public static Schema Const(JsonElement value) => new() { Const = value };

    /// <summary>An externally-owned schema emitted verbatim and validated only for JSON-ness.</summary>
    public static Schema Raw(JsonElement schema) => new() { Raw = schema };

    /// <summary>Validates a value against a schema node; returns the first violation or null.</summary>
    public static string? Validate(JsonElement value, Schema schema, string path = "$")
    {
        if (schema.Const is { } constant)
        {
            return JsonElement.DeepEquals(constant, value) ? null : $"{path}: must equal {constant}";
        }
        if (schema.Enum is { } values)
        {
            return values.Any(v => JsonElement.DeepEquals(v, value)) ? null : $"{path}: must be one of the enumerated values";
        }
        if (schema.OneOf is { } branches)
        {
            var matches = branches.Count(b => Validate(value, b, path) is null);
            return matches == 1 ? null : $"{path}: must match exactly one oneOf branch (matched {matches})";
        }
        if (schema.Type is not null)
        {
            var ok = schema.Type switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true,
            };
            if (!ok) return $"{path}: expected {schema.Type}, found {value.ValueKind}";
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object when schema.Properties is not null:
            {
                foreach (var required in schema.Required ?? [])
                {
                    if (!value.TryGetProperty(required, out _)) return $"{path}: missing required property '{required}'";
                }
                if (schema.AdditionalProperties == false)
                {
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!schema.Properties.ContainsKey(property.Name)) return $"{path}: unexpected property '{property.Name}'";
                    }
                }
                foreach (var property in value.EnumerateObject())
                {
                    if (schema.Properties.TryGetValue(property.Name, out var propertySchema))
                    {
                        var violation = Validate(property.Value, propertySchema, $"{path}.{property.Name}");
                        if (violation is not null) return violation;
                    }
                }
                break;
            }
            case JsonValueKind.Array when schema.Items is not null:
            {
                var length = value.GetArrayLength();
                if (schema.MinItems is { } min && length < min) return $"{path}: needs at least {min} items";
                if (schema.MaxItems is { } max && length > max) return $"{path}: allows at most {max} items";
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var violation = Validate(item, schema.Items, $"{path}[{index++}]");
                    if (violation is not null) return violation;
                }
                break;
            }
            case JsonValueKind.String:
            {
                if (schema.MinLength is { } minLength && value.GetString()!.Length < minLength) return $"{path}: shorter than {minLength}";
                break;
            }
            case JsonValueKind.Number:
            {
                var number = value.GetDouble();
                if (schema.Minimum is { } minimum && number < minimum) return $"{path}: below {minimum}";
                if (schema.Maximum is { } maximum && number > maximum) return $"{path}: above {maximum}";
                break;
            }
        }
        return null;
    }
}
