using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Guards;
using Blazorly.Harness.Core.Mcp;
using Blazorly.Harness.Core.Schedule;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Spill;
using Blazorly.Harness.Core.TokenMeter;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

public class SpillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-spill-" + Guid.NewGuid().ToString("N")[..8]);

    public SpillTests() => Directory.CreateDirectory(_root);

    private sealed class BigTool : ToolDefinition<BigTool.Args, string>
    {
        public sealed record Args(string Label);

        public override string Name => "big_out";
        public override string Description => "renders a large result";
        public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema.Schema> { ["label"] = JsonSchema.String() },
            required: ["label"]);
        public override JsonSchema.Schema Output { get; } = JsonSchema.String();
        protected override Task<string> ExecuteTyped(Args args, ToolRunContext exec)
            => Task.FromResult($"HEAD-{args.Label}-" + new string('x', 50_000) + $"-TAIL-{args.Label}");
        protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, string value) => [new TextBlock(value)];
    }

    private static ToolExecutionInput Input(Agent agent, object args) => new()
    {
        Name = "big_out",
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = "call_" + Guid.NewGuid().ToString("N")[..6],
        Signal = CancellationToken.None,
        Agent = agent,
    };

    [Fact]
    public async Task OversizeOutput_IsSpilledWithPreviewAndRetrievable()
    {
        await using var harness = TestHarness.Create();
        var spills = SpillService.Mount(harness.Ctx, _root, new SpillOptions { ThresholdChars = 20_000, HeadChars = 200, TailChars = 50 });
        _ = harness.Tools.Register(new BigTool());
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(Input(agent, new { label = "A" }));
        Assert.False(result.IsError);
        var content = Assert.IsType<TextBlock>(result.Content.Single()).Text;
        Assert.Contains("spilled as 'spill_1'", content);                    // locator note
        Assert.Contains("HEAD-A-", content);                                 // head preview
        Assert.EndsWith("TAIL-A", content);                                  // tail preview
        Assert.True(content.Length < 2_000);                                 // bounded preview
        Assert.Equal(50_014, spills.Describe("spill_1")!.Chars);              // full text stored

        // spill_read retrieves windows.
        var window = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "spill_read",
            Arguments = JsonSerializer.SerializeToElement(new { spill_id = "spill_1", offset = 0, max_chars = 100 }),
            CallId = "call_read_1",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.False(window.IsError);
        var text = Assert.IsType<TextBlock>(window.Content.Single()).Text;
        Assert.Contains("HEAD-A-", text);
        Assert.Contains("remaining_chars:", text);
    }

    [Fact]
    public async Task UnderThresholdOutput_IsNotSpilled()
    {
        await using var harness = TestHarness.Create();
        // Threshold above the tool's 50k render: nothing spills, content stays inline.
        SpillService.Mount(harness.Ctx, _root, new SpillOptions { ThresholdChars = 100_000 });
        _ = harness.Tools.Register(new BigTool());
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "big_out",
            Arguments = JsonSerializer.SerializeToElement(new { label = "tiny" }),
            CallId = "call_small_1",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(result.Content.Single()).Text;
        Assert.True(text.Length > 1_000); // untouched, still inline
        Assert.DoesNotContain("spilled as", text);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class RepeatGuardTests
{
    private static ToolExecutionInput Input(Agent agent, string label) => new()
    {
        Name = "probe",
        Arguments = JsonSerializer.SerializeToElement(new { label }),
        CallId = "call_" + Guid.NewGuid().ToString("N")[..6],
        Signal = CancellationToken.None,
        Agent = agent,
    };

    [Fact]
    public async Task IdenticalCalls_BeyondThreshold_InjectReminderOncePerExtension()
    {
        await using var harness = TestHarness.Create();
        var guard = RepeatCallGuard.Mount(harness.Ctx, new RepeatGuardOptions { Threshold = 3 });
        var agent = harness.CreateAgent();

        guard.Observe(Input(agent, "same"));
        guard.Observe(Input(agent, "same"));
        Assert.Empty(agent.Inbox.NextStep);

        guard.Observe(Input(agent, "same")); // streak 3 → remind
        Assert.Single(agent.Inbox.NextStep);
        guard.Observe(Input(agent, "same")); // streak 4 → remind again
        Assert.Equal(2, agent.Inbox.NextStep.Count);

        guard.Observe(Input(agent, "different")); // streak reset, no reminder
        Assert.Equal(2, agent.Inbox.NextStep.Count);
        guard.Observe(Input(agent, "different"));
        guard.Observe(Input(agent, "different"));
        Assert.Equal(3, agent.Inbox.NextStep.Count);
        Assert.Contains("[reminder]", agent.Inbox.NextStep[^1].FlattenText());
    }
}

public class ToolTimeoutPolicyTests
{
    [Fact]
    public async Task DefaultTimeout_AppliesWhenDefinitionHasNone()
    {
        await using var harness = TestHarness.Create();
        harness.Tools.DefaultToolTimeoutMs = 200;
        _ = harness.Tools.Register(new ProbeTool());
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "probe",
            Arguments = JsonSerializer.SerializeToElement(new { value = "slow", delayMs = 5_000 }),
            CallId = "call_to_1",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.ToolTimeout, result.Error!.Info!.Code);
    }

    private sealed class PatientTool : ToolDefinition<PatientTool.Args, string>
    {
        public sealed record Args(string Label);
        public override string Name => "patient";
        public override string Description => "waits a bit";
        public override int? TimeoutMs => 10_000; // definition wins over the default
        public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema.Schema> { ["label"] = JsonSchema.String() },
            required: ["label"]);
        public override JsonSchema.Schema Output { get; } = JsonSchema.String();
        protected override async Task<string> ExecuteTyped(Args args, ToolRunContext exec)
        {
            await Task.Delay(300, exec.Signal);
            return $"done:{args.Label}";
        }
        protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, string value) => [new TextBlock(value)];
    }

    [Fact]
    public async Task DefinitionTimeout_WinsOverDefault()
    {
        await using var harness = TestHarness.Create();
        harness.Tools.DefaultToolTimeoutMs = 100;
        _ = harness.Tools.Register(new PatientTool());
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "patient",
            Arguments = JsonSerializer.SerializeToElement(new { label = "ok" }),
            CallId = "call_to_2",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.False(result.IsError);
        Assert.Contains("done:ok", Assert.IsType<TextBlock>(result.Content.Single()).Text);
    }
}

public class TokenMeterTests
{
    [Fact]
    public async Task Measure_ReportsPressureProviderAnchorTotalsAndBreakdown()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("ok"));
        var meter = TokenMeterService.Mount(harness.Ctx);
        meter.ContextWindowTokens = 10_000;
        var agent = harness.CreateAgent();

        agent.Session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        agent.Session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        var message = Llm.Message.CreateAssistant("replay", "demo", [new TextBlock("working…")]);
        agent.Session.Append(SessionEventTypes.AssistantMessage,
            new SessionPayloads.AssistantMessage(1, 1, message, new TokenUsage(InputTokens: 120, OutputTokens: 40, CacheReadTokens: 30, CacheWriteTokens: 10)),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        agent.Session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(1, 1));
        agent.Session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));

        var reading = meter.Measure(agent);

        Assert.Equal(reading.SystemTokens + reading.ToolsTokens + reading.MessageTokens, reading.PressureTokens);
        Assert.Equal(160, reading.ProviderPressureTokens); // 120 + 30 + 10
        Assert.Equal(120, reading.TotalInputTokens);
        Assert.Equal(40, reading.TotalOutputTokens);
        Assert.Equal(30, reading.TotalCacheReadTokens);
        Assert.Equal(10_000, reading.ContextWindowTokens);
        Assert.Equal((int)Math.Min(100, reading.PressureTokens * 100 / 10_000), reading.OccupancyPercent);
        Assert.True(reading.OccupancyPercent is > 0 and < 100);
    }
}

public class ScheduleTests : IAsyncDisposable, IDisposable
{
    private readonly TestHarness _harness = TestHarness.Create(_ => ReplayScript.Text("done"));
    private readonly ScheduleService _schedules;
    private readonly Agent _agent;

    public ScheduleTests()
    {
        _schedules = ScheduleService.Mount(_harness.Ctx, new ScheduleOptions { MinEverySeconds = 1, TickMs = 50 });
        _agent = _harness.CreateAgent();
    }

    [Fact]
    public async Task AfterSeconds_DeliversAsFollowupTurnWhenIdle()
    {
        _schedules.Create(_agent.Session.Id, "check the deploy", afterSeconds: 1, at: null, everySeconds: null);
        Assert.Single(_schedules.List(_agent.Session.Id));

        // The delivery loop claims the idle agent and runs the reminder as a real turn.
        for (var i = 0; i < 100 && _agent.Session.Events.All(e => e.Type != SessionEventTypes.TurnEnd); i++)
        {
            await Task.Delay(100);
        }
        Assert.Contains(_agent.Session.Events, e => e.Type == SessionEventTypes.UserMessage
            && SessionEventRead.MessageOf(e).FlattenText().Contains("Scheduled reminder: check the deploy"));
        Assert.Contains(_agent.Session.Events, e => e.Type == SessionEventTypes.ScheduleChange
            && e.Data.GetProperty("action").GetString() == "deliver");
        Assert.True(_schedules.List(_agent.Session.Id).Single(r => r.Text == "check the deploy").Done);
    }

    [Fact]
    public async Task DeletedSchedules_AreNotDelivered()
    {
        var record = _schedules.Create(_agent.Session.Id, "never fires", afterSeconds: 1, at: null, everySeconds: null);
        _schedules.Delete(_agent.Session.Id, record.Id);
        Assert.DoesNotContain(_schedules.List(_agent.Session.Id), r => r.Id == record.Id);

        await Task.Delay(1_500);
        Assert.DoesNotContain(_agent.Session.Events, e => e.Type == SessionEventTypes.UserMessage
            && SessionEventRead.MessageOf(e).FlattenText().Contains("never fires"));
    }

    [Fact]
    public void ForkedSessions_DoNotInheritParentSchedules()
    {
        _schedules.Create(_agent.Session.Id, "parent reminder", afterSeconds: 3_600, at: null, everySeconds: null);
        var child = _harness.Sessions.Fork(_agent.Session.Id);
        Assert.Empty(_schedules.Fold(child).Records);
    }

    [Fact]
    public void EverySeconds_BelowFloor_IsRejected()
    {
        Assert.Throws<ToolException>(() =>
            _schedules.Create(_agent.Session.Id, "too fast", afterSeconds: null, at: null, everySeconds: 0));
    }

    public async ValueTask DisposeAsync() => await _schedules.DisposeAsync();
    public void Dispose() => _harness.Ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

public class McpClientTests : IAsyncDisposable
{
    private const string FakeServer = """
        import sys, json

        def send(obj):
            sys.stdout.write(json.dumps(obj) + "\n")
            sys.stdout.flush()

        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            msg = json.loads(line)
            method = msg.get("method", "")
            if method == "initialize":
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {
                    "protocolVersion": "2024-11-05", "capabilities": {"tools": {}},
                    "serverInfo": {"name": "fake", "version": "0.0.1"}}})
            elif method == "notifications/initialized":
                pass
            elif method == "tools/list":
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {"tools": [{
                    "name": "echo",
                    "description": "Echo the given text.",
                    "inputSchema": {"type": "object",
                                    "properties": {"text": {"type": "string", "description": "text to echo"}},
                                    "required": ["text"]}}]}})
            elif method == "tools/call":
                args = msg["params"]["arguments"]
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {
                    "content": [{"type": "text", "text": "echo: " + args.get("text", "")}],
                    "isError": False}})
        """;

    private static async Task<(string Config, string Dir)> WriteServerConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blazorly-mcp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "fake_server.py");
        await File.WriteAllTextAsync(script, FakeServer);
        var config = Path.Combine(dir, "mcp.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            servers = new[] { new { name = "testsrv", command = "python3", args = new[] { script } } },
        }));
        return (config, dir);
    }

    [Fact]
    public async Task StdioServer_RegistersToolsAndAnswersCalls()
    {
        var (config, dir) = await WriteServerConfig();

        await using var harness = TestHarness.Create();
        var mcp = McpClientService.Mount(harness.Ctx, new McpOptions { ConfigPath = config, ControlTimeoutMs = 5_000 });
        try
        {
            for (var i = 0; i < 50 && harness.Tools.Get("mcp__testsrv__echo") is null; i++)
            {
                await Task.Delay(100);
            }
            var tool = harness.Tools.Get("mcp__testsrv__echo");
            Assert.NotNull(tool);
            Assert.Contains("Echo the given text.", tool!.Description);

            // Raw schema passthrough reaches the model-facing schemas.
            var schema = harness.Tools.Schemas().Single(s => s.Name == "mcp__testsrv__echo");
            Assert.Contains("\"text\"", schema.Parameters.GetRawText());

            var agent = harness.CreateAgent();
            var result = await harness.Tools.Execute(new ToolExecutionInput
            {
                Name = "mcp__testsrv__echo",
                Arguments = JsonSerializer.SerializeToElement(new { text = "hello mcp" }),
                CallId = "call_mcp_1",
                Signal = CancellationToken.None,
                Agent = agent,
            });
            Assert.False(result.IsError);
            Assert.Contains("echo: hello mcp", Assert.IsType<TextBlock>(result.Content.Single()).Text);
        }
        finally
        {
            await mcp.DisposeAsync();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync() => await Task.CompletedTask;
}
