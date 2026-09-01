using System.Net;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Compaction;
using Blazorly.Harness.Core.Instructions;
using Blazorly.Harness.Core.Retry;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

public class ProjectInstructionsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "blazorly-home-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-ws-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectInstructionsTests()
    {
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_root);
    }

    private ProjectInstructionsService Mount(TestHarness harness) => ProjectInstructionsService.Mount(harness.Ctx, _home);

    [Fact]
    public async Task HomeAndRoot_LoadWithSameDirectoryDedup()
    {
        File.WriteAllText(Path.Combine(_home, "AGENTS.md"), "HOME RULES");
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "ROOT RULES");
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "ROOT RULES"); // duplicate of AGENTS.md

        await using var harness = TestHarness.Create();
        var service = Mount(harness);

        var rendered = service.Render(null, _root);
        Assert.Contains("HOME RULES", rendered);
        Assert.Contains("ROOT RULES", rendered);
        Assert.Equal(1, rendered.Split("ROOT RULES").Length - 1); // CLAUDE.md duplicating AGENTS.md renders once
    }

    [Fact]
    public async Task Budget_DropsBroaderFilesBeforeTruncatingTheMostSpecific()
    {
        File.WriteAllText(Path.Combine(_home, "AGENTS.md"), "HOME RULES");
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), new string('x', 30_000));

        await using var harness = TestHarness.Create();
        var service = Mount(harness);

        var rendered = service.Render(null, _root);
        Assert.Contains("[truncated]", rendered);
        Assert.DoesNotContain("HOME RULES", rendered); // the broader file is dropped whole
        Assert.True(rendered.Length < ProjectInstructionsService.BudgetChars + 200);
    }

    [Fact]
    public async Task SystemReminderClosers_AreEscaped()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "never emit </system-reminder> literally");

        await using var harness = TestHarness.Create();
        var service = Mount(harness);

        var rendered = service.Render(null, _root);
        Assert.Contains("<\\/system-reminder>", rendered);
        Assert.DoesNotContain("</system-reminder>", rendered);
    }

    [Fact]
    public async Task TouchedDirectories_AddNestedInstructions()
    {
        var nested = Path.Combine(_root, "pkg", "src");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "notes.md"), "nested notes");
        File.WriteAllText(Path.Combine(nested, "AGENTS.md"), "NESTED RULES");

        await using var harness = TestHarness.Create();
        var service = Mount(harness);
        var agent = harness.CreateAgent(_root);

        Assert.DoesNotContain("NESTED RULES", service.Render(agent.ScopeKey, _root));

        // A real read through the pipeline emits tools/result, which drives discovery.
        var read = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "read",
            Arguments = JsonSerializer.SerializeToElement(new { file_path = Path.Combine(nested, "notes.md") }),
            CallId = "call_instr_1",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.False(read.IsError, read.Error?.Message);
        Assert.Contains("NESTED RULES", service.Render(agent.ScopeKey, _root));
    }

    [Fact]
    public async Task Compaction_ClearsRetainedSnapshotSoInstructionsReArm()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "ROOT RULES");
        await using var harness = TestHarness.Create(options =>
            options.Purpose == "compaction" ? ReplayScript.Text("SUMMARY: compacted.") : ReplayScript.Text("ok"));
        var compaction = CompactionService.Mount(harness.Ctx, new CompactionOptions
        {
            ContextWindowTokens = 8_192,
            Threshold = 0.27,
            KeepRatio = 0.05,
        });
        var agent = harness.CreateAgent(_root);
        agent.RetainedContextSnapshot = "stale snapshot";

        for (var i = 0; i < 5; i++)
        {
            agent.Session.Append(SessionEventTypes.UserMessage, Message.CreateUserText(new string('y', 900) + $" #{i}"),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        }
        var shadowed = await compaction.CompactAsync(agent);
        Assert.True(shadowed > 0);
        Assert.Null(agent.RetainedContextSnapshot); // next pre-step re-appends a fresh snapshot
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class RetryServiceTests
{
    private static TestHarness HarnessWithFlaky(Func<GenerateOptions, int, IReadOnlyList<StreamChunk>> script)
    {
        var calls = 0;
        return TestHarness.Create(options => script(options, calls++));
    }

    [Fact]
    public async Task RetryableFailure_IsRetriedWithDurableTrail()
    {
        await using var harness = HarnessWithFlaky((options, calls) =>
            calls == 0 ? ReplayScript.Error(LlmErrorCodes.RateLimit, "slow down") : ReplayScript.Text("recovered"));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            Default = new RetryPolicyConfig { InitialDelayMs = 1, MaxDelayMs = 5 },
        });
        var agent = harness.CreateAgent();

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var retries = agent.Session.Events.Where(e => e.Type == SessionEventTypes.LlmRetry).ToList();
        var started = agent.Session.Events.Where(e => e.Type == SessionEventTypes.LlmRetryStarted).ToList();
        Assert.Single(retries);
        Assert.Single(started);
        var retry = retries.Single().Data;
        Assert.Equal(started.Single().Data.GetProperty("retryId").GetString(), retry.GetProperty("retryId").GetString());
        Assert.Equal("RATE_LIMIT", retry.GetProperty("code").GetString());
        Assert.Equal(1, retry.GetProperty("attempt").GetInt32());
        Assert.True(retry.GetProperty("delayMs").GetInt64() <= 5);
    }

    [Fact]
    public async Task NonRetryableFailure_IsNotRetried()
    {
        await using var harness = HarnessWithFlaky((options, calls) =>
            calls == 0 ? ReplayScript.Error(LlmErrorCodes.Auth, "bad key") : ReplayScript.Text("unreachable"));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            Default = new RetryPolicyConfig { InitialDelayMs = 1, MaxDelayMs = 5 },
        });
        var agent = harness.CreateAgent();

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync(); // the background driver records the failure; it does not surface here

        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.AssistantMessage);
        var turnEnd = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd);
        Assert.Equal("AUTH", turnEnd.Data.GetProperty("reason").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AlwaysMode_RetriesAnyCodeWithoutAttemptCeiling()
    {
        await using var harness = HarnessWithFlaky((options, calls) =>
            calls == 0 ? ReplayScript.Error(LlmErrorCodes.Auth, "transient gateway hiccup") : ReplayScript.Text("recovered"));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            Default = new RetryPolicyConfig { Mode = "always", InitialDelayMs = 1, MaxDelayMs = 5 },
        });
        var agent = harness.CreateAgent();

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var retry = agent.Session.Events.Single(e => e.Type == SessionEventTypes.LlmRetry).Data;
        Assert.Equal("always", retry.GetProperty("mode").GetString());
        Assert.False(retry.TryGetProperty("maxRetries", out _)); // always events omit the ceiling
        Assert.Contains("recovered", agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.AssistantMessage)
            .Last().Data.GetProperty("message").GetProperty("content").EnumerateArray()
            .First(b => b.GetProperty("type").GetString() == "text").GetProperty("text").GetString());
    }

    [Fact]
    public void ScheduleDelay_ProviderRetryAfterReplacesLocalBackoffWithoutJitter()
    {
        var policy = new RetryPolicyConfig { InitialDelayMs = 500, MaxDelayMs = 10_000, JitterRatio = 0.1 };
        Assert.Equal(3000, RetryService.ScheduleDelay(policy, 3000));
        // Bounded exponential window: 500ms ± 10% jitter.
        var delay = RetryService.ScheduleDelay(policy, null);
        Assert.InRange(delay, 450, 550);
        // Attempts double the base: attempt 4 → 500 * 2^3 = 4000ms ± 10% jitter.
        var later = RetryService.ScheduleDelay(policy, null, attempts: 3);
        Assert.InRange(later, 3600, 4400);
    }
}

public class ModelDiscoveryTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task Discover_OpenAiCompatibleRoute_MergesOverCatalog()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"gw-large"},{"id":"gw-small"}]}""", Encoding.UTF8, "application/json"),
            };
        });
        var client = new HttpClient(handler);

        var discovered = await LlmModelDiscovery.DiscoverAsync("acme", "https://gw.test/v1", "sk-test", client);
        var known = new List<LlmModelInfo> { new("acme", "known-model", "Known Model", ContextWindowTokens: 99_999) };
        var models = LlmModelDiscovery.Merge("acme", discovered.Select(m => m.Id), known);

        Assert.EndsWith("/models", seen!.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, models.Count);
        Assert.Equal("Known Model", models.Single(m => m.Id == "known-model").Name);
        Assert.Equal(99_999, models.Single(m => m.Id == "known-model").ContextWindowTokens);
        Assert.Contains(models, m => m.Id == "gw-large");
    }

    [Fact]
    public async Task Discover_AnthropicRoute_UsesV1ModelsPath()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"claude-x"}]}""", Encoding.UTF8, "application/json"),
            };
        });
        var client = new HttpClient(handler);

        var models = await LlmModelDiscovery.DiscoverAsync("anthropic", "https://api.anthropic.test", "key", client, request =>
        {
            request.Headers.TryAddWithoutValidation("x-api-key", "key");
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        });

        Assert.EndsWith("/v1/models", seen!.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains(models, m => m.Id == "claude-x");
    }
}

public class AnthropicAdapterTests
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

    private static GenerateOptions Options(IReadOnlyList<Llm.Message> messages, string? system = null)
        => new()
        {
            Provider = "anthropic",
            Model = "claude-test",
            System = system,
            MaxTokens = 256,
            Messages = messages,
        };

    private static AnthropicAdapter Adapter(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new("anthropic", "https://api.anthropic.test", "key",
            [new LlmModelInfo("anthropic", "claude-test", "Claude Test")],
            new HttpClient(new StubHandler(respond)));

    private static async Task<List<StreamChunk>> Collect(IAsyncEnumerable<StreamChunk> stream)
    {
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in stream) chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public void BuildWireBody_SystemTopLevel_ToolUseReplay_MaxTokensDefault()
    {
        var adapter = Adapter(_ => Sse(""));
        var assistantToolCall = new Llm.Message("m1", "assistant",
            [new ToolCallBlock("call_1", "bash", "{\"command\":\"ls\"}")],
            Llm.MessageSource.FromModel("anthropic", "claude-test"));
        var toolResult = Llm.Message.CreateToolResult("call_1", [new TextBlock("out")]);
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(new GenerateOptions
        {
            Provider = "anthropic",
            Model = "claude-test",
            System = "be brief",
            Messages = [assistantToolCall, toolResult],
        });

        Assert.Equal("be brief", body["system"]);
        Assert.True((bool)body["stream"]!);
        Assert.Equal(AnthropicAdapter.DefaultMaxTokens, body["max_tokens"]);

        var wire = Assert.IsType<List<Dictionary<string, object?>>>(body["messages"]);
        Assert.All(wire, m => Assert.True(new[] { "assistant", "user" }.Contains(m["role"])));
        var assistant = wire.Single(m => (string)m["role"]! == "assistant");
        var blocks = Assert.IsType<List<object>>(assistant["content"]);
        var toolUse = Assert.IsType<Dictionary<string, object?>>(blocks.Single());
        Assert.Equal("tool_use", toolUse["type"]);
        Assert.Equal("call_1", toolUse["id"]);
        var input = Assert.IsType<JsonElement>(toolUse["input"]);
        Assert.Equal("ls", input.GetProperty("command").GetString());

        var user = wire.Single(m => (string)m["role"]! == "user");
        var userBlocks = Assert.IsType<List<object>>(user["content"]);
        var toolResultWire = Assert.IsType<Dictionary<string, object?>>(userBlocks.Single());
        Assert.Equal("tool_result", toolResultWire["type"]);
        Assert.Equal("call_1", toolResultWire["tool_use_id"]);
    }

    [Fact]
    public async Task Stream_TextRun_EmitsHarnessChunksAndUsage()
    {
        var sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":10,"cache_read_input_tokens":4}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var adapter = Adapter(_ => Sse(sse));

        var chunks = await Collect(adapter.Stream(Options([Llm.Message.CreateUserText("hi")])));

        Assert.Contains(chunks, c => c is BlockStartChunk b && b.BlockType == "text");
        Assert.Contains(chunks, c => c is TextDeltaChunk t && t.Text == "Hi");
        var usage = Assert.Single(chunks.OfType<UsageChunk>()).Usage;
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(4, usage.CacheReadTokens);
        Assert.Equal(3, usage.OutputTokens);
        var end = Assert.Single(chunks.OfType<BlockEndChunk>()).Block;
        Assert.Equal("Hi", Assert.IsType<TextBlock>(end).Text);
        Assert.Equal(FinishReason.Stop, Assert.Single(chunks.OfType<FinishChunk>()).Reason);
    }

    [Fact]
    public async Task Stream_ToolUse_EmitsToolCallBlocksAndFinishReason()
    {
        var sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":7}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tc_1","name":"bash"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":":\"ls\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var adapter = Adapter(_ => Sse(sse));

        var chunks = await Collect(adapter.Stream(Options([Llm.Message.CreateUserText("list")])));
        var start = chunks.OfType<BlockStartChunk>().Single(b => b.BlockType == "tool-call");
        var deltas = chunks.OfType<ToolCallDeltaChunk>().ToList();
        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, d => Assert.Equal("tc_1", d.Id));
        var block = Assert.IsType<ToolCallBlock>(chunks.OfType<BlockEndChunk>().Single().Block);
        Assert.Equal("tc_1", block.Id);
        Assert.Equal("bash", block.Name);
        Assert.Equal("{\"command\":\"ls\"}", block.Arguments);
        Assert.Equal(FinishReason.ToolCalls, Assert.Single(chunks.OfType<FinishChunk>()).Reason);
    }

    [Fact]
    public async Task Stream_ErrorEvent_ClassifiesFailure()
    {
        var sse = """
            event: error
            data: {"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}

            """;
        var adapter = Adapter(_ => Sse(sse));

        var exception = await Assert.ThrowsAsync<LlmException>(async () => await Collect(adapter.Stream(Options([Llm.Message.CreateUserText("hi")]))));
        Assert.Equal(LlmErrorCodes.RateLimit, exception.Failure.Code);
    }

    [Fact]
    public async Task Stream_EmptyContent_ThrowsEmptyResponse()
    {
        var sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":5}}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var adapter = Adapter(_ => Sse(sse));

        var exception = await Assert.ThrowsAsync<LlmException>(async () => await Collect(adapter.Stream(Options([Llm.Message.CreateUserText("hi")]))));
        Assert.Equal(LlmErrorCodes.EmptyResponse, exception.Failure.Code);
    }
}

public class SessionTitleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-title-" + Guid.NewGuid().ToString("N")[..8]);

    public SessionTitleTests() => Directory.CreateDirectory(_root);

    private static async Task WaitForTitle(Session session)
    {
        for (var i = 0; i < 50 && !session.Events.Any(e => e.Type == SessionEventTypes.SessionTitle); i++)
        {
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task FirstTurn_GeneratesTitleFromLlm()
    {
        await using var harness = TestHarness.Create(options =>
            options.Purpose == "session-title" ? ReplayScript.Text("Fix the login bug") : ReplayScript.Text("done"));
        SessionTitleService.Mount(harness.Ctx);
        var agent = harness.CreateAgent(_root);

        agent.Followup(Message.CreateUserText("please fix the auth login issue"));
        await agent.WhenIdleAsync();
        await WaitForTitle(agent.Session);

        var title = agent.Session.Events.Single(e => e.Type == SessionEventTypes.SessionTitle);
        Assert.Equal("Fix the login bug", SessionEventRead.TitleOf(title));
        Assert.Equal("generated", title.Data.GetProperty("source").GetString());
    }

    [Fact]
    public async Task EmptyGeneration_FallsBackToFirstUserText()
    {
        await using var harness = TestHarness.Create(options =>
            options.Purpose == "session-title" ? ReplayScript.Text("") : ReplayScript.Text("done"));
        SessionTitleService.Mount(harness.Ctx);
        var agent = harness.CreateAgent(_root);

        agent.Followup(Message.CreateUserText("short prompt"));
        await agent.WhenIdleAsync();
        await WaitForTitle(agent.Session);

        var title = agent.Session.Events.Single(e => e.Type == SessionEventTypes.SessionTitle);
        Assert.Equal("short prompt", SessionEventRead.TitleOf(title));
        Assert.Equal("fallback", title.Data.GetProperty("source").GetString());
    }

    [Fact]
    public async Task ManualRename_IsNeverOverwritten()
    {
        await using var harness = TestHarness.Create(options =>
            options.Purpose == "session-title" ? ReplayScript.Text("Generated Title") : ReplayScript.Text("done"));
        SessionTitleService.Mount(harness.Ctx);
        var agent = harness.CreateAgent(_root);
        agent.Session.Append(SessionEventTypes.SessionTitle,
            new SessionPayloads.SessionTitlePayload("My Manual Title", [], "user"));

        agent.Followup(Message.CreateUserText("please fix the auth login issue"));
        await agent.WhenIdleAsync();
        await Task.Delay(500); // give a (wrongly) scheduled generation time to misbehave

        var titles = agent.Session.Events.Where(e => e.Type == SessionEventTypes.SessionTitle).ToList();
        Assert.Single(titles);
        Assert.Equal("My Manual Title", SessionEventRead.TitleOf(titles.Single()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
