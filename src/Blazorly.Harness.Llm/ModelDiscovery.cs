using System.Text.Json;
using Blazorly.Harness.Llm.Adapters;

namespace Blazorly.Harness.Llm;

/// <summary>
/// Discovers the model list of an OpenAI-compatible route via GET {base}/models and merges
/// the ids over a known catalog (catalog metadata wins for known ids; discovered-only ids
/// appear without metadata).
/// </summary>
public static class LlmModelDiscovery
{
    public static async Task<IReadOnlyList<LlmModelInfo>> DiscoverAsync(
        string provider, string baseUrl, string apiKey, HttpClient http, Action<HttpRequestMessage>? configure = null, CancellationToken ct = default)
    {
        // Anthropic serves its model list under /v1/models with x-api-key auth; OpenAI-compatible routes use /models + bearer.
        var path = provider == "anthropic" ? "/v1/models" : "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}{path}");
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
        var ids = new List<string>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } modelId)
                {
                    ids.Add(modelId);
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } modelId)
                {
                    ids.Add(modelId);
                }
            }
        }
        return Merge(provider, ids, []);
    }

    /// <summary>Discovered ids merged over known metadata; known ids keep their catalog entry.</summary>
    public static IReadOnlyList<LlmModelInfo> Merge(string provider, IEnumerable<string> discoveredIds, IReadOnlyList<LlmModelInfo> known)
    {
        var merged = new List<LlmModelInfo>(known);
        var seen = new HashSet<string>(known.Select(m => m.Id), StringComparer.Ordinal);
        foreach (var id in discoveredIds)
        {
            if (!seen.Add(id)) continue;
            merged.Add(new LlmModelInfo(provider, id, id));
        }
        return merged;
    }
}
