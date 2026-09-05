using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blazorly.Harness.Llm.Adapters;

/// <summary>
/// Streams from the OpenAI/xAI Responses API (POST /responses SSE). Chat Completions is
/// legacy on those hosts: this adapter maps harness messages onto <c>input</c> items,
/// flat function tools, and typed stream events (output_text / reasoning / function_call).
/// Conversation history stays local (<c>store: false</c>); the harness is the source of truth.
/// </summary>
public sealed class ResponsesApiAdapter : LlmAdapter
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
    private readonly bool _requireApiKey;
    private readonly IReadOnlyList<LlmModelInfo> _models;
    private readonly string _userAgent;
    private readonly Func<string, (byte[] Data, string MimeType)?>? _attachmentResolver;

    public ResponsesApiAdapter(string provider, string baseUrl, string apiKey, IReadOnlyList<LlmModelInfo> models, HttpClient http, string? userAgent = null, Func<string, (byte[] Data, string MimeType)?>? attachmentResolver = null, bool requireApiKey = true)
    {
        _attachmentResolver = attachmentResolver;
        _provider = provider;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _requireApiKey = requireApiKey;
        _models = models;
        _http = http;
        _userAgent = userAgent ?? "blazorly-harness";
    }

    public override string Provider => _provider;

    public override IReadOnlyList<LlmModelInfo> ListModels() => _models;

    public override async IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) && _requireApiKey)
            throw new LlmException(LlmErrorCodes.MissingCredential, $"api key for provider '{_provider}' is not configured");

        var body = JsonSerializer.Serialize(BuildWireBody(options), WireOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
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
            throw OpenAiCompatibleAdapter.ClassifyHttp((int)response.StatusCode, errorBody, response.Headers);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var fold = new ResponsesFold();
        await foreach (var payload in OpenAiCompatibleAdapter.SsePayloads(reader, ct).ConfigureAwait(false))
        {
            if (payload is null || payload == "[DONE]") continue;
            using var doc = JsonDocument.Parse(payload);
            foreach (var chunk in fold.Handle(doc.RootElement)) yield return chunk;
        }

        foreach (var chunk in fold.ToChunks()) yield return chunk;
    }

    public object BuildWireBody(GenerateOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["input"] = BuildInput(options),
            ["instructions"] = string.IsNullOrEmpty(options.System) ? null : options.System,
            ["stream"] = true,
            ["store"] = false,
            ["tools"] = options.Tools is { Count: > 0 }
                ? options.Tools.Select(t => (object)new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = NormalizeFunctionParameters(t.Parameters),
                }).ToList()
                : null,
            ["tool_choice"] = options.Tools is { Count: > 0 } ? "auto" : null,
            ["temperature"] = options.Temperature,
            ["max_output_tokens"] = options.MaxTokens,
            ["prompt_cache_key"] = string.IsNullOrWhiteSpace(options.SessionId) ? null : options.SessionId,
            ["include"] = new[] { "reasoning.encrypted_content" },
        };
        foreach (var (key, value) in BuildReasoningFields(options)) body[key] = value;
        if (options.Stop is { Count: > 0 } && options.ReasoningEffort is null)
            body["stop"] = options.Stop.ToList();
        return body;
    }

    /// <summary>
    /// xAI compiles function tools into a grammar and requires <c>parameters.properties</c>
    /// to be an object (or a union of objects). Argument-less tools historically serialized
    /// as <c>{"type":"object"}</c>, which Chat Completions accepted and Responses rejects.
    /// </summary>
    internal static JsonElement NormalizeFunctionParameters(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            return EmptyObjectParameters();
        if (parameters.TryGetProperty("oneOf", out _) || parameters.TryGetProperty("anyOf", out _))
            return parameters;
        if (parameters.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            return parameters;

        var map = new Dictionary<string, object?>();
        foreach (var property in parameters.EnumerateObject())
            map[property.Name] = property.Value.Clone();
        map.TryAdd("type", "object");
        map["properties"] = new Dictionary<string, object?>();
        return JsonSerializer.SerializeToElement(map);
    }

    private static JsonElement EmptyObjectParameters()
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
        });

    /// <summary>
    /// Responses API takes <c>reasoning.effort</c>. Title requests run at low effort so they
    /// stay cheap; <c>off</c> maps to <c>low</c> because xAI reasoning models reject disable.
    /// </summary>
    public IReadOnlyDictionary<string, object?> BuildReasoningFields(GenerateOptions options)
    {
        var effort = options.Purpose == "session-title" ? "low" : options.ReasoningEffort;
        if (effort is null) return new Dictionary<string, object?>();
        if (effort is "off" or "none") effort = "low";
        return new Dictionary<string, object?>
        {
            ["reasoning"] = new Dictionary<string, object?> { ["effort"] = effort },
        };
    }

    internal List<object> BuildInput(GenerateOptions options)
    {
        var input = new List<object>();
        foreach (var message in options.Messages)
        {
            switch (message.Role)
            {
                case "user" when message.Content.OfType<ToolResultBlock>().FirstOrDefault() is { } toolResult:
                    var toolText = Flatten(toolResult.Content);
                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = toolResult.ToolCallId,
                        ["output"] = toolText.Length > 0 ? toolText : "(no output)",
                    });
                    break;
                case "user" when message.Content.OfType<ImageBlock>().Any() && AttachmentResolverAvailable:
                {
                    var parts = new List<object>();
                    var text = Flatten(message.Content);
                    if (text.Length > 0) parts.Add(new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = text });
                    foreach (var image in message.Content.OfType<ImageBlock>())
                    {
                        var resolved = ResolveAttachment(image.AttachmentId);
                        if (resolved is null) continue;
                        parts.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:{resolved.Value.MimeType};base64,{Convert.ToBase64String(resolved.Value.Data)}",
                        });
                    }
                    input.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = parts });
                    break;
                }
                case "user":
                    input.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = Flatten(message.Content) });
                    break;
                case "assistant":
                {
                    // Unsigned reasoning summaries cannot be replayed; encrypted traces are not
                    // persisted on ReasoningBlock yet. Function calls and visible text are.
                    foreach (var toolCall in message.Content.OfType<ToolCallBlock>())
                    {
                        input.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "function_call",
                            ["call_id"] = toolCall.Id,
                            ["name"] = toolCall.Name,
                            ["arguments"] = toolCall.Arguments,
                        });
                    }
                    var assistantText = Flatten(message.Content);
                    if (assistantText.Length > 0)
                    {
                        input.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = assistantText });
                    }
                    break;
                }
            }
        }
        return input;
    }

    private bool AttachmentResolverAvailable => _attachmentResolver is not null;

    private (byte[] Data, string MimeType)? ResolveAttachment(string id) => _attachmentResolver?.Invoke(id);

    private static string Flatten(IReadOnlyList<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextBlock>().Select(b => b.Text));

    /// <summary>Accumulates Responses SSE events into the harness chunk protocol.</summary>
    private sealed class ResponsesFold
    {
        private const int TextIndex = 0;
        private const int ReasoningIndex = 1;
        private int _nextToolIndex = 2;
        private readonly Dictionary<int, int> _toolWireToHarness = new();
        private readonly Dictionary<string, int> _itemIdToWire = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _toolIds = new();
        private readonly Dictionary<int, string> _toolNames = new();
        private readonly Dictionary<int, StringBuilder> _toolArgs = new();
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private bool _textOpened;
        private bool _reasoningOpened;

        public TokenUsage? Usage { get; set; }
        public string? FinishReasonWire { get; set; }

        public IEnumerable<StreamChunk> Handle(JsonElement root)
        {
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type is null)
            {
                // Some gateways omit event types and send a completed response object.
                if (root.TryGetProperty("output", out _) || root.TryGetProperty("status", out _))
                    return HandleCompleted(root);
                return [];
            }

            return type switch
            {
                "response.output_item.added" => HandleItemAdded(root),
                "response.output_text.delta" or "response.text.delta" => HandleTextDelta(root),
                "response.reasoning_text.delta" or "response.reasoning_summary_text.delta" => HandleReasoningDelta(root),
                "response.function_call_arguments.delta" => HandleFunctionArgsDelta(root),
                "response.function_call_arguments.done" => HandleFunctionArgsDone(root),
                "response.output_item.done" => HandleItemDone(root),
                "response.completed" => HandleCompleted(ResponseObject(root)),
                "response.incomplete" => HandleIncomplete(root),
                "response.failed" or "error" => ThrowFailed(root),
                _ => [],
            };
        }

        private IEnumerable<StreamChunk> HandleItemAdded(JsonElement root)
        {
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                yield break;
            var wireIndex = OutputIndex(root);
            if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { Length: > 0 } itemId)
                _itemIdToWire[itemId] = wireIndex;

            var itemType = item.TryGetProperty("type", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null;
            switch (itemType)
            {
                case "message":
                    foreach (var chunk in OpenText()) yield return chunk;
                    break;
                case "reasoning":
                    foreach (var chunk in OpenReasoning()) yield return chunk;
                    break;
                case "function_call":
                    foreach (var chunk in OpenFunctionCall(item, wireIndex)) yield return chunk;
                    break;
            }
        }

        private IEnumerable<StreamChunk> HandleTextDelta(JsonElement root)
        {
            var delta = DeltaText(root);
            if (delta.Length == 0) yield break;
            foreach (var chunk in OpenText()) yield return chunk;
            _text.Append(delta);
            yield return new TextDeltaChunk(TextIndex, delta);
        }

        private IEnumerable<StreamChunk> HandleReasoningDelta(JsonElement root)
        {
            var delta = DeltaText(root);
            if (delta.Length == 0) yield break;
            foreach (var chunk in OpenReasoning()) yield return chunk;
            _reasoning.Append(delta);
            yield return new ReasoningDeltaChunk(ReasoningIndex, delta);
        }

        private IEnumerable<StreamChunk> HandleFunctionArgsDelta(JsonElement root)
        {
            var wireIndex = ResolveWireIndex(root);
            var delta = DeltaText(root);
            if (delta.Length == 0) yield break;
            foreach (var chunk in EnsureTool(wireIndex, id: null, name: null)) yield return chunk;
            foreach (var chunk in AppendToolArgs(wireIndex, delta)) yield return chunk;
        }

        private IEnumerable<StreamChunk> HandleFunctionArgsDone(JsonElement root)
        {
            var wireIndex = ResolveWireIndex(root);
            var id = root.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() : null;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var arguments = root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            foreach (var chunk in OpenFunctionCallFields(wireIndex, id, name, arguments)) yield return chunk;
        }

        private IEnumerable<StreamChunk> HandleItemDone(JsonElement root)
        {
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                yield break;
            var itemType = item.TryGetProperty("type", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null;
            if (itemType != "function_call") yield break;
            var wireIndex = OutputIndex(root);
            foreach (var chunk in OpenFunctionCall(item, wireIndex)) yield return chunk;
        }

        private IEnumerable<StreamChunk> HandleCompleted(JsonElement response)
        {
            if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                Usage = ParseUsage(usage);
            if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                throw ClassifyError(error);

            var status = response.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            if (status == "failed") throw ClassifyError(response.TryGetProperty("error", out var err) ? err : default);
            if (status == "incomplete")
                FinishReasonWire = IncompleteReason(response) ?? "length";

            // Only fold the final output array when the stream sent no live deltas
            // (some gateways emit a single completed payload).
            if (!_textOpened && !_reasoningOpened && _toolIds.Count == 0
                && response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in output.EnumerateArray())
                {
                    foreach (var chunk in IngestOutputItem(item, index++)) yield return chunk;
                }
            }

            if (FinishReasonWire is null)
                FinishReasonWire = _toolIds.Count > 0 ? "tool_calls" : "stop";
        }

        private IEnumerable<StreamChunk> HandleIncomplete(JsonElement root)
        {
            var response = ResponseObject(root);
            FinishReasonWire = IncompleteReason(response) ?? "length";
            if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                Usage = ParseUsage(usage);
            yield break;
        }

        private IEnumerable<StreamChunk> IngestOutputItem(JsonElement item, int wireIndex)
        {
            var itemType = item.TryGetProperty("type", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null;
            switch (itemType)
            {
                case "message":
                    foreach (var chunk in IngestMessage(item)) yield return chunk;
                    break;
                case "reasoning":
                    foreach (var chunk in IngestReasoning(item)) yield return chunk;
                    break;
                case "function_call":
                    foreach (var chunk in OpenFunctionCall(item, wireIndex)) yield return chunk;
                    break;
            }
        }

        private IEnumerable<StreamChunk> IngestMessage(JsonElement item)
        {
            if (!item.TryGetProperty("content", out var content)) yield break;
            if (content.ValueKind == JsonValueKind.String)
            {
                foreach (var chunk in AppendFullText(content.GetString() ?? "")) yield return chunk;
                yield break;
            }
            if (content.ValueKind != JsonValueKind.Array) yield break;
            foreach (var part in content.EnumerateArray())
            {
                var partType = part.TryGetProperty("type", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() : null;
                if (partType is "output_text" or "text"
                    && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    foreach (var chunk in AppendFullText(text.GetString() ?? "")) yield return chunk;
                }
            }
        }

        private IEnumerable<StreamChunk> IngestReasoning(JsonElement item)
        {
            if (!item.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
                yield break;
            foreach (var part in summary.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    foreach (var chunk in AppendFullReasoning(text.GetString() ?? "")) yield return chunk;
                }
            }
        }

        private IEnumerable<StreamChunk> AppendFullText(string text)
        {
            if (text.Length == 0 || _text.Length > 0) yield break;
            foreach (var chunk in OpenText()) yield return chunk;
            _text.Append(text);
            yield return new TextDeltaChunk(TextIndex, text);
        }

        private IEnumerable<StreamChunk> AppendFullReasoning(string text)
        {
            if (text.Length == 0 || _reasoning.Length > 0) yield break;
            foreach (var chunk in OpenReasoning()) yield return chunk;
            _reasoning.Append(text);
            yield return new ReasoningDeltaChunk(ReasoningIndex, text);
        }

        private IEnumerable<StreamChunk> OpenText()
        {
            if (_textOpened) yield break;
            _textOpened = true;
            yield return new BlockStartChunk(TextIndex, "text");
        }

        private IEnumerable<StreamChunk> OpenReasoning()
        {
            if (_reasoningOpened) yield break;
            _reasoningOpened = true;
            yield return new BlockStartChunk(ReasoningIndex, "reasoning");
        }

        private IEnumerable<StreamChunk> OpenFunctionCall(JsonElement item, int wireIndex)
        {
            var id = item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() : null;
            id ??= item.TryGetProperty("id", out var iid) && iid.ValueKind == JsonValueKind.String ? iid.GetString() : null;
            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var arguments = item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            foreach (var chunk in OpenFunctionCallFields(wireIndex, id, name, arguments)) yield return chunk;
        }

        private IEnumerable<StreamChunk> OpenFunctionCallFields(int wireIndex, string? id, string? name, string arguments)
        {
            foreach (var chunk in EnsureTool(wireIndex, id, name)) yield return chunk;
            if (arguments.Length == 0) yield break;
            var harnessIndex = _toolWireToHarness[wireIndex];
            if (_toolArgs[harnessIndex].Length > 0) yield break; // already streamed as deltas
            foreach (var chunk in AppendToolArgs(wireIndex, arguments)) yield return chunk;
        }

        private IEnumerable<StreamChunk> EnsureTool(int wireIndex, string? id, string? name)
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
        }

        private IEnumerable<StreamChunk> AppendToolArgs(int wireIndex, string argsDelta)
        {
            if (argsDelta.Length == 0) yield break;
            var harnessIndex = _toolWireToHarness[wireIndex];
            _toolArgs[harnessIndex].Append(argsDelta);
            yield return new ToolCallDeltaChunk(harnessIndex, _toolIds[harnessIndex], _toolNames.GetValueOrDefault(harnessIndex), argsDelta);
        }

        public IReadOnlyList<StreamChunk> ToChunks()
        {
            var chunks = new List<StreamChunk>();
            if (Usage is not null) chunks.Add(new UsageChunk(Usage));
            if (_reasoningOpened && _reasoning.Length > 0)
                chunks.Add(new BlockEndChunk(ReasoningIndex, new ReasoningBlock(_reasoning.ToString())));
            if (_textOpened && _text.Length > 0)
                chunks.Add(new BlockEndChunk(TextIndex, new TextBlock(_text.ToString())));
            else if (!_reasoningOpened && _toolIds.Count == 0)
                throw new LlmException(LlmErrorCodes.EmptyResponse, "provider returned no content");
            foreach (var (index, id) in _toolIds)
            {
                var block = new ToolCallBlock(id, _toolNames.GetValueOrDefault(index) ?? "", _toolArgs.GetValueOrDefault(index)?.ToString() ?? "{}");
                chunks.Add(new BlockEndChunk(index, block));
            }
            chunks.Add(new FinishChunk(MapFinish(FinishReasonWire)));
            return chunks;
        }

        private int ResolveWireIndex(JsonElement root)
        {
            if (root.TryGetProperty("item_id", out var itemId) && itemId.ValueKind == JsonValueKind.String
                && itemId.GetString() is { Length: > 0 } id && _itemIdToWire.TryGetValue(id, out var mapped))
            {
                return mapped;
            }
            return OutputIndex(root);
        }

        private static int OutputIndex(JsonElement root)
            => root.TryGetProperty("output_index", out var oi) && oi.ValueKind == JsonValueKind.Number ? oi.GetInt32() : 0;

        private static string DeltaText(JsonElement root)
        {
            if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                return delta.GetString() ?? "";
            if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? "";
            return "";
        }

        private static JsonElement ResponseObject(JsonElement root)
            => root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object ? response : root;

        private static string? IncompleteReason(JsonElement response)
        {
            if (response.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                return reason.GetString();
            }
            return null;
        }

        private static IEnumerable<StreamChunk> ThrowFailed(JsonElement root)
        {
            var error = root.TryGetProperty("error", out var e) ? e
                : root.TryGetProperty("response", out var response) && response.TryGetProperty("error", out var re) ? re
                : root;
            throw ClassifyError(error);
        }

        private static LlmException ClassifyError(JsonElement error)
        {
            var message = error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : "provider error";
            var code = error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            return code switch
            {
                "rate_limit_exceeded" or "rate_limit_error" => new LlmException(LlmErrorCodes.RateLimit, message ?? "rate limited"),
                "invalid_api_key" or "authentication_error" => new LlmException(LlmErrorCodes.Auth, message ?? "rejected credentials"),
                "context_length_exceeded" => new LlmException(LlmErrorCodes.ContextWindowExceeded, message ?? "request exceeds context window"),
                _ => new LlmException(LlmErrorCodes.Server, message ?? "provider error"),
            };
        }

        private static TokenUsage? ParseUsage(JsonElement usage)
        {
            var input = ReadInt(usage, "input_tokens") ?? ReadInt(usage, "prompt_tokens") ?? 0;
            var output = ReadInt(usage, "output_tokens") ?? ReadInt(usage, "completion_tokens") ?? 0;
            long cached = 0;
            if (usage.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object)
                cached = ReadInt(itd, "cached_tokens") ?? 0;
            else if (usage.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object)
                cached = ReadInt(ptd, "cached_tokens") ?? 0;
            long? reasoning = null;
            if (usage.TryGetProperty("output_tokens_details", out var otd) && otd.ValueKind == JsonValueKind.Object)
                reasoning = ReadInt(otd, "reasoning_tokens");
            else if (usage.TryGetProperty("completion_tokens_details", out var ctd) && ctd.ValueKind == JsonValueKind.Object)
                reasoning = ReadInt(ctd, "reasoning_tokens");
            var uncached = Math.Max(0, input - cached);
            return new TokenUsage(uncached, output, cached > 0 ? cached : null, null, reasoning);
        }

        private static long? ReadInt(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt64() : null;

        private static string MapFinish(string? wire) => wire switch
        {
            "tool_calls" => FinishReason.ToolCalls,
            "max_output_tokens" or "length" or "max_tokens" => FinishReason.MaxTokens,
            _ => FinishReason.Stop,
        };
    }
}
