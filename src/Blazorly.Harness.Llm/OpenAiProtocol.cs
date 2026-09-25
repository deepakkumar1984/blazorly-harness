namespace Blazorly.Harness.Llm;

/// <summary>Protocol selection and actionable errors for OpenAI-compatible routes.</summary>
public static class OpenAiProtocol
{
    public static bool UsesResponsesForModel(string model)
    {
        // Gateways may prefix an upstream model with its provider, e.g. openai/gpt-6-astra.
        var id = model[(model.LastIndexOf('/') + 1)..];
        return IsFamily(id, "gpt-5") || IsFamily(id, "gpt-6");
    }

    private static bool IsFamily(string model, string family)
        => model.Equals(family, StringComparison.OrdinalIgnoreCase)
            || model.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresResponsesApi(string message)
    {
        var lower = message.ToLowerInvariant();
        return (lower.Contains("/responses") || lower.Contains("responses api"))
            && (lower.Contains("/chat/completions") || lower.Contains("chat completions"))
            && (lower.Contains("use ") || lower.Contains("require") || lower.Contains("not supported"));
    }
}
