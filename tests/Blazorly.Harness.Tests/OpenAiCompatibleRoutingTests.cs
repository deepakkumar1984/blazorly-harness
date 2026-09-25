using System.Net;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Retry;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;

namespace Blazorly.Harness.Tests;

public class OpenAiCompatibleRoutingTests
{
    public const string RequiresResponses = "Function tools with reasoning_effort are not supported for gpt-6-astra in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.";

    private static GenerateOptions Options(string model = "gpt-6-astra") => new()
    {
        Provider = "custom",
        Model = model,
        Messages = [Message.CreateUserText("hello")],
        Tools = [ToolSchemaJson.FromJson("probe", "Test tool", """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""")],
        MaxTokens = 2048,
        ReasoningEffort = "high",
    };

    [Theory]
    [InlineData("gpt-6-astra")]
    [InlineData("gpt-6-astra-2026-09-01")]
    [InlineData("openai/gpt-6-astra")]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-luna")]
    [InlineData("gpt-5")]
    [InlineData("gpt-5.4")]
    public async Task ModernOpenAiModels_UseResponsesOnFirstRequest(string model)
    {
        using var handler = new RecordingHandler((_, _) => TextResponse());
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http);

        await AssertSuccessfulStream(adapter, Options(model));

        Assert.Equal("https://gateway.test/v1/responses", Assert.Single(handler.Urls));
        Assert.Equal("Bearer key", Assert.Single(handler.Authorization));
        var body = Assert.Single(handler.Bodies);
        AssertResponsesBody(body);
        Assert.Equal(model, body.GetProperty("model").GetString());
        Assert.Equal("probe", body.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("https://gateway.test/v1/chat/completions", "gpt-6-astra")]
    [InlineData("https://gateway.test/v1/chat/completions/", "gpt-6-astra")]
    [InlineData("https://gateway.test/v1/responses", "deployment-alias")]
    public async Task PastedCallUrl_UsesSingleResponsesPath(string baseUrl, string model)
    {
        using var handler = new RecordingHandler((_, _) => TextResponse());
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", baseUrl, "", [], http, requireApiKey: false);

        await AssertSuccessfulStream(adapter, Options(model));

        Assert.Equal("https://gateway.test/v1/responses", Assert.Single(handler.Urls));
        Assert.Null(Assert.Single(handler.Authorization));
    }

    [Fact]
    public async Task MixedGateway_SelectsTheProtocolForEachModel()
    {
        using var handler = new RecordingHandler((body, _) => body.TryGetProperty("input", out var input)
            ? TextResponse()
            : Sse("""data: {"choices":[{"delta":{"content":"hello"},"finish_reason":"stop"}]}"""));
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http);

        await AssertSuccessfulStream(adapter, Options("qwen3"));
        await AssertSuccessfulStream(adapter, Options("gpt-6-astra"));
        await AssertSuccessfulStream(adapter, Options("deepseek-v4-flash"));

        Assert.Equal([
            "https://gateway.test/v1/chat/completions",
            "https://gateway.test/v1/responses",
            "https://gateway.test/v1/chat/completions",
        ], handler.Urls);
        Assert.True(handler.Bodies[0].TryGetProperty("messages", out _));
        AssertResponsesBody(handler.Bodies[1]);
        Assert.True(handler.Bodies[2].TryGetProperty("messages", out _));
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("openai-compatible")]
    public async Task ToolRoundTripAndSecondTurn_StayOnResponses(string provider)
    {
        using var handler = new RecordingHandler((_, call) => call == 0 ? ToolResponse() : TextResponse());
        using var http = new HttpClient(handler);
        await using var harness = TestHarness.Create();
        harness.Llm.RegisterAdapter(new OpenAiCompatibleAdapter(provider, "https://gateway.test/v1", "key", [], http));
        harness.Loop.DefaultSelection = Options().ToCallConfig() with { Provider = provider };
        RetryService.Mount(harness.Ctx);
        var probe = new ProbeTool("probe");
        var executed = new List<string>();
        probe.BodyStarted += executed.Add;
        harness.Tools.Register(probe);
        var agent = harness.CreateAgent(options: new AgentOptions(provider, "gpt-6-astra", 2048, "high"));

        agent.Followup(Message.CreateUserText("run the probe"));
        await agent.WhenIdleAsync();
        agent.Followup(Message.CreateUserText("continue"));
        await agent.WhenIdleAsync();

        Assert.Equal(["checked"], executed);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.All(handler.Urls, url => Assert.Equal("https://gateway.test/v1/responses", url));
        Assert.All(handler.Bodies, AssertResponsesBody);
        var continuation = handler.Bodies[1].GetProperty("input").EnumerateArray().ToList();
        Assert.Contains(continuation, item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call");
        Assert.Contains(continuation, item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            && item.GetProperty("call_id").GetString() == "call_1" && item.GetProperty("output").GetString() == "echo:checked");
        Assert.Equal(2048, agent.Options.MaxTokens);
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        var ends = agent.Session.Events.Where(e => e.Type == SessionEventTypes.TurnEnd).ToList();
        Assert.Equal(2, ends.Count);
        Assert.All(ends, end => Assert.Equal("completed", end.Data.GetProperty("reason").GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task ResponsesRejection_DoesNotFallBackToChatCompletions()
    {
        using var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"Responses is unavailable on this gateway"}}"""),
        });
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http);

        var failure = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in adapter.Stream(Options())) { }
        });

        Assert.Equal("https://gateway.test/v1/responses", Assert.Single(handler.Urls));
        Assert.Contains("Responses is unavailable", failure.Message);
    }

    private static void AssertResponsesBody(JsonElement body)
    {
        Assert.True(body.TryGetProperty("input", out _));
        Assert.False(body.TryGetProperty("messages", out _));
        Assert.Equal(2048, body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.TryGetProperty("max_tokens", out _));
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        Assert.All(body.GetProperty("tools").EnumerateArray(), tool => Assert.False(tool.TryGetProperty("function", out _)));
    }

    private static async Task AssertSuccessfulStream(OpenAiCompatibleAdapter adapter, GenerateOptions options)
    {
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in adapter.Stream(options)) chunks.Add(chunk);
        Assert.Contains(chunks, c => c is TextDeltaChunk { Text: "hello" });
        Assert.Equal(FinishReason.Stop, Assert.IsType<FinishChunk>(chunks.Last()).Reason);
    }

    private static HttpResponseMessage TextResponse() => Sse("""
        data: {"type":"response.output_text.delta","delta":"hello"}

        data: {"type":"response.completed","response":{"status":"completed"}}
        """);

    private static HttpResponseMessage ToolResponse() => Sse("""
        data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"probe","arguments":""}}

        data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":0,"delta":"{\"value\":\"checked\"}"}

        data: {"type":"response.completed","response":{"status":"completed"}}
        """);

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
    };

    private sealed class RecordingHandler(Func<JsonElement, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];
        public List<string?> Urls { get; } = [];
        public List<string?> Authorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = doc.RootElement.Clone();
            Bodies.Add(body);
            Urls.Add(request.RequestUri?.ToString());
            Authorization.Add(request.Headers.Authorization?.ToString());
            return respond(body, Bodies.Count - 1);
        }
    }
}
