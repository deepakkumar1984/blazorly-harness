using System.Text.Json.Serialization;

namespace Blazorly.Harness.Llm;

/// <summary>Full request options; the loop stamps messages derived from the session log.</summary>
public sealed record GenerateOptions
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }
    public string? System { get; init; }
    public IReadOnlyList<ToolSchema>? Tools { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public IReadOnlyList<string>? Stop { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? SessionId { get; init; }
    public string? Purpose { get; init; }

    public LlmCallConfig ToCallConfig() => new()
    {
        Provider = Provider,
        Model = Model,
        ReasoningEffort = ReasoningEffort,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        Stop = Stop is null ? null : [.. Stop],
    };
}

/// <summary>The logged header subset of a request; equality is field-wise including element-wise stop.</summary>
public sealed record LlmCallConfig
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public IReadOnlyList<string>? Stop { get; init; }

    public bool ValueEquals(LlmCallConfig? other)
    {
        if (other is null) return false;
        if (Provider != other.Provider || Model != other.Model
            || ReasoningEffort != other.ReasoningEffort
            || Temperature != other.Temperature || MaxTokens != other.MaxTokens) return false;
        return SequenceEqual(Stop, other.Stop);

        static bool SequenceEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}

public sealed record LlmModelInfo(
    string Provider,
    string Id,
    string Name,
    string? Description = null,
    string[]? InputModalities = null,
    long? ContextWindowTokens = null,
    int? MaxOutputTokens = null,
    bool? SupportsReasoning = null,
    string[]? ReasoningEfforts = null,
    string? DefaultEffort = null);
