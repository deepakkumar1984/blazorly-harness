using System.Net;
using System.Text.Json;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

public class ResponsesApiAdapterTests
{
    private static GenerateOptions Options(string? system = null, string? sessionId = null, string? effort = null) => new()
    {
        Provider = "xai",
        Model = "grok-4.6",
        System = system,
        SessionId = sessionId,
        ReasoningEffort = effort,
        Messages =
        [
            Message.CreateUserText("hello"),
            Message.CreateAssistant("xai", "grok-4.6",
            [
                new TextBlock("working"),
                new ToolCallBlock("call_1", "bash", "{\"command\":\"ls\"}"),
            ]),
            Message.CreateToolResult("call_1", [new TextBlock("file-a\nfile-b")]),
        ],
        Tools = [ToolSchemaJson.FromJson("bash", "Run a shell command", """{"type":"object","properties":{"command":{"type":"string"}}}""")],
    };

    [Fact]
    public void MapsHarnessMessagesToResponsesInput()
    {
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "k", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(Options("be brief", sessionId: "session-1", effort: "high"));
        var json = JsonSerializer.Serialize(body);

        Assert.False(body.ContainsKey("messages"));
        Assert.Equal(true, body["stream"]);
        Assert.Equal(false, body["store"]);
        Assert.Equal("be brief", body["instructions"]);
        Assert.Equal("session-1", body["prompt_cache_key"]);
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.NotNull(body["input"]);

        Assert.Contains("\"type\":\"function_call_output\"", json);
        Assert.Contains("\"call_id\":\"call_1\"", json);
        Assert.Contains("\"type\":\"function_call\"", json);
        Assert.Contains("\"name\":\"bash\"", json);
        Assert.DoesNotContain("\"role\":\"tool\"", json);
        Assert.DoesNotContain("\"function\":{\"name\":\"bash\"", json);
        Assert.DoesNotContain("reasoning_content", json);

        var reasoning = Assert.IsType<Dictionary<string, object?>>(body["reasoning"]);
        Assert.Equal("high", reasoning["effort"]);
    }

    [Fact]
    public void ToolsAreFlatFunctionSchema()
    {
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "k", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(Options());
        var json = JsonSerializer.Serialize(body);
        Assert.Contains("\"type\":\"function\"", json);
        Assert.Contains("\"name\":\"bash\"", json);
        Assert.Contains("\"tool_choice\":\"auto\"", json);
        Assert.DoesNotContain("\"function\":{", json);
    }

    [Fact]
    public void ArgumentlessTool_GetsEmptyPropertiesObject()
    {
        // xAI 400s with invalid_type at function.parameters.properties when properties is missing.
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "k", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(new GenerateOptions
        {
            Provider = "xai",
            Model = "grok-4.6",
            Messages = [Message.CreateUserText("list jobs")],
            Tools = [ToolSchemaJson.FromJson("job_list", "List jobs", """{"type":"object"}""")],
        });
        var json = JsonSerializer.Serialize(body);
        Assert.Contains("\"properties\":{}", json);
        using var doc = JsonDocument.Parse(json);
        var parameters = doc.RootElement.GetProperty("tools")[0].GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, parameters.GetProperty("properties").ValueKind);
    }

    [Fact]
    public void UnionParameters_AreLeftAlone()
    {
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "k", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(new GenerateOptions
        {
            Provider = "xai",
            Model = "grok-4.6",
            Messages = [Message.CreateUserText("hi")],
            Tools = [ToolSchemaJson.FromJson("contact", "Reach someone",
                """{"oneOf":[{"type":"object","properties":{"email":{"type":"string"}}}]}""")],
        });
        var json = JsonSerializer.Serialize(body);
        Assert.Contains("\"oneOf\"", json);
        using var doc = JsonDocument.Parse(json);
        var parameters = doc.RootElement.GetProperty("tools")[0].GetProperty("parameters");
        Assert.True(parameters.TryGetProperty("oneOf", out _));
        Assert.False(parameters.TryGetProperty("properties", out _));
    }

    [Fact]
    public void SessionTitle_UsesLowReasoningEffort()
    {
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "k", [], new HttpClient());
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(new GenerateOptions
        {
            Provider = "xai",
            Model = "grok-4.6",
            Messages = [Message.CreateUserText("title this")],
            ReasoningEffort = "xhigh",
            Purpose = "session-title",
        });
        var reasoning = Assert.IsType<Dictionary<string, object?>>(body["reasoning"]);
        Assert.Equal("low", reasoning["effort"]);
    }

    [Fact]
    public async Task PostsToResponsesEndpoint()
    {
        var http = new StaticHttpHandler("""
            data: {"type":"response.output_text.delta","delta":"hi"}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":10,"output_tokens":1}}}

            data: [DONE]

            """);
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "sk-xai", [], new HttpClient(http));
        await foreach (var _ in adapter.Stream(Options())) { }
        Assert.Equal("https://api.x.ai/v1/responses", http.LastRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", http.LastRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("sk-xai", http.LastRequest?.Headers.Authorization?.Parameter);
        Assert.DoesNotContain("/chat/completions", http.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task ParsesSseStreamIntoChunks()
    {
        var sse = string.Join("\n",
        [
            """data: {"type":"response.output_item.added","output_index":0,"item":{"id":"rs_1","type":"reasoning"}}""",
            "",
            """data: {"type":"response.reasoning_summary_text.delta","delta":"let me think"}""",
            "",
            """data: {"type":"response.output_item.added","output_index":1,"item":{"id":"msg_1","type":"message","role":"assistant"}}""",
            "",
            """data: {"type":"response.output_text.delta","item_id":"msg_1","delta":"Hello"}""",
            "",
            """data: {"type":"response.output_item.added","output_index":2,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"bash","arguments":""}}""",
            "",
            """data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":2,"delta":"{\"command\":"}""",
            "",
            """data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":2,"delta":"\"ls\"}"}""",
            "",
            """data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":20},"output_tokens":7,"output_tokens_details":{"reasoning_tokens":4}}}}""",
            "",
            "data: [DONE]",
            "",
        ]);
        var http = new StaticHttpHandler(sse);
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "key", [], new HttpClient(http));
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in adapter.Stream(Options())) chunks.Add(chunk);

        Assert.Contains(chunks, c => c is ReasoningDeltaChunk r && r.Text == "let me think");
        Assert.Contains(chunks, c => c is TextDeltaChunk t && t.Text == "Hello");
        var assembled = new BlockAssembler();
        foreach (var chunk in chunks) assembled.Push(chunk);
        var blocks = assembled.Blocks();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("{\"command\":\"ls\"}", Assert.IsType<ToolCallBlock>(blocks[2]).Arguments);
        Assert.Equal("call_1", Assert.IsType<ToolCallBlock>(blocks[2]).Id);
        Assert.Equal(FinishReason.ToolCalls, assembled.Finish!.Reason);
        Assert.Equal(80, assembled.Usage!.InputTokens); // 100 input - 20 cached
        Assert.Equal(7, assembled.Usage!.OutputTokens);
        Assert.Equal(4, assembled.Usage!.ReasoningTokens);
    }

    [Fact]
    public async Task WholeFunctionCallChunk_DoesNotDoubleAppend()
    {
        // xAI may return a function call whole in one event rather than streaming arguments.
        var sse = string.Join("\n",
        [
            """data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"bash","arguments":"{\"command\":\"ls\"}"}}""",
            "",
            """data: {"type":"response.function_call_arguments.done","output_index":0,"call_id":"call_1","name":"bash","arguments":"{\"command\":\"ls\"}"}""",
            "",
            """data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call","call_id":"call_1","name":"bash","arguments":"{\"command\":\"ls\"}"}],"usage":{"input_tokens":8,"output_tokens":3}}}""",
            "",
            "data: [DONE]",
            "",
        ]);
        var http = new StaticHttpHandler(sse);
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "key", [], new HttpClient(http));
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in adapter.Stream(Options())) chunks.Add(chunk);

        var assembled = new BlockAssembler();
        foreach (var chunk in chunks) assembled.Push(chunk);
        var call = Assert.IsType<ToolCallBlock>(Assert.Single(assembled.Blocks()));
        Assert.Equal("{\"command\":\"ls\"}", call.Arguments);
        Assert.Equal(FinishReason.ToolCalls, assembled.Finish!.Reason);
    }

    [Fact]
    public async Task CompletedPayloadWithoutDeltas_FoldsOutput()
    {
        var sse = string.Join("\n",
        [
            """data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}],"usage":{"input_tokens":5,"output_tokens":1}}}""",
            "",
            "data: [DONE]",
            "",
        ]);
        var http = new StaticHttpHandler(sse);
        var adapter = new ResponsesApiAdapter("openai", "https://api.openai.com/v1", "key", [], new HttpClient(http));
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in adapter.Stream(Options())) chunks.Add(chunk);

        var assembled = new BlockAssembler();
        foreach (var chunk in chunks) assembled.Push(chunk);
        var text = Assert.IsType<TextBlock>(Assert.Single(assembled.Blocks()));
        Assert.Equal("done", text.Text);
        Assert.Equal(FinishReason.Stop, assembled.Finish!.Reason);
    }

    [Fact]
    public async Task MissingKey_OnKeyRequiringRoute_FailsFast()
    {
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "", [], new HttpClient());
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
        {
            var e = adapter.Stream(Options()).GetAsyncEnumerator();
            return e.MoveNextAsync().AsTask();
        });
        Assert.Equal(LlmErrorCodes.MissingCredential, ex.Code);
        Assert.Contains("api key for provider 'xai'", ex.Message);
    }

    [Fact]
    public void Catalog_RoutesXaiAndOpenAiToResponses()
    {
        Assert.True(ProviderCatalog.UsesResponsesApi("xai"));
        Assert.True(ProviderCatalog.UsesResponsesApi("openai"));
        Assert.False(ProviderCatalog.UsesResponsesApi("deepseek"));
        Assert.False(ProviderCatalog.UsesResponsesApi("groq"));
        Assert.False(ProviderCatalog.UsesResponsesApi("ollama"));
        Assert.False(ProviderCatalog.UsesResponsesApi("anthropic"));
    }

    private sealed class StaticHttpHandler(string body) : HttpClientHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
