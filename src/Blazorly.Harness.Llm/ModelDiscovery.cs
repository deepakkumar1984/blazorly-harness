using System.Text.Json;
using Blazorly.Harness.Llm.Adapters;

namespace Blazorly.Harness.Llm;

/// <summary>
/// Discovers model availability via GET {base}/models. Static metadata can describe returned
/// ids; it must never add ids that the endpoint did not return.
/// </summary>
public static class LlmModelDiscovery
{
    public static async Task<IReadOnlyList<LlmModelInfo>> DiscoverAsync(
        string provider, string baseUrl, string apiKey, HttpClient http, Action<HttpRequestMessage>? configure = null, CancellationToken ct = default,
        bool anthropicModelsPath = false)
    {
        // Anthropic serves its model list under /v1/models with x-api-key auth; OpenAI-compatible routes use /models + bearer.
        // Either way the version/call suffix may already be pasted into the base URL — strip before appending.
        var anthropic = provider == "anthropic" || anthropicModelsPath;
        var path = anthropic ? "/v1/models" : "/models";
        var endpointRoot = anthropic
            ? TransportErrors.TrimApiSuffixes(baseUrl, "/v1/models", "/models", "/v1")
            : TransportErrors.TrimApiSuffixes(baseUrl, "/models", "/chat/completions", "/responses");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpointRoot}{path}");
        if (configure is not null)
        {
            configure(request);
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        request.Headers.TryAddWithoutValidation("User-Agent", "blazorly-harness");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new LlmException(OpenAiCompatibleAdapter.ClassifyHttp((int)response.StatusCode, body).Failure);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var found = new List<LlmModelInfo>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray()) AddItem(item, found);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) AddItem(item, found);
        }
        else
        {
            throw new JsonException("The model endpoint did not return a model list (expected a data array).");
        }
        return found.DistinctBy(m => m.Id, StringComparer.Ordinal).ToList();

        void AddItem(JsonElement item, List<LlmModelInfo> into)
        {
            if (item.ValueKind != JsonValueKind.Object) return;
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } modelId
                || string.IsNullOrWhiteSpace(modelId)) return;
            // Stock OpenAI /models is id-only, but some gateways publish sizes alongside
            // (OpenRouter-style); take them when present so unknown ids still resolve windows.
            var window = ReadLong(item, "context_window", "context_length", "max_context_length", "contextWindow", "contextLength");
            var output = ReadLong(item, "max_output_tokens", "max_completion_tokens", "maxOutputTokens", "maxCompletionTokens");
            into.Add(new LlmModelInfo(provider, modelId, modelId,
                ContextWindowTokens: window,
                MaxOutputTokens: output is > 0 and <= int.MaxValue ? (int)output : null));
        }
    }

    private static long? ReadLong(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed) && parsed > 0) return parsed;
        }
        return null;
    }

    /// <summary>Describes discovered ids with known metadata, without adding undiscovered ids.</summary>
    public static IReadOnlyList<LlmModelInfo> Merge(string provider, IEnumerable<string> discoveredIds, IReadOnlyList<LlmModelInfo> known)
    {
        var merged = new List<LlmModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in discoveredIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            merged.Add(known.FirstOrDefault(m => m.Id == id) ?? new LlmModelInfo(provider, id, id));
        }
        return merged;
    }
}
