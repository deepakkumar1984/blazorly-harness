using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blazorly.Harness.Llm.Adapters;

/// <summary>
/// Streams from the Anthropic Messages API (POST /v1/messages SSE). Tool results replay as
/// user tool_result blocks, tool calls as tool_use blocks with parsed input objects, and the
/// system prompt rides the top-level system parameter. Extended thinking is requested through the
/// <c>thinking</c> parameter; thinking blocks stream in as reasoning deltas and replay only when
/// they carry the signature Anthropic attested them with.
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
        _baseUrl = TransportErrors.TrimApiSuffixes(baseUrl, "/v1/messages", "/messages", "/v1");
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
        catch (TaskCanceledException ex)
        {
            // Reached only when the caller did not cancel: the HTTP client's own timeout fired.
            throw new LlmException(LlmErrorCodes.Timeout, TransportErrors.DescribeTimeout($"{_baseUrl}/v1/messages", ex));
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(LlmErrorCodes.Transport, TransportErrors.DescribeSendFailure(ex, $"{_baseUrl}/v1/messages", body.Length));
        }
        catch (IOException ex)
        {
            throw new LlmException(LlmErrorCodes.Transport, TransportErrors.DescribeSendFailure(ex, $"{_baseUrl}/v1/messages", body.Length));
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
        await foreach (var payload in TransportErrors.GuardSse(OpenAiCompatibleAdapter.SsePayloads(reader, ct), $"{_baseUrl}/v1/messages", ct).ConfigureAwait(false))
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

    /// <summary>Absent fields are omitted, never null: System.Text.Json writes null
    /// dictionary values through, and strict gateways (Azure Pydantic) 400 on them.</summary>
    public object BuildWireBody(GenerateOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["max_tokens"] = options.MaxTokens ?? DefaultMaxTokens,
            ["stream"] = true,
            ["messages"] = BuildWireMessages(options),
        };
        if (!string.IsNullOrEmpty(options.System)) body["system"] = options.System;
        if (options.Tools is { Count: > 0 })
        {
            body["tools"] = options.Tools.Select(t => (object)new
            {
                name = t.Name,
                description = t.Description,
                input_schema = ToolParameterSchemas.Normalize(t.Parameters),
            }).ToList();
        }
        var thinking = BuildThinkingFields(options);
        foreach (var (key, value) in thinking) body[key] = value;
        // Anthropic rejects any temperature it did not default while thinking is enabled.
        if (options.Temperature is not null && !ThinkingEnabled(thinking)) body["temperature"] = options.Temperature;
        if (options.Stop is { Count: > 0 }) body["stop_sequences"] = options.Stop.ToList();
        return body;
    }

    /// <summary>
    /// Extended thinking for the Messages API. Anthropic only emits thinking blocks when the
    /// request asks for them, so without this field the effort picker is a no-op on Anthropic
    /// routes and the gateway answers with a blank thinking block — observed live on Azure's
    /// Anthropic endpoint as one empty <c>thinking_delta</c> per tool-use step, with real
    /// thinking never reaching the transcript. <c>budget_tokens</c> must sit strictly below
    /// <c>max_tokens</c>, so a request with no room for reasoning leaves thinking out instead of
    /// provoking a 400. Title requests always run thinking-disabled.
    /// </summary>
    public IReadOnlyDictionary<string, object?> BuildThinkingFields(GenerateOptions options)
    {
        var effort = options.ReasoningEffort;
        if (options.Purpose == "session-title" || effort == "off") return DisabledThinking;
        if (effort is null) return NoThinkingFields; // unset keeps the provider default
        var maxTokens = options.MaxTokens ?? DefaultMaxTokens;
        if (maxTokens <= MinThinkingBudget) return NoThinkingFields;
        return new Dictionary<string, object?>
        {
            ["thinking"] = new Dictionary<string, object?>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = Math.Min(OpenAiCompatibleAdapter.ThinkingBudgetTokens(effort), maxTokens - 1),
            },
        };
    }

    /// <summary>Anthropic's floor for <c>budget_tokens</c>; also the room a request needs to think at all.</summary>
    public const int MinThinkingBudget = 1024;

    private static readonly IReadOnlyDictionary<string, object?> NoThinkingFields = new Dictionary<string, object?>();

    private static readonly IReadOnlyDictionary<string, object?> DisabledThinking = new Dictionary<string, object?>
    {
        ["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" },
    };

    private static bool ThinkingEnabled(IReadOnlyDictionary<string, object?> fields)
        => fields.TryGetValue("thinking", out var value)
           && value is Dictionary<string, object?> thinking
           && thinking.TryGetValue("type", out var type)
           && type is "enabled";

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
            var result = new Dictionary<string, object?>
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolResult.ToolCallId,
                ["content"] = new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = toolText.Length > 0 ? toolText : "(no output)" } },
            };
            // Omitted when false: null dictionary values serialize through, and strict
            // gateways 400 on them.
            if (toolResult.IsError == true) result["is_error"] = true;
            blocks.Add(result);
            return ("user", blocks);
        }
        // Signed thinking leads the turn it came from: Anthropic replays it only with its
        // signature and rejects a tool_use that arrives without the thinking block preceding it.
        foreach (var reasoning in message.Content.OfType<ReasoningBlock>())
        {
            if (reasoning.Signature is not { Length: > 0 } signature) continue;
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "thinking",
                ["thinking"] = reasoning.Text,
                ["signature"] = signature,
            });
        }
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Length > 0:
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ReasoningBlock:
                    break; // replayed above when signed; unsigned thinking cannot be replayed
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
        private string? _signature;
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
                // text and thinking open lazily on their first non-empty delta. Gateways answer a
                // tool-use turn with a thinking block that carries no text at all, and opening the
                // block here left an empty reasoning block in every transcript.
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
                {
                    var value = text.GetString() ?? "";
                    if (value.Length == 0) yield break;
                    if (!_textOpened)
                    {
                        _textOpened = true;
                        yield return new BlockStartChunk(TextIndex, "text");
                    }
                    _text.Append(value);
                    yield return new TextDeltaChunk(TextIndex, value);
                    break;
                }
                case "thinking_delta" when delta.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String:
                {
                    var value = thinking.GetString() ?? "";
                    if (value.Length == 0) yield break;
                    if (!_reasoningOpened)
                    {
                        _reasoningOpened = true;
                        yield return new BlockStartChunk(ReasoningIndex, "reasoning");
                    }
                    _reasoning.Append(value);
                    yield return new ReasoningDeltaChunk(ReasoningIndex, value);
                    break;
                }
                case "signature_delta" when delta.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.String:
                    // The attestation that lets this thinking block be replayed on the next turn.
                    _signature = signature.GetString();
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
            if (!Stopped && StopReason is null)
                throw new LlmException(LlmErrorCodes.StreamClosed, "Provider stream ended before a completion was reported.");
            var chunks = new List<StreamChunk>();
            if (InputUsage is not null)
            {
                chunks.Add(new UsageChunk(new TokenUsage(
                    InputUsage.InputTokens,
                    OutputTokens ?? 0,
                    InputUsage.CacheReadTokens,
                    InputUsage.CacheWriteTokens)));
            }
            if (_reasoning.Length > 0)
            {
                chunks.Add(new BlockEndChunk(ReasoningIndex, new ReasoningBlock(_reasoning.ToString(), _signature)));
            }
            if (_text.Length > 0)
            {
                chunks.Add(new BlockEndChunk(TextIndex, new TextBlock(_text.ToString())));
            }
            foreach (var index in _tools.Keys.OrderBy(i => i))
            {
                var (id, name) = _tools[index];
                chunks.Add(new BlockEndChunk(index, new ToolCallBlock(id, name ?? "", _toolArgs[index].ToString())));
            }
            if (_text.Length == 0 && _reasoning.Length == 0 && _tools.Count == 0)
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
