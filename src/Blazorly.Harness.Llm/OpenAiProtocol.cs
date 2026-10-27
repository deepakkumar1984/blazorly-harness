namespace Blazorly.Harness.Llm;

/// <summary>Protocol selection and actionable errors for OpenAI-compatible routes.</summary>
public static class OpenAiProtocol
{
    public static bool UsesResponsesForModel(string model)
    {
        // Unprefixed OpenAI ids and the openai/ namespace select Responses. Other gateway
        // prefixes (az/, azure/, cf/, …) are routing hints, not protocol guarantees: those
        // routes often expose Azure models through Chat Completions, and guessing Responses
        // makes the gateway rewrite max_output_tokens/reasoning into fields Azure rejects.
        var slash = model.LastIndexOf('/');
        var id = slash >= 0 ? model[(slash + 1)..] : model;
        if (slash >= 0 && !model[..slash].Equals("openai", StringComparison.OrdinalIgnoreCase))
            return false;
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
