using System.Net;
using System.Text;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;

namespace Blazorly.Harness.Tests;

public sealed class ResponseIntegrityTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("responses")]
    [InlineData("anthropic")]
    public async Task PartialTextWithoutCompletion_FailsInsteadOfReportingSuccess(string protocol)
    {
        var payload = protocol switch
        {
            "openai" => """data: {"choices":[{"delta":{"content":"partial text"},"finish_reason":null}]}""",
            "responses" => """data: {"type":"response.output_text.delta","output_index":0,"delta":"partial text"}""",
            _ => """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"partial text"}}""",
        };
        using var http = new HttpClient(new SseHandler(payload));
        var adapter = Adapter(protocol, http);
        var chunks = new List<StreamChunk>();
        var error = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var chunk in adapter.Stream(Options(), default)) chunks.Add(chunk);
        });
        Assert.Equal(LlmErrorCodes.StreamClosed, error.Failure.Code);
        Assert.DoesNotContain(chunks, c => c is FinishChunk { Reason: FinishReason.Stop });
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("responses")]
    [InlineData("anthropic")]
    public async Task PartialToolCallWithoutCompletion_IsNeverExecuted(string protocol)
    {
        var payload = protocol switch
        {
            "openai" => """
                data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"probe","arguments":"{\"value\":\"should-not-run\"}"}}]},"finish_reason":null}]}
                """,
            "responses" => """
                data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call-1","name":"probe","arguments":""}}

                data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"value\":\"should-not-run\"}"}
                """,
            _ => """
                data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call-1","name":"probe","input":{}}}

                data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"value\":\"should-not-run\"}"}}
                """,
        };
        using var http = new HttpClient(new SseHandler(payload));
        await using var harness = TestHarness.Create();
        harness.Llm.RegisterAdapter(Adapter(protocol, http));
        var probe = new ProbeTool();
        harness.Tools.Register(probe);
        var agent = harness.CreateAgent(options: new AgentOptions("integrity", "explicit-model"));
        agent.RetryLimit = 0;
        agent.Followup(Message.CreateUserText("test interrupted response"));
        await agent.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var reason = SessionEventRead.TurnEndReasonOf(agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd));
        Assert.Equal(LlmErrorCodes.StreamClosed, Assert.IsType<TurnEndReason.Error>(reason).Code);
        Assert.Empty(probe.CallLog);
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.ToolCall);
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.AssistantMessage
            && SessionEventRead.AssistantMessageOf(e).Interrupted != true);
    }

    [Fact]
    public async Task Responses_NonterminalStatus_IsNotTreatedAsACompletedResponse()
    {
        using var http = new HttpClient(new SseHandler("""
            data: {"status":"in_progress","output":[{"type":"message","content":[{"type":"output_text","text":"unfinished"}]}]}
            """));
        var error = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in Adapter("responses", http).Stream(Options(), default)) { }
        });
        Assert.Equal(LlmErrorCodes.StreamClosed, error.Failure.Code);
    }

    [Fact]
    public async Task ChatUsageAfterFinishedChoice_IsRetained()
    {
        using var http = new HttpClient(new SseHandler("""
            data: {"choices":[{"delta":{"content":"answer"},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":10,"prompt_tokens_details":{"cached_tokens":20}}}

            data: [DONE]
            """));
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in Adapter("openai", http).Stream(Options(), default)) chunks.Add(chunk);
        var usage = Assert.Single(chunks.OfType<UsageChunk>()).Usage;
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(20, usage.CacheReadTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.Equal(FinishReason.Stop, Assert.Single(chunks.OfType<FinishChunk>()).Reason);
    }

    private static GenerateOptions Options() => new()
    {
        Provider = "integrity", Model = "explicit-model", Messages = [Message.CreateUserText("hello")],
    };

    private static LlmAdapter Adapter(string protocol, HttpClient http) => protocol switch
    {
        "openai" => new OpenAiCompatibleAdapter("integrity", "http://unused.test/v1", "test-key", [], http),
        "responses" => new ResponsesApiAdapter("integrity", "http://unused.test/v1", "test-key", [], http),
        _ => new AnthropicAdapter("integrity", "http://unused.test", "test-key", [], http),
    };

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body + "\n\n", Encoding.UTF8, "text/event-stream"),
            });
    }
}
