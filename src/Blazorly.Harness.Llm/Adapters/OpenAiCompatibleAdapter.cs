using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blazorly.Harness.Llm.Adapters;

/// <summary>
/// Streams from any OpenAI-compatible chat-completions endpoint (DeepSeek, OpenAI, vLLM, …)
/// via server-sent events. Harness tool-result blocks map to role:'tool' wire messages;
/// reasoning maps to DeepSeek's reasoning_content field.
/// </summary>
public sealed class OpenAiCompatibleAdapter : LlmAdapter
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly string _provider;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly IReadOnlyList<LlmModelInfo> _models;
    private readonly string _userAgent;
    private readonly Func<string, (byte[] Data, string MimeType)?>? _attachmentResolver;

    /// <param name="attachmentResolver">Resolves attachment ids into bytes for image input (inline base64).</param>
    public OpenAiCompatibleAdapter(string provider, string baseUrl, string apiKey, IReadOnlyList<LlmModelInfo> models, HttpClient http, string? userAgent = null, Func<string, (byte[] Data, string MimeType)?>? attachmentResolver = null)
    {
        _attachmentResolver = attachmentResolver;
        _provider = provider;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _models = models;
        _http = http;
        _userAgent = userAgent ?? "blazorly-harness";
    }

    public override string Provider => _provider;

    public override IReadOnlyList<LlmModelInfo> ListModels() => _models;

    public override async IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new LlmException(LlmErrorCodes.MissingCredential, $"api key for provider '{_provider}' is not configured");

        var body = JsonSerializer.Serialize(BuildWireBody(options), WireOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorCodes.Aborted, "request cancelled");
        }
        catch (TaskCanceledException)
        {
            throw new LlmException(LlmErrorCodes.Timeout, "request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(LlmErrorCodes.Transport, ex.Message);
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw ClassifyHttp((int)response.StatusCode, errorBody, response.Headers);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var fold = new AdapterFold();
        await foreach (var payload in SsePayloads(reader, ct).ConfigureAwait(false))
        {
            if (payload is null) continue;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                fold.Usage = ParseUsage(usage);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            string? finish = null;
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString();
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                {
                    foreach (var chunk in fold.ReasoningDelta(rc.GetString() ?? "")) yield return chunk;
                }
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    foreach (var chunk in fold.TextDelta(content.GetString() ?? "")) yield return chunk;
                }
                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var wireIndex = tc.TryGetProperty("index", out var wi) && wi.ValueKind == JsonValueKind.Number ? wi.GetInt32() : 0;
                        string? id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                        JsonElement? fn = tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object ? fnEl : null;
                        string? name = fn?.TryGetProperty("name", out var n) == true && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        string args = fn?.TryGetProperty("arguments", out var a) == true && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
                        foreach (var chunk in fold.ToolCallDelta(wireIndex, id, name, args)) yield return chunk;
                    }
                }
            }
            if (finish is not null)
            {
                fold.FinishReasonWire = finish;
                break;
            }
        }

        foreach (var chunk in fold.ToChunks()) yield return chunk;
    }

    private static TokenUsage? ParseUsage(JsonElement usage)
    {
        var prompt = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt64() : 0;
        var cached = 0L;
        if (usage.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object
            && ptd.TryGetProperty("cached_tokens", out var c) && c.ValueKind == JsonValueKind.Number)
        {
            cached = c.GetInt64();
        }
        else if (usage.TryGetProperty("prompt_cache_hit_tokens", out var pch) && pch.ValueKind == JsonValueKind.Number)
        {
            cached = pch.GetInt64();
        }
        var completion = usage.TryGetProperty("completion_tokens", out var cpt) && cpt.ValueKind == JsonValueKind.Number ? cpt.GetInt64() : 0;
        long? reasoning = usage.TryGetProperty("completion_tokens_details", out var cd) && cd.ValueKind == JsonValueKind.Object
            && cd.TryGetProperty("reasoning_tokens", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt64() : null;
        var uncached = Math.Max(0, prompt - cached);
        return new TokenUsage(uncached, completion, cached > 0 ? (long?)cached : null, null, reasoning);
    }

    public object BuildWireBody(GenerateOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = BuildWireMessages(options),
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["tools"] = options.Tools is { Count: > 0 }
                ? options.Tools.Select(t => (object)new
                {
                    type = "function",
                    function = new { name = t.Name, description = t.Description, parameters = t.Parameters },
                }).ToList()
                : null,
            ["tool_choice"] = options.Tools is { Count: > 0 } ? "auto" : null,
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxTokens,
            ["stop"] = options.Stop is { Count: > 0 } ? options.Stop.ToList() : null,
        };
        foreach (var (key, value) in BuildThinkingFields(options)) body[key] = value;
        return body;
    }

    /// <summary>
    /// DeepSeek's thinking extension (dsh llm-deepseek serialize.ts): "off" disables thinking,
    /// anything else enables it and carries the effort on <c>reasoning_effort</c>. Title requests
    /// always run thinking-disabled. Generic OpenAI-compatible routes only pass the effort through.
    /// </summary>
    public IReadOnlyDictionary<string, object?> BuildThinkingFields(GenerateOptions options)
    {
        var effort = options.ReasoningEffort;
        if (options.Provider == "deepseek")
        {
            return options.Purpose == "session-title" || effort == "off"
                ? new Dictionary<string, object?> { ["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" } }
                : effort is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>
                    {
                        ["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" },
                        ["reasoning_effort"] = effort,
                    };
        }
        return effort is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["reasoning_effort"] = effort };
    }

    public static LlmException ClassifyHttp(int status, string body, System.Net.Http.Headers.HttpHeaders? headers = null)
    {
        var exception = ClassifyHttp(status, body);
        if (headers is not null && TryRetryAfterMs(headers, out var retryAfterMs) && retryAfterMs is not null)
            return new LlmException(exception.Failure with { ProviderRetryAfterMs = retryAfterMs });
        return exception;
    }

    /// <summary>Reads Retry-After as delta-seconds or an HTTP-date into milliseconds.</summary>
    internal static bool TryRetryAfterMs(System.Net.Http.Headers.HttpHeaders headers, out long? retryAfterMs)
    {
        retryAfterMs = null;
        if (!headers.TryGetValues("Retry-After", out var values)) return false;
        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(raw)) return false;
        if (long.TryParse(raw, out var seconds))
        {
            retryAfterMs = Math.Max(0, seconds * 1000);
            return true;
        }
        if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
        {
            retryAfterMs = Math.Max(0, (long)(when - DateTimeOffset.UtcNow).TotalMilliseconds);
            return true;
        }
        return false;
    }

    public static LlmException ClassifyHttp(int status, string body)
    {
        var text = $"{body}".ToLowerInvariant();
        if (status == 401 || status == 403) return new LlmException(LlmErrorCodes.Auth, $"provider rejected credentials ({status})");
        if (status == 429) return new LlmException(LlmErrorCodes.RateLimit, $"rate limited ({status})");
        if (status is 400 or 413 && (text.Contains("context length") || text.Contains("maximum context") || text.Contains("too long")))
            return new LlmException(LlmErrorCodes.ContextWindowExceeded, $"request exceeds context window ({status})");
        if (status >= 500) return new LlmException(LlmErrorCodes.Server, $"provider server error ({status})");
        return new LlmException(LlmErrorCodes.InvalidRequest, $"provider rejected request ({status}): {Truncate(body, 400)}");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    internal List<object> BuildWireMessages(GenerateOptions options)
    {
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(options.System))
        {
            messages.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = options.System });
        }
        foreach (var message in options.Messages)
        {
            switch (message.Role)
            {
                case "user" when message.Content.OfType<ToolResultBlock>().FirstOrDefault() is { } toolResult:
                    var toolText = Flatten(toolResult.Content);
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolResult.ToolCallId,
                        ["content"] = toolText.Length > 0 ? toolText : "(no output)",
                    });
                    break;
                case "user" when message.Content.OfType<ImageBlock>().Any() && AttachmentResolverAvailable:
                {
                    // Image-bearing user content becomes multimodal parts: text + inline base64 images.
                    var parts = new List<object>();
                    var text = Flatten(message.Content);
                    if (text.Length > 0) parts.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text });
                    foreach (var image in message.Content.OfType<ImageBlock>())
                    {
                        var resolved = ResolveAttachment(image.AttachmentId);
                        if (resolved is null) continue;
                        parts.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object?>
                            {
                                ["url"] = $"data:{resolved.Value.MimeType};base64,{Convert.ToBase64String(resolved.Value.Data)}",
                            },
                        });
                    }
                    messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = parts });
                    break;
                }
                case "user":
                    messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = Flatten(message.Content) });
                    break;
                case "assistant":
                {
                    var reasoning = message.Content.OfType<ReasoningBlock>().FirstOrDefault()?.Text;
                    var assistantText = Flatten(message.Content);
                    var toolCalls = message.Content.OfType<ToolCallBlock>().ToList();
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = assistantText.Length > 0 ? assistantText : "",
                        ["reasoning_content"] = string.IsNullOrWhiteSpace(reasoning) ? null : reasoning,
                        ["tool_calls"] = toolCalls.Count > 0
                            ? toolCalls.Select(tc => (object)new
                            {
                                id = tc.Id,
                                type = "function",
                                function = new { name = tc.Name, arguments = tc.Arguments },
                            }).ToList()
                            : null,
                    });
                    break;
                }
            }
        }
        return messages;
    }

    private bool AttachmentResolverAvailable => _attachmentResolver is not null;

    private (byte[] Data, string MimeType)? ResolveAttachment(string id) => _attachmentResolver?.Invoke(id);

    private static string Flatten(IReadOnlyList<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextBlock>().Select(b => b.Text));

    /// <summary>Yields SSE data payloads; the stream ends at the literal [DONE] or EOF.</summary>
    internal static async IAsyncEnumerable<string?> SsePayloads(StreamReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new LlmException(LlmErrorCodes.Aborted, "request cancelled");
            }
            if (line is null)
            {
                if (data.Length > 0) yield return data.ToString();
                yield break;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    if (payload == "[DONE]") yield break;
                    yield return payload;
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line["data:".Length..].TrimStart());
            }
        }
    }

    /// <summary>Accumulates wire deltas and emits harness chunks: deltas live, block-ends at the end.</summary>
    private sealed class AdapterFold
    {
        private const int TextIndex = 0;
        private const int ReasoningIndex = 1;
        private int _nextToolIndex = 2;
        private readonly Dictionary<int, int> _toolWireToHarness = new();
        private readonly Dictionary<int, string> _toolIds = new();
        private readonly Dictionary<int, string> _toolNames = new();
        private readonly Dictionary<int, StringBuilder> _toolArgs = new();
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private bool _textOpened;
        private bool _reasoningOpened;

        public TokenUsage? Usage { get; set; }
        public string? FinishReasonWire { get; set; }

        public IEnumerable<StreamChunk> ReasoningDelta(string delta)
        {
            if (delta.Length == 0) yield break;
            if (!_reasoningOpened)
            {
                _reasoningOpened = true;
                yield return new BlockStartChunk(ReasoningIndex, "reasoning");
            }
            _reasoning.Append(delta);
            yield return new ReasoningDeltaChunk(ReasoningIndex, delta);
        }

        public IEnumerable<StreamChunk> TextDelta(string delta)
        {
            if (delta.Length == 0) yield break;
            if (!_textOpened)
            {
                _textOpened = true;
                yield return new BlockStartChunk(TextIndex, "text");
            }
            _text.Append(delta);
            yield return new TextDeltaChunk(TextIndex, delta);
        }

        public IEnumerable<StreamChunk> ToolCallDelta(int wireIndex, string? id, string? name, string argsDelta)
        {
            if (!_toolWireToHarness.TryGetValue(wireIndex, out var harnessIndex))
            {
                harnessIndex = _nextToolIndex++;
                _toolWireToHarness[wireIndex] = harnessIndex;
                _toolIds[harnessIndex] = id ?? Ids.NewCallId();
                _toolArgs[harnessIndex] = new StringBuilder();
                yield return new BlockStartChunk(harnessIndex, "tool-call");
            }
            if (id is { Length: > 0 }) _toolIds[harnessIndex] = id;
            if (name is { Length: > 0 }) _toolNames[harnessIndex] = name;
            var sb = _toolArgs[harnessIndex];
            if (argsDelta.Length > 0)
            {
                sb.Append(argsDelta);
                yield return new ToolCallDeltaChunk(harnessIndex, _toolIds[harnessIndex], name, argsDelta);
            }
        }

        public IReadOnlyList<StreamChunk> ToChunks()
        {
            var chunks = new List<StreamChunk>();
            if (Usage is not null) chunks.Add(new UsageChunk(Usage));
            if (_reasoningOpened && _reasoning.Length > 0)
            {
                chunks.Add(new BlockEndChunk(ReasoningIndex, new ReasoningBlock(_reasoning.ToString())));
            }
            if (_textOpened && _text.Length > 0)
            {
                chunks.Add(new BlockEndChunk(TextIndex, new TextBlock(_text.ToString())));
            }
            else if (!_reasoningOpened && _toolIds.Count == 0)
            {
                throw new LlmException(LlmErrorCodes.EmptyResponse, "provider returned no content");
            }
            foreach (var (index, id) in _toolIds)
            {
                var block = new ToolCallBlock(id, _toolNames.GetValueOrDefault(index) ?? "", _toolArgs.GetValueOrDefault(index)?.ToString() ?? "{}");
                chunks.Add(new BlockEndChunk(index, block));
            }
            chunks.Add(new FinishChunk(MapFinish(FinishReasonWire)));
            return chunks;
        }

        private static string MapFinish(string? wire) => wire switch
        {
            "tool_calls" => FinishReason.ToolCalls,
            "length" => FinishReason.MaxTokens,
            _ => FinishReason.Stop,
        };
    }
}
