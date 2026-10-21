using System.Net;
using System.Text;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>Adapters tolerate the version/call suffix pasted into the base URL (portal consoles
/// hand out full URLs): strip-then-append, so strict gateways never see a doubled segment.</summary>
public class EndpointUrlTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static GenerateOptions UserTurn(string provider, string model) => new()
    {
        Provider = provider,
        Model = model,
        Messages = [new Llm.Message("m1", "user", [new TextBlock("hi")], Llm.MessageSource.User())],
    };

    [Theory]
    // Root, version suffix, full call URL, trailing slash — all collapse to one path.
    [InlineData("https://gw.example.com/anthropic", "https://gw.example.com/anthropic/v1/messages")]
    [InlineData("https://gw.example.com/anthropic/v1", "https://gw.example.com/anthropic/v1/messages")]
    [InlineData("https://gw.example.com/anthropic/v1/", "https://gw.example.com/anthropic/v1/messages")]
    [InlineData("https://gw.example.com/anthropic/v1/messages", "https://gw.example.com/anthropic/v1/messages")]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    public async Task Anthropic_PostsToSingleMessagesPath(string baseUrl, string expected)
    {
        HttpRequestMessage? seen = null;
        var adapter = new AnthropicAdapter("AZ", baseUrl, "k", [], new HttpClient(new StubHandler(request =>
        {
            seen = request;
            return Sse("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        })));
        try { await foreach (var _ in adapter.Stream(UserTurn("AZ", "m"))) { } }
        catch (LlmException) { /* response shape is irrelevant; the URL was captured at send */ }
        Assert.Equal(expected, seen!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://gw.example.com/v1/chat/completions", "https://gw.example.com/v1/chat/completions")]
    public async Task OpenAi_PostsToSingleChatCompletionsPath(string baseUrl, string expected)
    {
        HttpRequestMessage? seen = null;
        var adapter = new OpenAiCompatibleAdapter("gw", baseUrl, "k", [], new HttpClient(new StubHandler(request =>
        {
            seen = request;
            return Sse("");
        })));
        try { await foreach (var _ in adapter.Stream(UserTurn("gw", "m"))) { } }
        catch (LlmException) { /* response shape is irrelevant; the URL was captured at send */ }
        Assert.Equal(expected, seen!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/responses")]
    [InlineData("https://gw.example.com/v1/responses", "https://gw.example.com/v1/responses")]
    [InlineData("https://gw.example.com/v1/chat/completions", "https://gw.example.com/v1/responses")]
    public async Task Responses_PostsToSingleResponsesPath(string baseUrl, string expected)
    {
        HttpRequestMessage? seen = null;
        var adapter = new ResponsesApiAdapter("gw", baseUrl, "k", [], new HttpClient(new StubHandler(request =>
        {
            seen = request;
            return Sse("");
        })));
        try { await foreach (var _ in adapter.Stream(UserTurn("gw", "m"))) { } }
        catch (LlmException) { /* response shape is irrelevant; the URL was captured at send */ }
        Assert.Equal(expected, seen!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("https://gw.example.com/anthropic", "https://gw.example.com/anthropic/v1/models")]
    [InlineData("https://gw.example.com/anthropic/v1", "https://gw.example.com/anthropic/v1/models")]
    public async Task Discover_AnthropicGateway_StripsVersionSuffix(string baseUrl, string expected)
    {
        HttpRequestMessage? seen = null;
        var http = new HttpClient(new StubHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"a"}]}""", Encoding.UTF8, "application/json"),
            };
        }));
        var models = await LlmModelDiscovery.DiscoverAsync("AZ", baseUrl, "k", http, null, CancellationToken.None, anthropicModelsPath: true);
        Assert.Equal(expected, seen!.RequestUri!.ToString());
        Assert.Equal(["a"], [.. models.Select(m => m.Id)]);
    }

    [Fact]
    public void Hint_404_PointsAtEndpointNeverParameters()
    {
        var text = AgentDriver.WithInvalidRequestHint(
            "provider rejected request (404: Requested API is currently not supported)",
            new AgentOptions("p", "m", 4000, "max"));
        Assert.Contains("base URL", text);
        Assert.DoesNotContain("max_tokens 4000", text);
        Assert.DoesNotContain("reasoning effort", text);
    }
}
