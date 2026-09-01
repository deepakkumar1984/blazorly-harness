using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blazorly.Harness.Llm.Adapters;

/// <summary>
/// Streams from the Anthropic Messages API (POST /v1/messages SSE). Tool results replay as
/// user tool_result blocks, tool calls as tool_use blocks with parsed input objects, and the
/// system prompt rides the top-level system parameter. Reasoning deltas stream in as
/// thinking blocks but are dropped on replay (Anthropic requires signed thinking blocks).
/// </summary>
public sealed class AnthropicAdapter : LlmAdapter
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public const int DefaultMaxTokens = 8192;

    private readonly HttpClient _http;
    private readonly string _provider;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly IReadOnlyList<LlmModelInfo> _models;
    private readonly string _userAgent;
    private readonly Func<string, (byte[] Data, string MimeType)?>? _attachmentResolver;

    public AnthropicAdapter(string provider, string baseUrl, string apiKey, IReadOnlyList<LlmModelInfo> models, HttpClient http, string? userAgent = null, Func<string, (byte[] Data, string MimeType)?>? attachmentResolver = null)
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
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
            throw OpenAiCompatibleAdapter.ClassifyHttp((int)response.StatusCode, errorBody, response.Headers);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var fold = new AnthropicFold();
        await foreach (var payload in OpenAiCompatibleAdapter.SsePayloads(reader, ct).ConfigureAwait(false))
        {
            if (payload is null) continue;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var startUsage))
                    {
                        fold.InputUsage = ParseUsage(startUsage);
                    }
                    break;
                case "content_block_start":
                {
                    var index = root.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
                    if (root.TryGetProperty("content_block", out var block))
                    {
                        foreach (var chunk in fold.BlockStart(index, block)) yield return chunk;
                    }
                    break;
                }
                case "content_block_delta":
                {
                    var index = root.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
                    if (root.TryGetProperty("delta", out var delta))
                    {
                        foreach (var chunk in fold.Delta(index, delta)) yield return chunk;
                    }
                    break;
                }
                case "message_delta":
                    if (root.TryGetProperty("delta", out var stopDelta) && stopDelta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                    {
                        fold.StopReason = sr.GetString();
                    }
                    if (root.TryGetProperty("usage", out var outUsage) && outUsage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number)
                    {
                        fold.OutputTokens = ot.GetInt64();
                    }
                    break;
                case "message_stop":
                    fold.Stopped = true;
                    break;
                case "error":
                    // Adapter contract: a failed call throws; the runtime normalizes it into an error finish.
                    throw ClassifyError(root);
            }
        }

        foreach (var chunk in fold.ToChunks()) yield return chunk;
    }

    public object BuildWireBody(GenerateOptions options) => new Dictionary<string, object?>
    {
        ["model"] = options.Model,
        ["max_tokens"] = options.MaxTokens ?? DefaultMaxTokens,
        ["stream"] = true,
        ["system"] = string.IsNullOrEmpty(options.System) ? null : options.System,
        ["messages"] = BuildWireMessages(options),
        ["tools"] = options.Tools is { Count: > 0 }
            ? options.Tools.Select(t => (object)new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.Parameters,
            }).ToList()
            : null,
        ["temperature"] = options.Temperature,
        ["stop_sequences"] = options.Stop is { Count: > 0 } ? options.Stop.ToList() : null,
    };

    /// <summary>Wire messages with consecutive same-role entries merged (the API requires alternating roles).</summary>
    internal List<Dictionary<string, object?>> BuildWireMessages(GenerateOptions options)
    {
        var wire = new List<Dictionary<string, object?>>();
        foreach (var message in options.Messages)
        {
            var (role, blocks) = WireBlocks(message);
            if (blocks.Count == 0) continue;
            if (wire.Count > 0 && wire[^1]["role"] as string == role
                && wire[^1]["content"] is List<object> existing)
            {
                existing.AddRange(blocks);
                continue;
            }
            wire.Add(new Dictionary<string, object?> { ["role"] = role, ["content"] = blocks });
        }
        return wire;
    }

    private (string Role, List<object> Blocks) WireBlocks(Llm.Message message)
    {
        var blocks = new List<object>();
        if (message.Role == "user" && message.Content.OfType<ToolResultBlock>().FirstOrDefault() is { } toolResult)
        {
            var toolText = Flatten(toolResult.Content);
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolResult.ToolCallId,
                ["content"] = new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = toolText.Length > 0 ? toolText : "(no output)" } },
                ["is_error"] = toolResult.IsError == true ? true : null,
            });
            return ("user", blocks);
        }
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Length > 0:
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ReasoningBlock:
                    break; // unsigned thinking cannot be replayed to Anthropic
                case ImageBlock image when message.Role == "user":
                {
                    var resolved = _attachmentResolver?.Invoke(image.AttachmentId);
                    if (resolved is null) break;
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "image",
                        ["source"] = new Dictionary<string, object?>
                        {
                            ["type"] = "base64",
                            ["media_type"] = resolved.Value.MimeType,
                            ["data"] = Convert.ToBase64String(resolved.Value.Data),
                        },
                    });
                    break;
                }
                case ToolCallBlock toolCall:
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.Id,
                        ["name"] = toolCall.Name,
                        ["input"] = ParseJsonObject(toolCall.Arguments),
                    });
                    break;
            }
        }
        if (blocks.Count == 0) blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = "" });
        return (message.Role, blocks);
    }

    private static JsonElement ParseJsonObject(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments.Length == 0 ? "{}" : arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.Clone();
        }
        catch (JsonException) { }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static string Flatten(IReadOnlyList<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextBlock>().Select(b => b.Text));

    private static TokenUsage? ParseUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number ? it.GetInt64() : 0;
        var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt64() : 0;
        var cacheWrite = usage.TryGetProperty("cache_creation_input_tokens", out var cw) && cw.ValueKind == JsonValueKind.Number ? cw.GetInt64() : 0;
        return new TokenUsage(input, 0, cacheRead > 0 ? cacheRead : null, cacheWrite > 0 ? cacheWrite : null, null);
    }

    private static LlmException ClassifyError(JsonElement payload)
    {
        var code = payload.TryGetProperty("error", out var error) && error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var message = payload.TryGetProperty("error", out var e2) && e2.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "provider error";
        return code switch
        {
            "rate_limit_error" => new LlmException(LlmErrorCodes.RateLimit, message ?? "rate limited"),
            "authentication_error" or "permission_error" => new LlmException(LlmErrorCodes.Auth, message ?? "rejected credentials"),
            "overloaded_error" or "api_error" => new LlmException(LlmErrorCodes.Server, message ?? "provider server error"),
            "timeout_error" => new LlmException(LlmErrorCodes.Timeout, message ?? "request timed out"),
            "invalid_request_error" or "request_too_large" => new LlmException(LlmErrorCodes.InvalidRequest, message ?? "provider rejected request"),
            _ => new LlmException(LlmErrorCodes.Server, message ?? "provider error"),
        };
    }

    /// <summary>Accumulates Anthropic SSE events into the harness chunk protocol.</summary>
    private sealed class AnthropicFold
    {
        private const int TextIndex = 0;
        private const int ReasoningIndex = 1;
        private int _nextToolIndex = 2;
        private readonly Dictionary<int, int> _wireToHarness = new();
        private readonly Dictionary<int, (string Id, string? Name)> _tools = new();
        private readonly Dictionary<int, StringBuilder> _toolArgs = new();
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private bool _textOpened;
        private bool _reasoningOpened;

        public TokenUsage? InputUsage { get; set; }
        public long? OutputTokens { get; set; }
        public string? StopReason { get; set; }
        public bool Stopped { get; set; }

        public IEnumerable<StreamChunk> BlockStart(int wireIndex, JsonElement block)
        {
            var type = block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            switch (type)
            {
                case "text":
                    _textOpened = true;
                    yield return new BlockStartChunk(TextIndex, "text");
                    break;
                case "thinking":
                    _reasoningOpened = true;
                    yield return new BlockStartChunk(ReasoningIndex, "reasoning");
                    break;
                case "tool_use":
                {
                    var id = block.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                    var name = block.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : null;
                    var harnessIndex = _nextToolIndex++;
                    _wireToHarness[wireIndex] = harnessIndex;
                    _tools[harnessIndex] = (id ?? Ids.NewCallId(), name);
                    _toolArgs[harnessIndex] = new StringBuilder();
                    yield return new BlockStartChunk(harnessIndex, "tool-call");
                    break;
                }
            }
        }

        public IEnumerable<StreamChunk> Delta(int wireIndex, JsonElement delta)
        {
            var type = delta.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            switch (type)
            {
                case "text_delta" when delta.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String:
                    _text.Append(text.GetString());
                    yield return new TextDeltaChunk(TextIndex, text.GetString() ?? "");
                    break;
                case "thinking_delta" when delta.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String:
                    _reasoning.Append(thinking.GetString());
                    yield return new ReasoningDeltaChunk(ReasoningIndex, thinking.GetString() ?? "");
                    break;
                case "input_json_delta" when delta.TryGetProperty("partial_json", out var json) && json.ValueKind == JsonValueKind.String:
                {
                    if (!_wireToHarness.TryGetValue(wireIndex, out var harnessIndex)) yield break;
                    var args = json.GetString() ?? "";
                    if (args.Length == 0) yield break;
                    var sb = _toolArgs[harnessIndex];
                    sb.Append(args);
                    var (id, name) = _tools[harnessIndex];
                    yield return new ToolCallDeltaChunk(harnessIndex, id, name, args);
                    break;
                }
            }
        }

        public IReadOnlyList<StreamChunk> ToChunks()
        {
            var chunks = new List<StreamChunk>();
            if (InputUsage is not null)
            {
                chunks.Add(new UsageChunk(new TokenUsage(
                    InputUsage.InputTokens,
                    OutputTokens ?? 0,
                    InputUsage.CacheReadTokens,
                    InputUsage.CacheWriteTokens)));
            }
            if (_reasoningOpened && _reasoning.Length > 0)
            {
                chunks.Add(new BlockEndChunk(ReasoningIndex, new ReasoningBlock(_reasoning.ToString())));
            }
            if (_textOpened && _text.Length > 0)
            {
                chunks.Add(new BlockEndChunk(TextIndex, new TextBlock(_text.ToString())));
            }
            foreach (var index in _tools.Keys.OrderBy(i => i))
            {
                var (id, name) = _tools[index];
                chunks.Add(new BlockEndChunk(index, new ToolCallBlock(id, name ?? "", _toolArgs[index].ToString())));
            }
            if ((!_textOpened || _text.Length == 0) && !_reasoningOpened && _tools.Count == 0)
            {
                throw new LlmException(LlmErrorCodes.EmptyResponse, "provider returned no content");
            }
            chunks.Add(new FinishChunk(MapFinish(StopReason)));
            return chunks;
        }

        private string MapFinish(string? wire) => wire switch
        {
            "tool_use" => FinishReason.ToolCalls,
            "max_tokens" => FinishReason.MaxTokens,
            null when !Stopped => FinishReason.Error,
            _ => FinishReason.Stop,
        };
    }
}
