using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Blazorly.Harness.Core.Decisions;

public sealed record SystemOneOptions
{
    /// <summary>Service base URL. Override for a self-hosted gateway or a test fake.</summary>
    public string BaseUrl { get; init; } = "https://api.typesafe.ai";

    /// <summary>Decision endpoint, appended to <see cref="BaseUrl"/>.</summary>
    public string Path { get; init; } = "/v1/systemone";

    public required string ApiKey { get; init; }

    /// <summary>Model id requested; null means the service default.</summary>
    public string? Model { get; init; }

    /// <summary>Auth header style: "bearer" (Authorization: Bearer …) or "apikey" (X-API-Key: …).</summary>
    public string AuthStyle { get; init; } = "bearer";

    /// <summary>Hard wall-clock cap for one call. This sits inside the cancel path, so it is tight.</summary>
    public int TimeoutMs { get; init; } = 1_500;

    /// <summary>Longest state part sent to the service (chars).</summary>
    public int MaxStateChars { get; init; } = DecisionState.DefaultPartChars;
}

/// <summary>
/// TypeSafe "System One" decision client: unstructured state plus typed questions in, calibrated
/// decisions out, one parallel pass. Never generates text.
///
/// WIRE FORMAT — read this before debugging. System One shipped as waitlisted early access and
/// the public request/response schema is not pinned, so this adapter sends a self-describing
/// payload and parses the reply *liberally*: any of the common shapes below are accepted.
///
/// Sent:
/// <code>
/// { "model": "…",
///   "state":     { "brief": "…", "cwd": "…" },
///   "questions": [ { "key": "needs_plan", "kind": "noul",  "prompt": "…" },
///                  { "key": "route",      "kind": "choice", "prompt": "…", "options": ["a","b"] },
///                  { "key": "risk",       "kind": "rating", "prompt": "…", "min": 0, "max": 10 } ] }
/// </code>
///
/// Accepted back (any of):
/// <code>
/// { "answers":   { "needs_plan": { "probability": 0.93 } } }
/// { "decisions": [ { "key": "needs_plan", "probability": 0.93 } ] }
/// { "needs_plan": 0.93 }
/// { "result": { … any of the above … } }
/// </code>
///
/// If the real service disagrees, fix it in exactly two places: <see cref="BuildRequest"/> and
/// <see cref="TryReadAnswer"/>. Run <c>blazorly decisions probe</c> to see the raw reply first.
/// </summary>
public sealed class SystemOneClient : IDecisionModel, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly SystemOneOptions _options;
    private readonly bool _ownsClient;

    public SystemOneClient(SystemOneOptions options)
        : this(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }), options, ownsClient: true)
    {
    }

    /// <summary>Test/fake-server constructor: the caller owns the client.</summary>
    public SystemOneClient(HttpClient httpClient, SystemOneOptions options, bool ownsClient = false)
    {
        _http = httpClient;
        _options = options;
        _ownsClient = ownsClient;
        switch (options.AuthStyle.ToLowerInvariant())
        {
            case "apikey":
            case "x-api-key":
                _http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", options.ApiKey);
                break;
            default:
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                break;
        }
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    }

    public string Impl => "systemone";

    public bool Available => true;

    public string? ModelVersion => _options.Model;

    public async Task<DecisionResult?> DecideAsync(
        DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct = default)
    {
        if (questions.Count == 0) return null;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint())
            {
                Content = Content(BuildRequestJson(state, questions)),
            };
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Degraded(stopwatch.ElapsedMilliseconds, state,
                    $"HTTP {(int)response.StatusCode}: {Clip(body, 240)}");
            }
            return Parse(body, stopwatch.ElapsedMilliseconds, state, questions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user cancelled: surface it. Swallowing this as a timeout would put an invisible
            // wait inside the measured cancel path, which the interruption contract forbids.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout, not the user cancelling: no opinion, the caller falls back.
            return Degraded(stopwatch.ElapsedMilliseconds, state, $"timed out after {_options.TimeoutMs}ms");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return Degraded(stopwatch.ElapsedMilliseconds, state, Clip(ex.Message, 240));
        }
    }

    /// <summary>
    /// Raw round-trip for <c>blazorly decisions probe</c>: request and reply, unparsed. Sends
    /// through the same <see cref="BuildRequestJson"/> + <see cref="Content"/> path as
    /// <see cref="DecideAsync"/>, so what the probe prints is byte-identical to what a live seam
    /// would have sent — a probe that serialised differently would be verifying the wrong thing.
    /// </summary>
    public async Task<(string Request, int Status, string Response)> ProbeAsync(
        DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct = default)
    {
        var request = BuildRequestJson(state, questions);
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint()) { Content = Content(request) };
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        return (request, (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private string Endpoint() => _options.BaseUrl.TrimEnd('/') + (_options.Path.StartsWith('/') ? _options.Path : "/" + _options.Path);

    /// <summary>
    /// Explicit Content-Length rather than <c>PostAsJsonAsync</c>: that helper streams a
    /// <c>JsonContent</c> as chunked, which some gateways and proxies reject or mis-read. Decisions
    /// are small, so buffering costs nothing and buys a body every server can read.
    /// </summary>
    private static StringContent Content(string json)
        => new(json, System.Text.Encoding.UTF8, "application/json");

    private string BuildRequestJson(DecisionState state, IReadOnlyList<DecisionQuestion> questions)
        => JsonSerializer.Serialize(BuildRequest(state, questions), Json);

    private object BuildRequest(DecisionState state, IReadOnlyList<DecisionQuestion> questions) => new
    {
        model = _options.Model,
        state = state.Parts.ToDictionary(p => p.Name, p => (object)p.Text, StringComparer.Ordinal),
        questions = questions.Select(q => q switch
        {
            Choice choice => (object)new
            {
                key = choice.Key,
                kind = "choice",
                prompt = choice.Prompt,
                options = choice.Options.Take(Choice.MaxOptions).ToArray(),
            },
            Rating rating => new
            {
                key = rating.Key,
                kind = "rating",
                prompt = rating.Prompt,
                min = rating.Min,
                max = rating.Max,
            },
            _ => new { key = q.Key, kind = "noul", prompt = q.Prompt },
        }).ToArray(),
    };

    private DecisionResult Parse(
        string body, long latencyMs, DecisionState state, IReadOnlyList<DecisionQuestion> questions)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException ex) { return Degraded(latencyMs, state, $"unparseable response: {ex.Message}"); }

        using (document)
        {
            var root = Unwrap(document.RootElement);
            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            string? modelVersion = null;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("model", out var mv) && mv.ValueKind == JsonValueKind.String)
            {
                modelVersion = mv.GetString();
            }

            foreach (var question in questions)
            {
                var answer = TryReadAnswer(root, question);
                if (answer is not null) answers[question.Key] = answer;
            }

            if (answers.Count == 0)
                return Degraded(latencyMs, state, $"no recognised answers in the response ({Clip(body, 240)})", modelVersion);

            return new DecisionResult(
                Impl, answers, latencyMs, state.Chars,
                Degraded: answers.Count < questions.Count,
                Error: answers.Count < questions.Count
                    ? $"answered {answers.Count}/{questions.Count} questions"
                    : null,
                ModelVersion: modelVersion ?? _options.Model);
        }
    }

    /// <summary>Some gateways nest the payload; accept one level of wrapping.</summary>
    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root;
        foreach (var wrapper in new[] { "result", "data", "response", "output" })
        {
            if (root.TryGetProperty(wrapper, out var inner)
                && inner.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return inner;
        }
        return root;
    }

    /// <summary>
    /// Liberal answer extraction: finds this question's key as an object property, an array entry,
    /// or a bare scalar, then reads whichever value fields the service happened to use.
    /// </summary>
    public static DecisionAnswer? TryReadAnswer(JsonElement root, DecisionQuestion question)
    {
        var node = FindNode(root, question.Key);
        if (node is null) return null;
        var value = node.Value;

        // A bare scalar keyed by the question name: { "needs_plan": 0.93 } or { "route": "cheap" }.
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False)
            return FromScalar(question, value);

        if (value.ValueKind != JsonValueKind.Object) return null;

        double? probability = FirstNumber(value, "probability", "p", "yes", "noul", "confidenceYes");
        double? score = FirstNumber(value, "score", "rating", "value", "amount");
        double? confidence = FirstNumber(value, "confidence", "certainty");
        string? choice = FirstString(value, "value", "choice", "answer", "selected", "option", "label");
        var options = ReadDistribution(value);

        return question switch
        {
            Noul => probability is { } p ? new DecisionAnswer(question.Key, DecisionAnswer.NoulKind)
                { Probability = Clamp01(p), Confidence = confidence } : null,

            Choice c => choice is { Length: > 0 } picked && c.Options.Contains(picked, StringComparer.Ordinal)
                ? new DecisionAnswer(question.Key, DecisionAnswer.ChoiceKind)
                { Value = picked, Options = options, Confidence = confidence ?? (options?.TryGetValue(picked, out var pp) == true ? pp : null) }
                : null, // an invented option is no answer at all — the point of a typed choice

            Rating r => (score ?? probability) is { } s
                ? new DecisionAnswer(question.Key, DecisionAnswer.RatingKind)
                { Score = Math.Clamp(s, r.Min, r.Max), Confidence = confidence }
                : null,

            _ => null,
        };
    }

    private static JsonElement? FindNode(JsonElement root, string key)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var direct)) return direct;
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var container in new[] { "answers", "decisions", "results", "questions", "responses" })
        {
            if (!root.TryGetProperty(container, out var child)) continue;
            if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty(key, out var nested)) return nested;
            if (child.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in child.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var entryKey = FirstString(entry, "key", "id", "name", "question");
                if (string.Equals(entryKey, key, StringComparison.Ordinal)) return entry;
            }
        }
        return null;
    }

    private static DecisionAnswer? FromScalar(DecisionQuestion question, JsonElement value)
    {
        switch (question)
        {
            case Noul:
                var p = ScalarAsDouble(value);
                return p is null ? null : new DecisionAnswer(question.Key, DecisionAnswer.NoulKind) { Probability = Clamp01(p.Value) };
            case Choice c:
                if (value.ValueKind != JsonValueKind.String) return null;
                var picked = value.GetString()!;
                return c.Options.Contains(picked, StringComparer.Ordinal)
                    ? new DecisionAnswer(question.Key, DecisionAnswer.ChoiceKind) { Value = picked }
                    : null;
            case Rating r:
                var s = ScalarAsDouble(value);
                return s is null ? null : new DecisionAnswer(question.Key, DecisionAnswer.RatingKind) { Score = Math.Clamp(s.Value, r.Min, r.Max) };
            default:
                return null;
        }
    }

    private static double? ScalarAsDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDouble(out var n) ? n : null,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
        _ => null,
    };

    private static double? FirstNumber(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out var number))
                return number;
        }
        return null;
    }

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }
        return null;
    }

    /// <summary>Reads a full option→probability map when the service reports one.</summary>
    private static IReadOnlyDictionary<string, double>? ReadDistribution(JsonElement obj)
    {
        foreach (var name in new[] { "options", "probabilities", "distribution", "scores" })
        {
            if (!obj.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) continue;
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var property in map.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
                    result[property.Name] = number;
            }
            if (result.Count > 0) return result;
        }
        return null;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

    private DecisionResult Degraded(long latencyMs, DecisionState state, string error, string? modelVersion = null)
        => new(Impl, new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal), latencyMs, state.Chars,
            Degraded: true, Error: error, ModelVersion: modelVersion ?? _options.Model);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
