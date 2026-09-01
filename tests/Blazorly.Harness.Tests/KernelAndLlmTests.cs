using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using System.Net;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

public class EventBusTests
{
    [Fact]
    public async Task Emit_ContainsListenerFailures()
    {
        var ctx = HarnessContext.CreateRoot();
        var errors = new List<string>();
        ctx.Events.OnListenerError = (_, ex) => errors.Add(ex.Message);
        var seen = new List<int>();
        ctx.Events.On<int>("x", (v, _) => { seen.Add(v); throw new InvalidOperationException("boom"); });
        await ctx.Events.EmitAsync("x", 42);
        Assert.Equal([42], seen);
        Assert.Equal(["boom"], errors);
    }

    [Fact]
    public async Task Waterfall_FirstRegisteredIsOutermost_AndValueFlowsDown()
    {
        var ctx = HarnessContext.CreateRoot();
        var order = new List<string>();
        ctx.Events.OnWaterfall<string, string, string>("w", (payload, value, next, _) =>
        {
            order.Add("outer:" + value);
            return next(value + ">o");
        });
        ctx.Events.OnWaterfall<string, string, string>("w", (payload, value, next, _) =>
        {
            order.Add("inner:" + value);
            return next(value + ">i");
        });
        var result = await ctx.Events.WaterfallAsync<string, string, string>("w", "p", "start", v => Task.FromResult(v + ">terminal"));
        Assert.Equal("start>o>i>terminal", result);
        Assert.Equal(["outer:start", "inner:start>o"], order);
    }

    [Fact]
    public async Task Waterfall_ShortCircuitSkipsDownstream()
    {
        var ctx = HarnessContext.CreateRoot();
        var terminalRan = false;
        ctx.Events.OnWaterfall<object?, string, string>("w", (_, _, _, _) => Task.FromResult("short"));
        var result = await ctx.Events.WaterfallAsync<object?, string, string>("w", null, "v", _ =>
        {
            terminalRan = true;
            return Task.FromResult("terminal");
        });
        Assert.Equal("short", result);
        Assert.False(terminalRan);
    }

    [Fact]
    public async Task ScopeAdmission_FlowsUpNeverDown()
    {
        var ctx = HarnessContext.CreateRoot();
        var parentKey = new object();
        var childKey = new object();
        await using var parentScope = ctx.CreateScope(parentKey);
        await using var childScope = ctx.CreateScope(childKey, parentKey);

        var globalSaw = new List<string>();
        var parentSaw = new List<string>();
        var childSaw = new List<string>();
        ctx.Events.On<string>("e", (v, _) => { globalSaw.Add(v); return Task.CompletedTask; });
        ctx.Events.On<string>("e", (v, _) => { parentSaw.Add(v); return Task.CompletedTask; }, scopeKey: parentKey);
        ctx.Events.On<string>("e", (v, _) => { childSaw.Add(v); return Task.CompletedTask; }, scopeKey: childKey);

        await ctx.Events.EmitAsync("e", "child-subject", subjectKey: childKey);
        Assert.Equal(["child-subject"], globalSaw);
        Assert.Equal(["child-subject"], parentSaw); // parent hears about its descendant
        Assert.Equal(["child-subject"], childSaw);

        globalSaw.Clear(); parentSaw.Clear(); childSaw.Clear();
        await ctx.Events.EmitAsync("e", "parent-subject", subjectKey: parentKey);
        Assert.Equal(["parent-subject"], globalSaw);
        Assert.Equal(["parent-subject"], parentSaw);
        Assert.Empty(childSaw); // the child never hears about the parent
    }

    [Fact]
    public async Task Serial_AwaitsInRegistrationOrder()
    {
        var ctx = HarnessContext.CreateRoot();
        var order = new List<string>();
        ctx.Events.On<int>("s", async (v, _) => { await Task.Yield(); order.Add($"first:{v}"); });
        ctx.Events.On<int>("s", (v, _) => { order.Add($"second:{v}"); return Task.CompletedTask; });
        await ctx.Events.SerialAsync("s", 7);
        Assert.Equal(["first:7", "second:7"], order);
    }

    [Fact]
    public async Task ContextEffects_UnwindLifo()
    {
        var ctx = HarnessContext.CreateRoot();
        var unwind = new List<string>();
        ctx.Effect(() => unwind.Add("first"));
        var disposer = ctx.Effect(() => unwind.Add("second"));
        disposer.Dispose();
        await ctx.DisposeAsync();
        Assert.Equal(["second", "first"], unwind);
    }
}

public class BlockAssemblerTests
{
    [Fact]
    public void FoldsDeltasIntoBlocks()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new BlockStartChunk(0, "text"));
        assembler.Push(new TextDeltaChunk(0, "Hello "));
        assembler.Push(new TextDeltaChunk(0, "world"));
        assembler.Push(new BlockStartChunk(1, "tool-call"));
        assembler.Push(new ToolCallDeltaChunk(1, "call_1", "bash", "{\"command\":\"ls\"}"));
        assembler.Push(new UsageChunk(new TokenUsage(10, 5)));
        assembler.Push(new FinishChunk(FinishReason.ToolCalls));

        var blocks = assembler.Blocks();
        var text = Assert.IsType<TextBlock>(blocks[0]);
        Assert.Equal("Hello world", text.Text);
        var call = Assert.IsType<ToolCallBlock>(blocks[1]);
        Assert.Equal("bash", call.Name);
        Assert.Equal("{\"command\":\"ls\"}", call.Arguments); // raw JSON preserved end to end
        Assert.Equal(FinishReason.ToolCalls, assembler.Finish!.Reason);
        Assert.Equal(10, assembler.Usage!.InputTokens);
    }

    [Fact]
    public void BlockEndIsAuthoritative()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new TextDeltaChunk(0, "stale"));
        assembler.Push(new BlockEndChunk(0, new TextBlock("final")));
        assembler.Push(new TextDeltaChunk(0, "ignored after close"));
        var text = Assert.IsType<TextBlock>(assembler.Blocks().Single());
        Assert.Equal("final", text.Text);
    }

    [Fact]
    public void MaxTokensDropsToolCalls()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new ToolCallDeltaChunk(0, "call_1", "bash", "{}"));
        assembler.Push(new TextDeltaChunk(1, "partial"));
        assembler.Push(new FinishChunk(FinishReason.MaxTokens));
        var blocks = assembler.Blocks();
        Assert.Single(blocks);
        Assert.IsType<TextBlock>(blocks[0]);
    }
}

public class OpenAiAdapterWireTests
{
    private static GenerateOptions Options(string? system = null) => new()
    {
        Provider = "test",
        Model = "m1",
        System = system,
        Messages =
        [
            Message.CreateUserText("hello"),
            Message.CreateAssistant("test", "m1",
            [
                new TextBlock("thinking..."),
                new ToolCallBlock("call_1", "bash", "{\"command\":\"ls\"}"),
            ]),
            Message.CreateToolResult("call_1", [new TextBlock("file-a\nfile-b")]),
        ],
    };

    [Fact]
    public void MapsHarnessMessagesToWireFormat()
    {
        var adapter = new OpenAiCompatibleAdapter("test", "http://localhost", "k", [], new HttpClient());
        var body = (System.Collections.Generic.Dictionary<string, object?>)adapter.BuildWireBody(Options("be brief"));
        var messages = (List<object>)body["messages"]!;
        Assert.Equal(4, messages.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.Contains("\"role\":\"system\"", json);
        Assert.Contains("\"role\":\"tool\"", json);
        Assert.Contains("\"tool_call_id\":\"call_1\"", json);
        Assert.Contains("\"function\":{\"name\":\"bash\"", json);
        Assert.Contains("\"reasoning_content\"", json); // assistant reasoning replayed
    }

    [Fact]
    public void ClassifiesHttpErrors()
    {
        Assert.Equal(LlmErrorCodes.Auth, OpenAiCompatibleAdapter.ClassifyHttp(401, "").Code);
        Assert.Equal(LlmErrorCodes.RateLimit, OpenAiCompatibleAdapter.ClassifyHttp(429, "").Code);
        Assert.Equal(LlmErrorCodes.Server, OpenAiCompatibleAdapter.ClassifyHttp(502, "").Code);
        Assert.Equal(LlmErrorCodes.ContextWindowExceeded,
            OpenAiCompatibleAdapter.ClassifyHttp(400, "This model's maximum context length is 65536 tokens").Code);
    }

    [Fact]
    public async Task ParsesSseStreamIntoChunks()
    {
        var sse = string.Join("\n",
        [
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"let me think\"}}]}",
            "",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}",
            "",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"bash\",\"arguments\":\"{\\\"command\\\":\"}}]}}]}",
            "",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"ls\\\"}\"}}]}}]}",
            "",
            "data: {\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":20,\"completion_tokens\":7}}",
            "",
            "data: [DONE]",
            "",
        ]);
        var http = new StaticHttpHandler(sse);
        var adapter = new OpenAiCompatibleAdapter("test", "http://localhost", "key", [], new HttpClient(http));
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in adapter.Stream(Options())) chunks.Add(chunk);

        Assert.Contains(chunks, c => c is ReasoningDeltaChunk r && r.Text == "let me think");
        Assert.Contains(chunks, c => c is TextDeltaChunk t && t.Text == "Hello");
        var assembled = new BlockAssembler();
        foreach (var chunk in chunks) assembled.Push(chunk);
        var blocks = assembled.Blocks();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("{\"command\":\"ls\"}", Assert.IsType<ToolCallBlock>(blocks[2]).Arguments);
        Assert.Equal(FinishReason.ToolCalls, assembled.Finish!.Reason);
        Assert.Equal(80, assembled.Usage!.InputTokens); // 100 prompt - 20 cached
        Assert.Equal(7, assembled.Usage!.OutputTokens);
    }

    private sealed class StaticHttpHandler(string body) : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(body, System.Text.Encoding.UTF8, "text/event-stream");
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
