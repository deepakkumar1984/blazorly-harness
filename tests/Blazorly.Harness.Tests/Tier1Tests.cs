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
            options.Purpose == "compaction" ? Scripted.Text("SUMMARY: compacted.") : Scripted.Text("ok"));
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

    /// <summary>Sub-millisecond policy: the production rate-limit window (5s–30s) would make tests crawl.</summary>
    private static RetryPolicyConfig Fast(int maxRetries = 5, string mode = "normal") => new()
    {
        Mode = mode,
        MaxRetries = maxRetries,
        InitialDelayMs = 1,
        MaxDelayMs = 5,
        RateLimitMinDelayMs = 0,
        RateLimitMaxDelayMs = 5,
    };

    [Fact]
    public async Task RetryableFailure_IsRetriedWithDurableTrail()
    {
        await using var harness = HarnessWithFlaky((options, calls) =>
            calls == 0 ? Scripted.Error(LlmErrorCodes.RateLimit, "slow down") : Scripted.Text("recovered"));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            Default = Fast(),
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
            calls == 0 ? Scripted.Error(LlmErrorCodes.Auth, "bad key") : Scripted.Text("unreachable"));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            Default = Fast(),
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
    public async Task InvalidRequest_Generic400_ReducesMaxTokensAndRecovers()
    {
        // Unknown ceiling + a 400 that names nothing (the generic-rejection case): halve
        // 128000 -> 64000 -> 32000 until the route accepts, then keep the working value.
        await using var harness = HarnessWithFlaky((options, _) =>
            options.MaxTokens is > 32000
                ? Scripted.Error(LlmErrorCodes.InvalidRequest, "provider rejected request (400)")
                : Scripted.Text("recovered"));
        RetryService.Mount(harness.Ctx, new RetryOptions { Default = Fast() });
        var agent = harness.CreateAgent(options: new AgentOptions(MaxTokens: 128000));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var retries = agent.Session.Events.Where(e => e.Type == SessionEventTypes.LlmRetry).ToList();
        Assert.Equal(2, retries.Count);
        Assert.Equal(128000, retries[0].Data.GetProperty("maxTokensFrom").GetInt32());
        Assert.Equal(64000, retries[0].Data.GetProperty("maxTokensTo").GetInt32());
        Assert.Equal(64000, retries[1].Data.GetProperty("maxTokensFrom").GetInt32());
        Assert.Equal(32000, retries[1].Data.GetProperty("maxTokensTo").GetInt32());
        Assert.Equal(32000, agent.Options.MaxTokens); // the accepted ceiling sticks
        Assert.Contains("recovered", agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.AssistantMessage)
            .Last().Data.GetProperty("message").GetProperty("content").EnumerateArray()
            .First(b => b.GetProperty("type").GetString() == "text").GetProperty("text").GetString());
    }

    [Fact]
    public async Task InvalidRequest_EffortBlame_IsNotAdapted()
    {
        // A 400 naming the reasoning effort fails identically at any cap: no adaptation,
        // options untouched, and the terminal hint points at the effort knob.
        await using var harness = HarnessWithFlaky((options, _) =>
            Scripted.Error(LlmErrorCodes.InvalidRequest, "reasoning effort 'max' is not supported by this model"));
        RetryService.Mount(harness.Ctx, new RetryOptions { Default = Fast() });
        var agent = harness.CreateAgent(options: new AgentOptions(MaxTokens: 128000, ReasoningEffort: "max"));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        Assert.Equal(128000, agent.Options.MaxTokens);
        var reason = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd).Data.GetProperty("reason");
        Assert.Equal("INVALID_REQUEST", reason.GetProperty("code").GetString());
        Assert.Contains("Reset with /effort default", reason.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvalidRequest_AtFloor_IsNotAdapted()
    {
        await using var harness = HarnessWithFlaky((options, _) =>
            Scripted.Error(LlmErrorCodes.InvalidRequest, "provider rejected request (400)"));
        RetryService.Mount(harness.Ctx, new RetryOptions { Default = Fast() });
        var agent = harness.CreateAgent(options: new AgentOptions(MaxTokens: 1024));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        Assert.Equal(1024, agent.Options.MaxTokens);
        var reason = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd).Data.GetProperty("reason");
        Assert.Equal("INVALID_REQUEST", reason.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidRequest_UnknownModel_IsNotAdapted()
    {
        await using var harness = HarnessWithFlaky((options, _) =>
            Scripted.Error(LlmErrorCodes.InvalidRequest, "provider rejected request (400: model 'mimo-v9' not found)"));
        RetryService.Mount(harness.Ctx, new RetryOptions { Default = Fast() });
        var agent = harness.CreateAgent(options: new AgentOptions(MaxTokens: 128000));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        Assert.Equal(128000, agent.Options.MaxTokens);
    }

    [Fact]
    public async Task InvalidRequest_BadArguments_IsNotAdapted()
    {
        // A strict gateway rejecting a tool call's arguments fails identically at any cap:
        // no max_tokens probing, options untouched, terminal hint names the arguments.
        await using var harness = HarnessWithFlaky((options, _) =>
            Scripted.Error(LlmErrorCodes.InvalidRequest,
                """provider rejected request (400: {"success":false,"error":"'arguments' must be valid JSON"})"""));
        RetryService.Mount(harness.Ctx, new RetryOptions { Default = Fast() });
        var agent = harness.CreateAgent(options: new AgentOptions(MaxTokens: 128000));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        Assert.Equal(128000, agent.Options.MaxTokens);
        var reason = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd).Data.GetProperty("reason");
        Assert.Equal("INVALID_REQUEST", reason.GetProperty("code").GetString());
        Assert.Contains("arguments", reason.GetProperty("message").GetString());
        Assert.DoesNotContain("max_tokens 128000", reason.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvalidRequest_Persistent400_StopsAtAttemptBudget()
    {
        // A route that rejects everything: 5 adaptations (128000 -> 4000), then terminal.
        await using var harness = HarnessWithFlaky((options, _) =>
            Scripted.Error(LlmErrorCodes.InvalidRequest, "provider rejected request (400)"));
        RetryService.Mount(harness.Ctx, new RetryOptions { Default = Fast() });
        var agent = harness.CreateAgent(options: new AgentOptions(MaxTokens: 128000));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var retries = agent.Session.Events.Where(e => e.Type == SessionEventTypes.LlmRetry).ToList();
        Assert.Equal(5, retries.Count);
        Assert.Equal(4000, agent.Options.MaxTokens);
        var reason = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd).Data.GetProperty("reason");
        Assert.Equal("INVALID_REQUEST", reason.GetProperty("code").GetString());
    }

    [Fact]
    public void TryReduceMaxTokens_RequiresInvalidRequestAndReducibleCap()
    {
        Assert.False(RetryService.TryReduceMaxTokens(new LlmFailure("slow down", LlmErrorCodes.RateLimit), 128000, out _));
        Assert.False(RetryService.TryReduceMaxTokens(new LlmFailure("too long", LlmErrorCodes.ContextWindowExceeded), 128000, out _));
        Assert.False(RetryService.TryReduceMaxTokens(new LlmFailure("provider rejected request (400)", LlmErrorCodes.InvalidRequest), null, out _));
        Assert.False(RetryService.TryReduceMaxTokens(new LlmFailure("provider rejected request (400)", LlmErrorCodes.InvalidRequest), 1024, out _));
        Assert.True(RetryService.TryReduceMaxTokens(new LlmFailure("provider rejected request (400)", LlmErrorCodes.InvalidRequest), 128000, out var reduced));
        Assert.Equal(64000, reduced);
    }

    [Fact]
    public void TryReduceMaxTokens_NamedMaxTokensAlwaysAdapts()
    {
        // An explicit max_tokens complaint wins even when the message also says reasoning.
        Assert.True(RetryService.TryReduceMaxTokens(
            new LlmFailure("max_tokens 128000 exceeds the per-request limit for reasoning models", LlmErrorCodes.InvalidRequest),
            128000, out var reduced));
        Assert.Equal(64000, reduced);
    }

    [Fact]
    public void TryReduceMaxTokens_EffortOrMissingModelBlameSkips()
    {
        foreach (var message in new[]
        {
            "reasoning effort 'max' is not supported by this model",
            "provider rejected request (400: model 'mimo-v9' not found)",
            "provider rejected request (400: unknown model 'mimo-v9')",
            "provider rejected request (400: model does not support tools)",
            """provider rejected request (400: {"success":false,"error":"'arguments' must be valid JSON"})""",
            "provider rejected request (400: unknown tool 'bash_exec')",
        })
        {
            Assert.False(RetryService.TryReduceMaxTokens(new LlmFailure(message, LlmErrorCodes.InvalidRequest), 128000, out _), message);
        }
    }

    [Fact]
    public void TryReduceMaxTokens_ClampsToFloor()
    {
        Assert.True(RetryService.TryReduceMaxTokens(new LlmFailure("provider rejected request (400)", LlmErrorCodes.InvalidRequest), 1500, out var clamped));
        Assert.Equal(1024, clamped);
        Assert.True(RetryService.TryReduceMaxTokens(new LlmFailure("provider rejected request (400)", LlmErrorCodes.InvalidRequest), 1025, out clamped));
        Assert.Equal(1024, clamped);
        Assert.False(RetryService.TryReduceMaxTokens(new LlmFailure("provider rejected request (400)", LlmErrorCodes.InvalidRequest), 1024, out _));
    }

    [Fact]
    public async Task QuotaExhaustion_FailsFastWithTheProvidersMessage()
    {
        // Z.ai answers an empty balance with 429 + code 1113; classified as QUOTA it must not be
        // retried, and the turn-end reason must carry the provider's own words to the UI.
        await using var harness = HarnessWithFlaky((options, calls) => Scripted.Error(LlmErrorCodes.Quota,
            "provider balance or quota exhausted (429: Insufficient balance or no resource package. Please recharge.)"));
        RetryService.Mount(harness.Ctx, new RetryOptions { Default = Fast() });
        var agent = harness.CreateAgent();

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        var reason = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd).Data.GetProperty("reason");
        Assert.Equal("QUOTA", reason.GetProperty("code").GetString());
        Assert.Contains("Insufficient balance or no resource package. Please recharge.", reason.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AlwaysMode_RetriesAnyCodeWithoutAttemptCeiling()
    {
        await using var harness = HarnessWithFlaky((options, calls) =>
            calls == 0 ? Scripted.Error(LlmErrorCodes.Auth, "transient gateway hiccup") : Scripted.Text("recovered"));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            Default = Fast(mode: "always"),
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

    [Fact]
    public void ScheduleDelay_ProviderRetryAfterBeyondMaxDelayIsWaitedOutUpToTheCap()
    {
        // A 30s rate-limit window is longer than MaxDelayMs but shorter than the cap, so the
        // provider's ask is honored verbatim instead of burning attempts on a 10s backoff.
        var policy = new RetryPolicyConfig { MaxDelayMs = 10_000, MaxRetryAfterMs = 60_000 };
        Assert.Equal(60_000, RetryService.RetryAfterCap(policy));
        Assert.Equal(30_000, RetryService.ScheduleDelay(policy, 30_000, attempts: 4));
        // Past the cap the ask is unschedulable and local backoff takes over:
        // min(500 * 2^4, 10_000) = 8000ms ± 10% jitter.
        Assert.InRange(RetryService.ScheduleDelay(policy, 120_000, attempts: 4), 7_200, 8_800);
        // The cap never drops below MaxDelayMs.
        Assert.Equal(10_000, RetryService.RetryAfterCap(policy with { MaxRetryAfterMs = 0 }));
    }

    [Fact]
    public async Task RateLimitWithLongProviderRetryAfter_IsRetriedAfterTheAskAndRecorded()
    {
        await using var harness = HarnessWithFlaky((options, calls) =>
            calls == 0
                ? Scripted.Error(LlmErrorCodes.RateLimit, "rate limited (429)", providerRetryAfterMs: 20)
                : Scripted.Text("recovered"));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            // MaxDelayMs 5 would have declined a 20ms ask before MaxRetryAfterMs existed.
            Default = Fast() with { MaxRetryAfterMs = 60 },
        });
        var agent = harness.CreateAgent();

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var retry = agent.Session.Events.Single(e => e.Type == SessionEventTypes.LlmRetry).Data;
        Assert.Equal(20, retry.GetProperty("delayMs").GetInt64());
        Assert.Equal(20, retry.GetProperty("retryAfterMs").GetInt64());
        Assert.False(agent.Session.Events.Single(e => e.Type == SessionEventTypes.LlmRetryStarted)
            .Data.TryGetProperty("retryAfterMs", out _));
        Assert.Contains("recovered", agent.Session.Events
            .Last(e => e.Type == SessionEventTypes.AssistantMessage).Data.GetProperty("message")
            .GetProperty("content").EnumerateArray()
            .First(b => b.GetProperty("type").GetString() == "text").GetProperty("text").GetString());
    }

    [Fact]
    public async Task RateLimitAskingLongerThanTheCap_FallsThroughWithoutARetry()
    {
        await using var harness = HarnessWithFlaky((options, calls) =>
            Scripted.Error(LlmErrorCodes.RateLimit, "rate limited (429)", providerRetryAfterMs: 600));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            Default = Fast(maxRetries: 3) with { MaxRetryAfterMs = 50 },
        });
        var agent = harness.CreateAgent();
        agent.RetryLimit = 0; // the exact-provider policy declined; no downstream retry either

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.LlmRetry);
        var turnEnd = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd);
        Assert.Equal("RATE_LIMIT", turnEnd.Data.GetProperty("reason").GetProperty("code").GetString());
    }

    [Fact]
    public void ScheduleDelay_RateLimitWithoutProviderGuidanceIsSpacedAcrossTheQuotaWindow()
    {
        var policy = new RetryPolicyConfig { InitialDelayMs = 500, MaxDelayMs = 10_000, JitterRatio = 0 }; // no jitter: exact windows
        // Non-rate-limit codes keep the ordinary backoff ladder.
        Assert.Equal(500, RetryService.ScheduleDelay(policy, null, attempts: 0, code: LlmErrorCodes.Server));
        // RATE_LIMIT starts at the floor, doubles, and stops at the rate-limit ceiling.
        Assert.Equal(5_000, RetryService.ScheduleDelay(policy, null, attempts: 0, code: LlmErrorCodes.RateLimit));
        Assert.Equal(10_000, RetryService.ScheduleDelay(policy, null, attempts: 1, code: LlmErrorCodes.RateLimit));
        Assert.Equal(20_000, RetryService.ScheduleDelay(policy, null, attempts: 2, code: LlmErrorCodes.RateLimit));
        Assert.Equal(30_000, RetryService.ScheduleDelay(policy, null, attempts: 5, code: LlmErrorCodes.RateLimit));
        // A usable provider ask still wins over the local window.
        Assert.Equal(7_000, RetryService.ScheduleDelay(policy, 7_000, attempts: 0, code: LlmErrorCodes.RateLimit));
        // Opting out of the floor restores the plain ladder for rate limits.
        Assert.Equal(1_000, RetryService.ScheduleDelay(policy with { RateLimitMinDelayMs = 0 }, null, attempts: 1,
            code: LlmErrorCodes.RateLimit));
    }

    [Fact]
    public async Task RateLimitStorm_WaitsOutTheWindowAndRecovers()
    {
        await using var harness = HarnessWithFlaky((options, calls) =>
            calls < 3 ? Scripted.Error(LlmErrorCodes.RateLimit, "rate limited (429)") : Scripted.Text("recovered"));
        RetryService.Mount(harness.Ctx, new RetryOptions
        {
            // Shrunken window (5ms floor, 12ms ceiling) so the test proves the spacing, not the wall clock.
            Default = new RetryPolicyConfig
            {
                InitialDelayMs = 1,
                MaxDelayMs = 4,
                JitterRatio = 0,
                RateLimitMinDelayMs = 5,
                RateLimitMaxDelayMs = 12,
            },
        });
        var agent = harness.CreateAgent();

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var retries = agent.Session.Events.Where(e => e.Type == SessionEventTypes.LlmRetry).ToList();
        Assert.Equal([5, 10, 12], retries.Select(e => e.Data.GetProperty("delayMs").GetInt64()).ToList()); // floor, doubling, clamped at the ceiling — never the 1ms generic ladder
        Assert.False(retries[0].Data.TryGetProperty("retryAfterMs", out _)); // no provider ask was sent
        Assert.Contains("recovered", agent.Session.Events
            .Last(e => e.Type == SessionEventTypes.AssistantMessage).Data.GetProperty("message")
            .GetProperty("content").EnumerateArray()
            .First(b => b.GetProperty("type").GetString() == "text").GetProperty("text").GetString());
    }

    [Fact]
    public async Task PolicyFor_PrefersTheProviderOverride()
    {
        await using var harness = HarnessWithFlaky((options, calls) => Scripted.Text("unused"));
        var service = RetryService.Mount(harness.Ctx, new RetryOptions
        {
            Default = new RetryPolicyConfig { MaxRetries = 2 },
            Providers = new Dictionary<string, RetryPolicyConfig>(StringComparer.Ordinal)
            {
                ["zai"] = new() { MaxRetries = 9, MaxRetryAfterMs = 300_000 },
            },
        });

        Assert.Equal(9, service.PolicyFor("zai").MaxRetries);
        Assert.Equal(300_000, service.PolicyFor("zai").MaxRetryAfterMs);
        Assert.Equal(2, service.PolicyFor("deepseek").MaxRetries);
        Assert.Equal(2, service.PolicyFor(null).MaxRetries);
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

    [Fact]
    public void ReasoningBlock_LegacyLogsWithoutSignature_StillFold()
    {
        // Sessions persisted before signatures were captured have no signature member; a throw
        // here would blank the whole transcript view instead of one block.
        var legacy = JsonSerializer.Deserialize<ContentBlock>("""{"type":"reasoning","text":"old thought"}""", SessionJson.Options);
        var reasoning = Assert.IsType<ReasoningBlock>(legacy);
        Assert.Equal("old thought", reasoning.Text);
        Assert.Null(reasoning.Signature);

        // And routes that never sign write the same bytes they always did.
        var written = SessionJson.ToElement(new ReasoningBlock("t"));
        Assert.False(written.TryGetProperty("signature", out _));
    }

    private static Dictionary<string, object?> ThinkingOf(IReadOnlyDictionary<string, object?> fields)
        => (Dictionary<string, object?>)fields["thinking"];

    private static GenerateOptions ThinkingOptions(string? effort, int? maxTokens = null, string? purpose = null, double? temperature = null)
        => new()
        {
            Provider = "anthropic",
            Model = "claude-test",
            Messages = [Llm.Message.CreateUserText("hi")],
            ReasoningEffort = effort,
            MaxTokens = maxTokens,
            Purpose = purpose,
            Temperature = temperature,
        };

    // Anthropic only streams thinking when the request asks for it. Without the thinking field the
    // effort picker was a no-op on Anthropic routes and the gateway answered with a blank thinking
    // block (observed live on Azure: one empty thinking_delta per tool-use step).
    [Fact]
    public void Thinking_EffortEnablesThinkingWithBudget_OffDisables_UnsetOmits()
    {
        var adapter = Adapter(_ => Sse(""));

        var high = adapter.BuildThinkingFields(ThinkingOptions("high", maxTokens: 64_000));
        Assert.Equal("enabled", ThinkingOf(high)["type"]);
        Assert.Equal(16_384, ThinkingOf(high)["budget_tokens"]);

        Assert.Equal("disabled", ThinkingOf(adapter.BuildThinkingFields(ThinkingOptions("off", maxTokens: 64_000)))["type"]);
        Assert.Empty(adapter.BuildThinkingFields(ThinkingOptions(null, maxTokens: 64_000)));

        // Titles stay cheap even when the session runs at max.
        Assert.Equal("disabled", ThinkingOf(adapter.BuildThinkingFields(
            ThinkingOptions("max", maxTokens: 64_000, purpose: "session-title")))["type"]);
    }

    [Fact]
    public void Thinking_BudgetStaysBelowMaxTokens_AndIsDroppedWhenThereIsNoRoom()
    {
        var adapter = Adapter(_ => Sse(""));

        // "max" wants 32768; a 2000-token request cannot grant it.
        Assert.Equal(1_999, ThinkingOf(adapter.BuildThinkingFields(ThinkingOptions("max", maxTokens: 2_000)))["budget_tokens"]);

        // budget_tokens must be >= 1024 and < max_tokens, so a small request thinks not at all
        // rather than provoking a 400.
        Assert.Empty(adapter.BuildThinkingFields(ThinkingOptions("high", maxTokens: AnthropicAdapter.MinThinkingBudget)));
        Assert.Empty(adapter.BuildThinkingFields(ThinkingOptions("high", maxTokens: 256)));

        // Unset keeps the default ceiling and still leaves room to think.
        Assert.Equal(8_191, ThinkingOf(adapter.BuildThinkingFields(ThinkingOptions("high")))["budget_tokens"]);
    }

    [Fact]
    public void Thinking_EnabledDropsTemperature_DisabledKeepsIt()
    {
        var adapter = Adapter(_ => Sse(""));

        var enabled = (Dictionary<string, object?>)adapter.BuildWireBody(ThinkingOptions("high", maxTokens: 64_000, temperature: 0.2));
        Assert.False(enabled.ContainsKey("temperature")); // Anthropic rejects a set temperature next to thinking
        Assert.Equal("enabled", ThinkingOf(enabled)["type"]);

        var disabled = (Dictionary<string, object?>)adapter.BuildWireBody(ThinkingOptions("off", maxTokens: 64_000, temperature: 0.2));
        Assert.Equal(0.2, disabled["temperature"]);

        var unset = (Dictionary<string, object?>)adapter.BuildWireBody(ThinkingOptions(null, maxTokens: 64_000, temperature: 0.2));
        Assert.Equal(0.2, unset["temperature"]);
    }

    [Fact]
    public async Task Stream_BlankThinkingBlock_ProducesNoReasoningBlock()
    {
        // The shape Azure's Anthropic endpoint returns on tool-use turns: a thinking block that
        // opens, carries one empty delta and a signature, and never any text. Opening the block on
        // content_block_start used to leave an empty reasoning block in every transcript.
        var sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":5}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EqQBCgIY"}}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tc_9","name":"bash"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{}"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":4}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var adapter = Adapter(_ => Sse(sse));

        var chunks = await Collect(adapter.Stream(Options([Llm.Message.CreateUserText("hi")])));

        Assert.DoesNotContain(chunks, c => c is ReasoningDeltaChunk);
        Assert.DoesNotContain(chunks, c => c is BlockStartChunk { BlockType: "reasoning" });
        Assert.DoesNotContain(chunks.OfType<BlockEndChunk>(), c => c.Block is ReasoningBlock);
        Assert.Contains(chunks.OfType<BlockEndChunk>(), c => c.Block is ToolCallBlock { Id: "tc_9" });
    }

    [Fact]
    public async Task Stream_Thinking_CapturesSignature_AndReplaysItAheadOfTheTurn()
    {
        var sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":5}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me "}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"check."}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc"}}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Answer"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":6}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var adapter = Adapter(_ => Sse(sse));

        var chunks = await Collect(adapter.Stream(Options([Llm.Message.CreateUserText("hi")])));

        Assert.Equal("Let me check.", string.Concat(chunks.OfType<ReasoningDeltaChunk>().Select(d => d.Text)));
        var reasoning = Assert.IsType<ReasoningBlock>(chunks.OfType<BlockEndChunk>().First(c => c.Block is ReasoningBlock).Block);
        Assert.Equal("Let me check.", reasoning.Text);
        Assert.Equal("sig-abc", reasoning.Signature);

        // Signed thinking replays, and leads the turn: Anthropic rejects a tool_use that arrives
        // without the thinking block in front of it. Unsigned thinking still cannot be replayed.
        var assistant = new Llm.Message("m1", "assistant",
            [new TextBlock("Answer"), reasoning, new ReasoningBlock("unsigned thought"), new ToolCallBlock("tc_1", "bash", "{}")],
            Llm.MessageSource.FromModel("anthropic", "claude-test"));
        var wire = ((Dictionary<string, object?>)adapter.BuildWireBody(new GenerateOptions
        {
            Provider = "anthropic",
            Model = "claude-test",
            Messages = [assistant],
        }))["messages"];
        var blocks = Assert.IsType<List<object>>(Assert.IsType<List<Dictionary<string, object?>>>(wire).Single()["content"]);
        var types = blocks.Cast<Dictionary<string, object?>>()
            .Select(b => (string)b["type"]!)
            .ToList();
        Assert.Equal(["thinking", "text", "tool_use"], types);
        Assert.Equal("sig-abc", Assert.IsType<Dictionary<string, object?>>(blocks[0])["signature"]);
        Assert.Equal("Let me check.", Assert.IsType<Dictionary<string, object?>>(blocks[0])["thinking"]);
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
            options.Purpose == "session-title" ? Scripted.Text("Fix the login bug") : Scripted.Text("done"));
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
            options.Purpose == "session-title" ? Scripted.Text("") : Scripted.Text("done"));
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
            options.Purpose == "session-title" ? Scripted.Text("Generated Title") : Scripted.Text("done"));
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
