using System.Net;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Decisions;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Xunit;
using Agent = Blazorly.Harness.Core.Agent.Agent;

namespace Blazorly.Harness.Tests;

/// <summary>A local stand-in for a System One endpoint; replies with a fixed body per request.</summary>
internal sealed class FakeDecisionServer : IDisposable
{
    private HttpListener _listener = new();

    public string BaseUrl { get; private set; } = "";
    public Func<string, (int Status, string Body)> Responder { get; set; } = _ => (200, "{}");
    public int Requests;
    public string? LastBody;
    public string? LastAuthorization;
    public string? LastApiKeyHeader;
    /// <summary>Content-Length seen per request; null means the request was chunked.</summary>
    public List<string?> ContentLengths { get; } = [];
    /// <summary>When set, the server stalls this long before replying (drives timeout tests).</summary>
    public int DelayMs { get; set; }

    public FakeDecisionServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            // Deliberately below the OS ephemeral band. CannedWebServer picks a free port by
            // binding and releasing a TcpListener, so a listener of ours squatting in that band
            // would make its Start() throw — and it does not retry. Staying out of the way keeps
            // the web tests as stable as they were before this file existed.
            var port = Random.Shared.Next(21000, 24000);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                BaseUrl = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) when (attempt < 50)
            {
                _listener.Close();
            }
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    LastBody = await reader.ReadToEndAsync();
                    LastAuthorization = context.Request.Headers["Authorization"];
                    LastApiKeyHeader = context.Request.Headers["X-API-Key"];
                    lock (ContentLengths) ContentLengths.Add(context.Request.Headers["Content-Length"]);
                    Interlocked.Increment(ref Requests);
                    if (DelayMs > 0) await Task.Delay(DelayMs);
                    var (status, body) = Responder(LastBody);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.OutputStream.Write(bytes);
                    context.Response.Close();
                }
            }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        });
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch { /* already stopped */ }
    }
}

/// <summary>A decision model that returns a canned result, so seams can be tested without a wire.</summary>
internal sealed class ScriptedDecisionModel : IDecisionModel
{
    private readonly Func<DecisionState, IReadOnlyList<DecisionQuestion>, DecisionResult?> _script;

    public ScriptedDecisionModel(Func<DecisionState, IReadOnlyList<DecisionQuestion>, DecisionResult?> script)
        => _script = script;

    public static ScriptedDecisionModel Noul(string key, double probability)
        => new((state, _) => new DecisionResult("scripted",
            new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
            {
                [key] = new DecisionAnswer(key, DecisionAnswer.NoulKind) { Probability = probability },
            }, 1, state.Chars));

    /// <summary>Always answers 1.0 to every noul asked.</summary>
    public static ScriptedDecisionModel AllYes()
        => new((state, questions) => new DecisionResult("scripted",
            questions.ToDictionary(q => q.Key,
                q => new DecisionAnswer(q.Key, DecisionAnswer.NoulKind) { Probability = 1.0 },
                StringComparer.Ordinal),
            1, state.Chars));

    public string Impl => "scripted";
    public bool Available => true;
    public int Calls;

    public Task<DecisionResult?> DecideAsync(
        DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(_script(state, questions));
    }
}

/// <summary>
/// System One wire parsing. The published schema is not pinned, so the adapter accepts several
/// shapes; each one is asserted here so a service change shows up as a test failure rather than
/// as a silently abstaining gate.
/// </summary>
public class SystemOneWireTests
{
    private static SystemOneClient Client(FakeDecisionServer server, string auth = "bearer")
        => new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            new SystemOneOptions
            {
                ApiKey = "tsk-test",
                BaseUrl = server.BaseUrl,
                Path = "/v1/systemone",
                AuthStyle = auth,
                TimeoutMs = 5_000,
            }, ownsClient: false);

    private static DecisionState State() => new DecisionState().Add("brief", "refactor everything across the repo");

    private static readonly Noul Question = new("needs_plan", "does this need a plan?");

    [Theory]
    [InlineData("""{"answers":{"needs_plan":{"probability":0.93}}}""", 0.93)]
    [InlineData("""{"decisions":[{"key":"needs_plan","probability":0.93}]}""", 0.93)]
    [InlineData("""{"needs_plan":0.93}""", 0.93)]
    [InlineData("""{"result":{"answers":{"needs_plan":{"p":0.93}}}}""", 0.93)]
    [InlineData("""{"answers":{"needs_plan":{"yes":0.93}}}""", 0.93)]
    [InlineData("""{"answers":{"needs_plan":true}}""", 1.0)]
    public async Task NoulShapes_AllParse(string body, double expected)
    {
        using var server = new FakeDecisionServer { Responder = _ => (200, body) };
        using var client = Client(server);
        var result = await client.DecideAsync(State(), [Question]);
        Assert.Equal(expected, result!.ProbabilityOf("needs_plan")!.Value, 3);
        Assert.False(result.Degraded);
    }

    [Fact]
    public async Task Choice_AnInventedOptionIsNoAnswer()
    {
        // The whole point of a typed choice: the model cannot introduce an option that was not declared.
        using var server = new FakeDecisionServer
        {
            Responder = _ => (200, """{"answers":{"route":{"value":"delete-the-repo"}}}"""),
        };
        using var client = Client(server);
        var result = await client.DecideAsync(State(),
            [new Choice("route", "which route?", ["cheap", "frontier"])]);
        Assert.True(result!.Degraded);
        Assert.Empty(result.Answers);
    }

    [Fact]
    public async Task Choice_ADeclaredOptionParsesWithItsDistribution()
    {
        using var server = new FakeDecisionServer
        {
            Responder = _ => (200, """{"answers":{"route":{"value":"cheap","options":{"cheap":0.8,"frontier":0.2}}}}"""),
        };
        using var client = Client(server);
        var result = await client.DecideAsync(State(), [new Choice("route", "which route?", ["cheap", "frontier"])]);
        Assert.Equal("cheap", result!.ValueOf("route"));
        Assert.Equal(0.8, result.Answers["route"].Options!["cheap"], 3);
    }

    [Fact]
    public async Task Rating_ClampsToTheDeclaredRange()
    {
        using var server = new FakeDecisionServer { Responder = _ => (200, """{"answers":{"risk":{"score":99}}}""") };
        using var client = Client(server);
        var result = await client.DecideAsync(State(), [new Rating("risk", "how risky?", 0, 10)]);
        Assert.Equal(10, result!.ScoreOf("risk")!.Value, 3);
    }

    [Fact]
    public async Task Timeout_IsADegradedNoOpinion_NotAnException()
    {
        using var server = new FakeDecisionServer { DelayMs = 2_000, Responder = _ => (200, "{}") };
        using var client = new SystemOneClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            new SystemOneOptions { ApiKey = "tsk-test", BaseUrl = server.BaseUrl, TimeoutMs = 100 },
            ownsClient: false);

        var result = await client.DecideAsync(State(), [Question]);
        Assert.NotNull(result);
        Assert.True(result!.Degraded);
        Assert.Empty(result.Answers);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public async Task UserCancellation_PropagatesInsteadOfBecomingATimeout()
    {
        using var server = new FakeDecisionServer { DelayMs = 5_000, Responder = _ => (200, "{}") };
        using var client = new SystemOneClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            new SystemOneOptions { ApiKey = "tsk-test", BaseUrl = server.BaseUrl, TimeoutMs = 4_000 },
            ownsClient: false);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        // The interruption contract: a user cancel must surface, not be swallowed as "no opinion".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DecideAsync(State(), [Question], cancelled.Token));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(429)]
    public async Task HttpFailure_IsDegradedWithTheStatus(int status)
    {
        using var server = new FakeDecisionServer { Responder = _ => (status, """{"error":"nope"}""") };
        using var client = Client(server);
        var result = await client.DecideAsync(State(), [Question]);
        Assert.True(result!.Degraded);
        Assert.Contains(status.ToString(), result.Error);
    }

    [Fact]
    public async Task UnparseableBody_IsDegraded()
    {
        using var server = new FakeDecisionServer { Responder = _ => (200, "<html>not json</html>") };
        using var client = Client(server);
        var result = await client.DecideAsync(State(), [Question]);
        Assert.True(result!.Degraded);
        Assert.Empty(result.Answers);
    }

    [Fact]
    public async Task AuthStyle_SelectsTheHeader()
    {
        using var bearer = new FakeDecisionServer { Responder = _ => (200, """{"answers":{"needs_plan":{"probability":0.1}}}""") };
        using (var client = Client(bearer, "bearer"))
        {
            await client.DecideAsync(State(), [Question]);
        }
        Assert.Equal("Bearer tsk-test", bearer.LastAuthorization);

        using var apiKey = new FakeDecisionServer { Responder = _ => (200, """{"answers":{"needs_plan":{"probability":0.1}}}""") };
        using (var client = Client(apiKey, "apikey"))
        {
            await client.DecideAsync(State(), [Question]);
        }
        Assert.Equal("tsk-test", apiKey.LastApiKeyHeader);
    }

    [Fact]
    public async Task ProbeThenDecide_BothSendTheSameQuestions()
    {
        // Reproduces the `blazorly decisions probe` sequence exactly: one client, one state,
        // ProbeAsync then DecideAsync. The probe must never send a different request than the
        // real call, or it is not verifying anything.
        var bodies = new List<string>();
        using var server = new FakeDecisionServer
        {
            Responder = body => { bodies.Add(body); return (200, """{"answers":{"needs_plan":{"probability":0.5}}}"""); },
        };
        using var client = Client(server);
        var state = new DecisionState().Add("brief", "a brief");
        DecisionQuestion[] questions = [new Noul("needs_plan", "?"), new Rating("risk", "?", 0, 10)];

        await client.ProbeAsync(state, questions);
        await client.DecideAsync(state, questions);

        Assert.Equal(2, bodies.Count);
        foreach (var body in bodies)
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(2, document.RootElement.GetProperty("questions").GetArrayLength());
        }
        // Both paths must declare a Content-Length. PostAsJsonAsync streams a JsonContent as
        // chunked, which servers that only read Content-Length see as an empty body — the exact
        // defect that made `decisions probe` disagree with the live seam.
        Assert.Equal(2, server.ContentLengths.Count);
        Assert.All(server.ContentLengths, length => Assert.False(string.IsNullOrEmpty(length)));
        // And byte-identical: the probe is only meaningful if it sends what the real call sends.
        Assert.Equal(bodies[0], bodies[1]);
    }

    [Fact]
    public async Task Request_CarriesStateAndTypedQuestions()
    {
        using var server = new FakeDecisionServer { Responder = _ => (200, """{"answers":{"needs_plan":{"probability":0.1}}}""") };
        using var client = Client(server);
        await client.DecideAsync(new DecisionState().Add("brief", "the brief"),
            [Question, new Choice("route", "which?", ["a", "b"]), new Rating("risk", "how risky?", 0, 10)]);

        using var document = JsonDocument.Parse(server.LastBody!);
        var root = document.RootElement;
        Assert.Equal("the brief", root.GetProperty("state").GetProperty("brief").GetString());
        var kinds = root.GetProperty("questions").EnumerateArray()
            .Select(q => q.GetProperty("kind").GetString()!).ToArray();
        Assert.Equal(["noul", "choice", "rating"], kinds);
        Assert.Equal(2, root.GetProperty("questions")[1].GetProperty("options").GetArrayLength());
    }
}

/// <summary>
/// The decision service contract: no opinion is a first-class result, every decision is durable,
/// and disabling the seam makes it indistinguishable from the seam never having existed.
/// </summary>
public class DecisionServiceTests
{
    private static DecisionService Service(IDecisionModel model, DecisionOptions? options = null)
        => new(model, options ?? new DecisionOptions { Enabled = true, Seams = ["*"] });

    [Fact]
    public async Task Disabled_SkipsTheCallEntirely()
    {
        var model = ScriptedDecisionModel.AllYes();
        var service = Service(model, new DecisionOptions { Enabled = false, Seams = ["*"] });
        var result = await service.DecideAsync(DecisionSeams.AutoPlan, new DecisionState().Add("a", "b"), [new Noul("k", "?")]);
        Assert.Null(result);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task SeamNotListed_SkipsTheCall()
    {
        var model = ScriptedDecisionModel.AllYes();
        var service = Service(model, new DecisionOptions { Enabled = true, Seams = [DecisionSeams.AutoPlan] });
        Assert.Null(await service.DecideAsync(DecisionSeams.RiskGate, new DecisionState().Add("a", "b"), [new Noul("k", "?")]));
        Assert.NotNull(await service.DecideAsync(DecisionSeams.AutoPlan, new DecisionState().Add("a", "b"), [new Noul("k", "?")]));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task NoOpModel_IsNeverAvailable()
    {
        var service = Service(NoOpDecisionModel.Instance, new DecisionOptions { Enabled = true, Seams = ["*"] });
        Assert.False(service.IsEnabled(DecisionSeams.AutoPlan));
        Assert.Null(await service.DecideAsync(DecisionSeams.AutoPlan, new DecisionState().Add("a", "b"), [new Noul("k", "?")]));
    }

    [Fact]
    public async Task ADecision_IsDurableAsAnIgnorableEvent()
    {
        await using var harness = TestHarness.Create();
        var agent = harness.CreateAgent();
        var service = Service(ScriptedDecisionModel.Noul("needs_plan", 0.87));

        await service.DecideAsync(DecisionSeams.AutoPlan, new DecisionState().Add("brief", "do the thing"),
            [new Noul("needs_plan", "?")], agent.Session);

        var logged = agent.Session.Events.Single(e => e.Type == DecisionService.EventType);
        Assert.True(logged.Ignorable); // a plugin event: unknown readers may skip it safely
        Assert.False(SessionEventTypes.KnownTypes.Contains(DecisionService.EventType));
        var payload = SessionJson.FromElement<DecisionEventPayload>(logged.Data);
        Assert.Equal(DecisionSeams.AutoPlan, payload.Seam);
        Assert.Equal(0.87, payload.Answers["needs_plan"].Probability!.Value, 3);
        Assert.Equal(16, payload.StateHash.Length); // short hex digest of exactly what was shown
        Assert.Matches("^[0-9a-f]{16}$", payload.StateHash);
    }

    [Fact]
    public async Task IdenticalStateWithinTheTtl_IsServedFromCache()
    {
        var model = ScriptedDecisionModel.Noul("k", 0.5);
        var service = Service(model, new DecisionOptions { Enabled = true, Seams = ["*"], CacheSeconds = 60 });
        var state = new DecisionState().Add("brief", "same brief");
        var questions = new DecisionQuestion[] { new Noul("k", "?") };

        Assert.NotNull(await service.DecideAsync("s", state, questions));
        Assert.NotNull(await service.DecideAsync("s", state, questions));
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, service.Stats().CacheHits);
    }

    [Fact]
    public async Task AChangedState_DoesNotHitTheCache()
    {
        var model = ScriptedDecisionModel.Noul("k", 0.5);
        var service = Service(model, new DecisionOptions { Enabled = true, Seams = ["*"], CacheSeconds = 60 });
        var questions = new DecisionQuestion[] { new Noul("k", "?") };

        await service.DecideAsync("s", new DecisionState().Add("brief", "one"), questions);
        await service.DecideAsync("s", new DecisionState().Add("brief", "two"), questions);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task AModelThatThrows_BecomesNoOpinion()
    {
        var service = Service(new ThrowingModel());
        var result = await service.DecideAsync("s", new DecisionState().Add("a", "b"), [new Noul("k", "?")]);
        Assert.Null(result); // an empty answer set is not an opinion
        Assert.Equal(1, service.Stats().Degraded);
    }

    private sealed class ThrowingModel : IDecisionModel
    {
        public string Impl => "throws";
        public bool Available => true;
        public Task<DecisionResult?> DecideAsync(DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}

/// <summary>
/// The escalate-only invariant. A calibrated model that is wrong, slow or offline may only ever
/// make the harness more careful, never less — this is what makes the gate safe to leave enabled.
/// </summary>
public class RiskGateTests
{
    /// <summary>A tool whose presented kind is controllable, so the pre-filter can be tested.</summary>
    private sealed class KindTool : ToolDefinition<JsonElement, string>
    {
        private readonly string _kind;
        public KindTool(string name, string kind) { Name = name; _kind = kind; }
        public override string Name { get; }
        public override string Description => "test tool";
        public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(properties: new Dictionary<string, JsonSchema.Schema>());
        public override JsonSchema.Schema Output { get; } = JsonSchema.String();
        protected override Task<string> ExecuteTyped(JsonElement args, ToolRunContext exec) => Task.FromResult("ok");
        protected override IReadOnlyList<ContentBlock> RenderTyped(JsonElement args, string value) => [new TextBlock(value)];
        protected override ToolCallView? PresentCallTyped(JsonElement args) => new() { Kind = _kind, Title = Name };
    }

    private static (TestHarness Harness, Agent Agent, RiskGatePlugin Gate) Boot(
        IDecisionModel model, double threshold = 0.5, bool withAnswerer = true)
    {
        var harness = TestHarness.Create();
        var approval = ApprovalService.Mount(harness.Ctx);
        ToolPolicyService.Mount(harness.Ctx); // satisfies the gate's Inject contract
        var decisions = DecisionService.Mount(harness.Ctx, model,
            new DecisionOptions { Enabled = true, Seams = [DecisionSeams.RiskGate] });
        if (withAnswerer)
            approval.SetAnswerer((_, _) => Task.FromResult(ApprovalOutcome.AllowedOnce));
        var gate = new RiskGatePlugin(decisions, new RiskGateOptions { Threshold = threshold });
        return (harness, harness.CreateAgent(), gate);
    }

    private static ToolExecution Call(Agent agent, ToolDefinition tool, string argsJson)
        => new(new ToolExecutionInput
        {
            Name = tool.Name,
            Arguments = JsonDocument.Parse(argsJson).RootElement,
            CallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            Agent = agent,
        }, tool, agent.ScopeKey);

    [Fact]
    public async Task AConfidentRiskScore_EscalatesAnAllowedCallToAsk()
    {
        var (harness, agent, gate) = await BootAsync(ScriptedDecisionModel.Noul(RiskGatePlugin.RiskKey, 0.94));
        await using (harness)
        {
            var tool = new KindTool("bash", "execute");
            var escalated = await gate.EvaluateAsync(harness.Ctx, Call(agent, tool, """{"command":"git push --force"}"""), default);
            Assert.Equal(PreToolDecision.Ask, escalated!.Kind);
            Assert.Contains("94%", escalated.Reason);
        }
    }

    [Fact]
    public async Task ALowRiskScore_LeavesTheDecisionAlone()
    {
        var (harness, agent, gate) = await BootAsync(ScriptedDecisionModel.Noul(RiskGatePlugin.RiskKey, 0.02));
        await using (harness)
        {
            var escalated = await gate.EvaluateAsync(harness.Ctx,
                Call(agent, new KindTool("bash", "execute"), """{"command":"ls"}"""), default);
            Assert.Null(escalated);
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("search")]
    [InlineData("fetch")]
    [InlineData("other")]
    public async Task NonDestructiveKinds_NeverReachTheModel(string kind)
    {
        var model = ScriptedDecisionModel.AllYes();
        var (harness, agent, gate) = await BootAsync(model);
        await using (harness)
        {
            var result = await gate.EvaluateAsync(harness.Ctx,
                Call(agent, new KindTool("read", kind), """{"path":"a.txt"}"""), default);
            Assert.Null(result);
            Assert.Equal(0, model.Calls); // the pre-filter is what keeps this off the hot path
        }
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("delete")]
    public async Task DestructiveKinds_DoReachTheModel(string kind)
    {
        var model = ScriptedDecisionModel.Noul(RiskGatePlugin.RiskKey, 0.9);
        var (harness, agent, gate) = await BootAsync(model);
        await using (harness)
        {
            var result = await gate.EvaluateAsync(harness.Ctx,
                Call(agent, new KindTool(kind, kind), "{}"), default);
            Assert.Equal(PreToolDecision.Ask, result!.Kind);
            Assert.Equal(1, model.Calls);
        }
    }

    [Fact]
    public async Task WithNobodyToAnswer_StandsDownInsteadOfBreakingHeadlessRuns()
    {
        // An escalation with no answerer fails closed to a denial, so the gate must not escalate.
        var (harness, agent, gate) = await BootAsync(ScriptedDecisionModel.AllYes(), withAnswerer: false);
        await using (harness)
        {
            var result = await gate.EvaluateAsync(harness.Ctx,
                Call(agent, new KindTool("bash", "execute"), """{"command":"rm -rf /"}"""), default);
            Assert.Null(result);
        }
    }

    [Fact]
    public async Task ADenialIsNeverSoftened_AndTheModelIsNeverConsulted()
    {
        // The escalate-only invariant, driven through the real waterfall: an outer middleware
        // denies, so the gate must neither soften it to allow nor spend a call second-guessing it.
        var model = ScriptedDecisionModel.AllYes();
        var harness = TestHarness.Create();
        ApprovalService.Mount(harness.Ctx);
        ToolPolicyService.Mount(harness.Ctx);
        var decisions = DecisionService.Mount(harness.Ctx, model,
            new DecisionOptions { Enabled = true, Seams = [DecisionSeams.RiskGate] });
        harness.Ctx.OnWaterfall<ToolExecution, PreToolDecision, PreToolDecision>(
            "tools/pre-execute", (_, _, next, _) => next(PreToolDecision.Denied("outer policy")));
        var gate = new RiskGatePlugin(decisions);
        gate.Apply(harness.Ctx);

        await using (harness)
        {
            var agent = harness.CreateAgent();
            var tool = new KindTool("probe-deny", "execute");
            harness.Tools.Register(tool);
            var result = await harness.Tools.Execute(Call(agent, tool, "{}").Input);
            Assert.True(result.IsError);
            Assert.Contains("outer policy", result.Error!.Message);
            Assert.Equal(0, model.Calls); // a non-allow never reaches the model
        }
    }

    [Fact]
    public async Task RegisteredOnTheBus_AnAllowedRiskyCallIsParkedForApproval()
    {
        var model = ScriptedDecisionModel.Noul(RiskGatePlugin.RiskKey, 0.93);
        var harness = TestHarness.Create();
        ApprovalService.Mount(harness.Ctx);
        ToolPolicyService.Mount(harness.Ctx);
        var decisions = DecisionService.Mount(harness.Ctx, model,
            new DecisionOptions { Enabled = true, Seams = [DecisionSeams.RiskGate] });
        var reasons = new List<string?>();
        harness.Ctx.Get<ApprovalService>(ApprovalService.ServiceKey).SetAnswerer((request, _) =>
        {
            reasons.Add(request.Reason);
            return Task.FromResult(ApprovalOutcome.Rejected);
        });
        var gate = new RiskGatePlugin(decisions, new RiskGateOptions { Threshold = 0.5 });
        gate.Apply(harness.Ctx);

        await using (harness)
        {
            var agent = harness.CreateAgent();
            var tool = new KindTool("probe-risky", "execute");
            harness.Tools.Register(tool);
            var result = await harness.Tools.Execute(Call(agent, tool, """{"command":"rm -rf /"}""").Input);
            Assert.True(result.IsError); // the reviewer rejected it, so it must not have run
            Assert.Single(reasons);
            Assert.Contains("93%", reasons[0]);
            Assert.Contains("risk model", reasons[0]);
        }
    }

    [Fact]
    public async Task TheGateRunsInsideToolPolicy_SoAskEveryToolStillWins()
    {
        var model = ScriptedDecisionModel.Noul(RiskGatePlugin.RiskKey, 0.99);
        var harness = TestHarness.Create();
        ApprovalService.Mount(harness.Ctx);
        var policy = ToolPolicyService.Mount(harness.Ctx);
        var decisions = DecisionService.Mount(harness.Ctx, model,
            new DecisionOptions { Enabled = true, Seams = [DecisionSeams.RiskGate] });
        var asked = new List<string>();
        harness.Ctx.Get<ApprovalService>(ApprovalService.ServiceKey).SetAnswerer((request, _) =>
        {
            asked.Add(request.Reason ?? "");
            return Task.FromResult(ApprovalOutcome.Rejected);
        });
        var gate = new RiskGatePlugin(decisions);
        gate.Apply(harness.Ctx);
        await using (harness)
        {
            var agent = harness.CreateAgent();
            policy.SetAskEveryTool(agent.Id, true);
            var tool = new KindTool("probe-exec", "execute");
            harness.Tools.Register(tool);
            await harness.Tools.Execute(Call(agent, tool, "{}").Input);
            // ask-every-tool short-circuits before the gate, so the gate's own reason never appears.
            Assert.Single(asked);
            Assert.Equal("the session's permission mode is ask", asked[0]);
            Assert.Equal(0, model.Calls);
        }
    }

    /// <summary>Boot helper: the approval seam needs an answerer for escalation to be meaningful.</summary>
    private static Task<(TestHarness, Agent, RiskGatePlugin)> BootAsync(IDecisionModel model, bool withAnswerer = true)
    {
        var (harness, agent, gate) = Boot(model, withAnswerer: withAnswerer);
        return Task.FromResult((harness, agent, gate));
    }
}

/// <summary>
/// Auto-plan with a decision model: the structural guards still run first, the model replaces only
/// the complexity judgment, and an abstention falls back to the deterministic scorer.
/// </summary>
public class AutoPlanDecisionTests
{
    /// <summary>The same brief the heuristic tests use, so both paths are compared on one input.</summary>
    private const string Complex = AutoPlanBriefs.Complex;

    private static (TestHarness Harness, Agent Agent, AutoPlanPlugin Plugin) Boot(IDecisionModel? model)
    {
        var harness = TestHarness.Create();
        harness.Ctx.Provide(PlanModeService.ServiceKey, new PlanModeService());
        var decisions = DecisionService.Mount(harness.Ctx, model ?? NoOpDecisionModel.Instance,
            new DecisionOptions { Enabled = model is not null, Seams = [DecisionSeams.AutoPlan] });
        var plugin = new AutoPlanPlugin(55, decisions,
            new AutoPlanDecisionOptions { EngageAt = 0.60, SkipAt = 0.25 });
        return (harness, harness.CreateAgent(), plugin);
    }

    [Fact]
    public async Task NoModel_UsesTheDeterministicScorerExactlyAsBefore()
    {
        var (harness, agent, plugin) = Boot(null);
        await using (harness)
        {
            var engaged = await plugin.ShouldEngageAsync(agent.Session, [Message.CreateUserText(Complex)], default);
            Assert.NotNull(engaged);
            Assert.Contains(engaged!.Reasons, r => !r.StartsWith("decision model"));

            var trivial = await plugin.ShouldEngageAsync(agent.Session, [Message.CreateUserText("what does this do?")], default);
            Assert.Null(trivial);
        }
    }

    [Fact]
    public async Task AConfidentYes_EngagesWithTheProbabilityAsTheReason()
    {
        var (harness, agent, plugin) = Boot(ScriptedDecisionModel.Noul(AutoPlanPlugin.PlanKey, 0.91));
        await using (harness)
        {
            // A brief the heuristic would score low, but the model is confident about.
            var score = await plugin.ShouldEngageAsync(agent.Session, [Message.CreateUserText("tidy this up")], default);
            Assert.NotNull(score);
            Assert.Equal(91, score!.Total);
            Assert.Contains("decision model: P(needs plan) = 91%", score.Reasons);
        }
    }

    [Fact]
    public async Task AConfidentNo_SkipsEvenWhenTheHeuristicWouldEngage()
    {
        var (harness, agent, plugin) = Boot(ScriptedDecisionModel.Noul(AutoPlanPlugin.PlanKey, 0.03));
        await using (harness)
        {
            Assert.True(AutoPlanPolicy.ShouldEngage(agent.Session, [Message.CreateUserText(Complex)], 55, out _));
            Assert.Null(await plugin.ShouldEngageAsync(agent.Session, [Message.CreateUserText(Complex)], default));
        }
    }

    [Fact]
    public async Task AnUncertainProbability_AbstainsToTheHeuristic()
    {
        var model = ScriptedDecisionModel.Noul(AutoPlanPlugin.PlanKey, 0.42); // inside (0.25, 0.60)
        var (harness, agent, plugin) = Boot(model);
        await using (harness)
        {
            var score = await plugin.ShouldEngageAsync(agent.Session, [Message.CreateUserText(Complex)], default);
            Assert.NotNull(score);
            Assert.DoesNotContain(score!.Reasons, r => r.StartsWith("decision model"));
            Assert.Equal(1, model.Calls);
        }
    }

    [Fact]
    public async Task ExemptTurns_NeverReachTheModel()
    {
        var model = ScriptedDecisionModel.AllYes();
        var (harness, agent, plugin) = Boot(model);
        await using (harness)
        {
            agent.Session.Append(SessionEventTypes.SubagentDescriptor, new { }, new Session.AppendOptions(Ignorable: true));
            Assert.Null(await plugin.ShouldEngageAsync(agent.Session, [Message.CreateUserText(Complex)], default));
            Assert.Equal(0, model.Calls); // structural guards are free, so they run first
        }
    }
}
