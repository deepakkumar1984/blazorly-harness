using System.Net;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Retry;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;

namespace Blazorly.Harness.Tests;

public class OpenAiReasoningFallbackTests
{
    // Helpers below build minimal wire responses for both protocols.
    private const string UnsupportedEffort = "Unsupported parameter: 'reasoning_effort'. In the Responses API, this parameter has moved to 'reasoning.effort'.";

    private static GenerateOptions Options(string model) => new()
    {
        Provider = "custom",
        Model = model,
        Messages = [Message.CreateUserText("hello")],
        MaxTokens = 2048,
        ReasoningEffort = "max",
    };

    [Theory]
    [InlineData("Unsupported parameter: 'reasoning_effort'. In the Responses API, this parameter has moved to 'reasoning.effort'.", true)]
    [InlineData("reasoning effort 'max' is not supported by this model", true)]
    [InlineData("Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.", false)]
    [InlineData("max_tokens 128000 exceeds the per-request limit for reasoning models", false)]
    public void TryDropReasoningEffort_OnlyBlamesEffort(string message, bool expected)
    {
        var failure = new LlmFailure(message, LlmErrorCodes.InvalidRequest);
        Assert.Equal(expected, RetryService.TryDropReasoningEffort(failure, "max"));
    }

    [Theory]
    [InlineData("az/gpt-6.1-sol", false)]
    [InlineData("gpt-6.1-sol", true)]
    public async Task Agent_DropsRejectedEffortAndRetriesWithoutIt(string model, bool responses)
    {
        using var handler = new RecordingHandler((body, _) => HasEffort(body) ? Error() : Success(body));
        using var http = new HttpClient(handler);
        await using var harness = TestHarness.Create();
        harness.Llm.RegisterAdapter(new OpenAiCompatibleAdapter("custom", "https://gateway.test/v1", "key", [], http));
        harness.Loop.DefaultSelection = Options(model).ToCallConfig();
        RetryService.Mount(harness.Ctx);
        var agent = harness.CreateAgent(options: new AgentOptions("custom", model, 2048, "max"));

        agent.Followup(Message.CreateUserText("hello"));
        await agent.WhenIdleAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(HasEffort(handler.Bodies[0]));
        Assert.False(HasEffort(handler.Bodies[1]));
        Assert.Null(agent.Options.ReasoningEffort);
        var expectedUrl = responses ? "https://gateway.test/v1/responses" : "https://gateway.test/v1/chat/completions";
        Assert.All(handler.Urls, url => Assert.Equal(expectedUrl, url));
        var retry = Assert.Single(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        Assert.Equal("max", retry.Data.GetProperty("reasoningEffortFrom").GetString());
        var end = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd);
        Assert.Equal("completed", end.Data.GetProperty("reason").GetProperty("kind").GetString());
    }

    private static bool HasEffort(JsonElement body)
        => body.TryGetProperty("reasoning", out _)
            || body.TryGetProperty("reasoning_effort", out _)
            || body.TryGetProperty("thinking", out _);

    private static HttpResponseMessage Error()
    {
        var json = JsonSerializer.Serialize(new
        {
            error = new { message = UnsupportedEffort, type = "invalid_request_error", code = "unsupported_parameter" },
        });
        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(json) };
    }

    private static HttpResponseMessage Success(JsonElement body) => body.TryGetProperty("input", out _)
        ? Sse("""
            data: {"type":"response.output_text.delta","delta":"hello"}

            data: {"type":"response.completed","response":{"status":"completed"}}
            """)
        : Sse("""data: {"choices":[{"delta":{"content":"hello"},"finish_reason":"stop"}]}""");

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private sealed class RecordingHandler(Func<JsonElement, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];
        public List<string?> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = doc.RootElement.Clone();
            Bodies.Add(body);
            Urls.Add(request.RequestUri?.ToString());
            return respond(body, Bodies.Count - 1);
        }
    }
}
