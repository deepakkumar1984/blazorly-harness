using System.Net;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Retry;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;

namespace Blazorly.Harness.Tests;

public class OpenAiCompatibleTokenLimitTests
{
    private const string UnsupportedMaxTokens = "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.";
    private const string UnsupportedCompletionTokens = "Unsupported parameter: 'max_completion_tokens' is not supported with this model. Use 'max_tokens' instead.";

    private static GenerateOptions Options() => new()
    {
        Provider = "custom",
        Model = "reasoning-alias",
        System = "Be brief.",
        Messages = [Message.CreateUserText("hello")],
        Tools = [ToolSchemaJson.FromJson("probe", "Test tool", """{"type":"object"}""")],
        MaxTokens = 2048,
        ReasoningEffort = "high",
        Temperature = 0.5,
        Stop = ["END"],
    };

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("sse")]
    public async Task Stream_SwitchesParameterPreservingBudgetAndRemembersIt(string errorFormat)
    {
        using var handler = new RecordingHandler((_, call) => call == 0
            ? Error(UnsupportedMaxTokens, format: errorFormat)
            : Success());
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http);
        var options = Options();

        await AssertSuccessfulStream(adapter, options);
        await AssertSuccessfulStream(adapter, options);

        Assert.Equal(3, handler.Bodies.Count);
        AssertTokenLimit(handler.Bodies[0], "max_tokens", 2048);
        AssertTokenLimit(handler.Bodies[1], "max_completion_tokens", 2048);
        AssertTokenLimit(handler.Bodies[2], "max_completion_tokens", 2048);
        Assert.Equal(2048, options.MaxTokens);
        Assert.Equal(handler.Bodies[0].EnumerateObject().Count(), handler.Bodies[1].EnumerateObject().Count());
        foreach (var property in handler.Bodies[0].EnumerateObject().Where(p => p.Name != "max_tokens"))
            Assert.True(JsonElement.DeepEquals(property.Value, handler.Bodies[1].GetProperty(property.Name)), property.Name);
        Assert.All(handler.Urls, url => Assert.Equal("https://gateway.test/v1/chat/completions", url));
        Assert.All(handler.Authorization, auth => Assert.Equal("Bearer key", auth));
    }

    [Fact]
    public async Task Agent_RecoversWithoutReducingItsTokenBudget()
    {
        using var handler = new RecordingHandler((body, call) => body.TryGetProperty("max_tokens", out _)
            ? Error(UnsupportedMaxTokens)
            : Success());
        using var http = new HttpClient(handler);
        await using var harness = TestHarness.Create();
        harness.Llm.RegisterAdapter(new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http));
        harness.Loop.DefaultSelection = Options().ToCallConfig();
        RetryService.Mount(harness.Ctx);
        var agent = harness.CreateAgent(options: new AgentOptions("custom", "reasoning-alias", 2048, "high"));

        agent.Followup(Message.CreateUserText("hello"));
        await agent.WhenIdleAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(2048, agent.Options.MaxTokens);
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.AssistantMessage);
        var end = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd);
        Assert.Equal("completed", end.Data.GetProperty("reason").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task LearnedParameter_IsScopedToTheRouteAndModel()
    {
        using var handler = new RecordingHandler((_, call) => call == 0 ? Error(UnsupportedMaxTokens) : Success());
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http);
        await AssertSuccessfulStream(adapter, Options());

        await AssertSuccessfulStream(adapter, Options() with { Model = "legacy-model" });
        var otherRoute = new OpenAiCompatibleAdapter("other", "https://other.test/v1", "key", [], http);
        await AssertSuccessfulStream(otherRoute, Options() with { Provider = "other" });

        Assert.Equal(4, handler.Bodies.Count);
        AssertTokenLimit(handler.Bodies[2], "max_tokens", 2048);
        AssertTokenLimit(handler.Bodies[3], "max_tokens", 2048);
    }

    [Fact]
    public async Task ChangedRoute_CanSwitchBackToMaxTokens()
    {
        using var handler = new RecordingHandler((_, call) => call switch
        {
            0 => Error(UnsupportedMaxTokens),
            2 => Error(UnsupportedCompletionTokens),
            _ => Success(),
        });
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http);

        await AssertSuccessfulStream(adapter, Options());
        await AssertSuccessfulStream(adapter, Options());
        await AssertSuccessfulStream(adapter, Options());

        Assert.Equal(5, handler.Bodies.Count);
        AssertTokenLimit(handler.Bodies[2], "max_completion_tokens", 2048);
        AssertTokenLimit(handler.Bodies[3], "max_tokens", 2048);
        AssertTokenLimit(handler.Bodies[4], "max_tokens", 2048);
    }

    [Fact]
    public async Task UnsetBudget_OmitsBothParametersEvenAfterNegotiation()
    {
        using var handler = new RecordingHandler((_, call) => call == 0 ? Error(UnsupportedMaxTokens) : Success());
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http);
        await AssertSuccessfulStream(adapter, Options());
        await AssertSuccessfulStream(adapter, Options() with { MaxTokens = null });

        Assert.Equal(3, handler.Bodies.Count);
        Assert.False(handler.Bodies[2].TryGetProperty("max_tokens", out _));
        Assert.False(handler.Bodies[2].TryGetProperty("max_completion_tokens", out _));
    }

    [Theory]
    [InlineData(400, "max_tokens is too large. max_completion_tokens has the same limit.", 2048)]
    [InlineData(400, "Unsupported parameter: 'temperature'. Supported fields include max_tokens and max_completion_tokens.", 2048)]
    [InlineData(400, "Unsupported parameter: 'max_tokens'.", 2048)]
    [InlineData(401, UnsupportedMaxTokens, 2048)]
    [InlineData(429, UnsupportedMaxTokens, 2048)]
    [InlineData(500, UnsupportedMaxTokens, 2048)]
    [InlineData(400, UnsupportedMaxTokens, null)]
    public async Task OtherFailures_AreNotRetriedInsideTheAdapter(int status, string message, int? maxTokens)
    {
        using var handler = new RecordingHandler((_, _) => Error(message, status));
        using var http = new HttpClient(handler);
        var adapter = new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http);

        var failure = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in adapter.Stream(Options() with { MaxTokens = maxTokens })) { }
        });

        Assert.Single(handler.Bodies);
        Assert.Equal(status, failure.Failure.Status);
        Assert.Contains(message, failure.Message);
    }

    [Fact]
    public async Task BothParametersRejected_StopsAfterOneFallbackAndKeepsBudget()
    {
        using var handler = new RecordingHandler((_, call) => Error(call % 2 == 0
            ? UnsupportedMaxTokens
            : UnsupportedCompletionTokens));
        using var http = new HttpClient(handler);
        await using var harness = TestHarness.Create();
        harness.Llm.RegisterAdapter(new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http));
        harness.Loop.DefaultSelection = Options().ToCallConfig();
        RetryService.Mount(harness.Ctx);
        var agent = harness.CreateAgent(options: new AgentOptions("custom", "reasoning-alias", 2048));

        agent.Followup(Message.CreateUserText("hello"));
        await agent.WhenIdleAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(2048, agent.Options.MaxTokens);
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        var reason = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd).Data.GetProperty("reason");
        Assert.Equal(LlmErrorCodes.InvalidRequest, reason.GetProperty("code").GetString());
        Assert.Contains(UnsupportedCompletionTokens, reason.GetProperty("message").GetString());
        Assert.DoesNotContain("Lower Max output", reason.GetProperty("message").GetString());
    }

    private static void AssertTokenLimit(JsonElement body, string parameter, int value)
    {
        Assert.Equal(value, body.GetProperty(parameter).GetInt32());
        Assert.False(body.TryGetProperty(parameter == "max_tokens" ? "max_completion_tokens" : "max_tokens", out _));
        Assert.False(body.TryGetProperty("max_output_tokens", out _));
    }

    private static async Task AssertSuccessfulStream(OpenAiCompatibleAdapter adapter, GenerateOptions options)
    {
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in adapter.Stream(options)) chunks.Add(chunk);
        Assert.Contains(chunks, c => c is TextDeltaChunk { Text: "hello" });
        Assert.Equal(FinishReason.Stop, Assert.IsType<FinishChunk>(chunks.Last()).Reason);
    }

    private static HttpResponseMessage Error(string message, int status = 400, string format = "json")
    {
        var json = JsonSerializer.Serialize(new { error = new { message, type = "invalid_request_error", code = "unsupported_parameter" } });
        var body = format switch { "text" => message, "sse" => $"data: {json}\n\n", _ => json };
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
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
