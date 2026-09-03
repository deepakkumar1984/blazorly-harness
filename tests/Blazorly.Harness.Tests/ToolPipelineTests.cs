using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

public class ToolPipelineTests
{
    private static async Task<TestHarness> HarnessWithProbe(string name = "probe")
    {
        var harness = await Task.FromResult(TestHarness.Create());
        harness.Tools.Register(new ProbeTool(name));
        return harness;
    }

    private static ToolExecutionInput Input(JsonElement args, Agent? agent = null, string? name = "probe")
        => new()
        {
            Name = name ?? "probe",
            Arguments = args,
            CallId = "call_test",
            Signal = CancellationToken.None,
            Agent = agent,
        };

    private static JsonElement Args(string value) => JsonSerializer.SerializeToElement(new { value });

    [Fact]
    public async Task Execute_RunsBodyThroughOutputContract()
    {
        await using var harness = await HarnessWithProbe();
        var result = await harness.Tools.Execute(Input(Args("hi")));
        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(result.Content.Single());
        Assert.Equal("echo:hi", text.Text);
    }

    [Fact]
    public async Task UnknownTool_FailsClosed()
    {
        await using var harness = await HarnessWithProbe();
        var result = await harness.Tools.Execute(Input(Args("hi"), name: "nope"));
        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.UnknownTool, result.Error!.Info!.Code);
    }

    [Fact]
    public async Task InvalidArgs_RejectedBeforeBody()
    {
        await using var harness = await HarnessWithProbe();
        var bad = JsonSerializer.SerializeToElement(new { wrong = "shape" });
        var result = await harness.Tools.Execute(Input(bad));
        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.InvalidArgs, result.Error!.Info!.Code);
    }

    [Fact]
    public async Task PreExecuteDenial_SkipsBody_StillRunsPostExecute()
    {
        await using var harness = await HarnessWithProbe();
        var probe = (ProbeTool)harness.Tools.Get("probe")!;
        var postSaw = new List<string>();
        harness.Ctx.Events.OnWaterfall<ToolExecution, PreToolDecision, PreToolDecision>(
            "tools/pre-execute", (_, _, next, _) => Task.FromResult(PreToolDecision.Denied("policy says no")));
        harness.Ctx.Events.OnWaterfall<ToolPostExecute, PostToolDecision, PostToolDecision>(
            "tools/post-execute", (payload, _, next, _) => { postSaw.Add(payload.Execution.Input.Name); return next(PostToolDecision.Accept()); });

        var result = await harness.Tools.Execute(Input(Args("hi")));
        Assert.True(result.IsError);
        Assert.Contains("policy says no", Assert.IsType<TextBlock>(result.Content.Single()).Text);
        Assert.Empty(probe.CallLog);
        Assert.Equal(["probe"], postSaw);
    }

    [Fact]
    public async Task AskWithoutApprovalService_FailsClosed()
    {
        await using var harness = await HarnessWithProbe();
        harness.Ctx.Events.OnWaterfall<ToolExecution, PreToolDecision, PreToolDecision>(
            "tools/pre-execute", (_, _, next, _) => Task.FromResult(PreToolDecision.Asked("needs permission")));
        var result = await harness.Tools.Execute(Input(Args("hi")));
        Assert.True(result.IsError);
        Assert.Contains("approval required", result.Error!.Message);
    }

    [Fact]
    public async Task ApprovalAllowedOnce_Proceeds_RejectedDenies()
    {
        await using var harness = await HarnessWithProbe();
        var agent = harness.CreateAgent();
        var approval = Core.ApprovalService.Mount(harness.Ctx);
        harness.Ctx.Events.OnWaterfall<ToolExecution, PreToolDecision, PreToolDecision>(
            "tools/pre-execute", (_, _, next, _) => Task.FromResult(PreToolDecision.Asked("needs permission")));

        approval.SetAnswerer((_, _) => Task.FromResult(Core.ApprovalOutcome.AllowedOnce));
        var allowed = await harness.Tools.Execute(Input(Args("hi"), agent));
        Assert.False(allowed.IsError);

        approval.SetAnswerer((_, _) => Task.FromResult(Core.ApprovalOutcome.Rejected));
        var rejected = await harness.Tools.Execute(Input(Args("hi"), agent));
        Assert.True(rejected.IsError);
        Assert.Contains("rejected", rejected.Error!.Message);
    }

    [Fact]
    public async Task MonotonicGuard_DeniesAndCannotBeReAllowed()
    {
        await using var harness = await HarnessWithProbe();
        harness.Tools.AddGuard(_ => "forbidden zone");
        // A pre-execute allow cannot override the guard.
        harness.Ctx.Events.OnWaterfall<ToolExecution, PreToolDecision, PreToolDecision>(
            "tools/pre-execute", (_, _, next, _) => Task.FromResult(PreToolDecision.Allowed()));
        var result = await harness.Tools.Execute(Input(Args("hi")));
        Assert.True(result.IsError);
        Assert.Contains("forbidden zone", result.Error!.Message);
    }

    [Fact]
    public async Task PostExecute_BlockTurnsResultIntoError()
    {
        await using var harness = await HarnessWithProbe();
        harness.Ctx.Events.OnWaterfall<ToolPostExecute, PostToolDecision, PostToolDecision>(
            "tools/post-execute", (_, _, next, _) =>
            Task.FromResult(PostToolDecision.Block([new TextBlock("Error: fix the thing first")])));
        var result = await harness.Tools.Execute(Input(Args("hi")));
        Assert.True(result.IsError);
        Assert.Equal("Error: fix the thing first", Assert.IsType<TextBlock>(result.Content.Single()).Text);
    }

    [Fact]
    public async Task PostExecute_ValueReplacement_Revalidated()
    {
        await using var harness = await HarnessWithProbe();
        var replace = harness.Ctx.Events.OnWaterfall<ToolPostExecute, PostToolDecision, PostToolDecision>(
            "tools/post-execute", (_, _, next, _) =>
            Task.FromResult(PostToolDecision.AcceptValue(JsonSerializer.SerializeToElement("replaced:ok"))));
        var result = await harness.Tools.Execute(Input(Args("hi")));
        Assert.False(result.IsError);
        Assert.Equal("replaced:ok", Assert.IsType<TextBlock>(result.Content.Single()).Text);
        replace.Dispose();

        var invalidReplace = harness.Ctx.Events.OnWaterfall<ToolPostExecute, PostToolDecision, PostToolDecision>(
            "tools/post-execute", (_, _, next, _) =>
            Task.FromResult(PostToolDecision.AcceptValue(JsonSerializer.SerializeToElement(42))));
        var invalid = await harness.Tools.Execute(Input(Args("hi")));
        Assert.True(invalid.IsError);
        Assert.Equal(ToolErrorCodes.InvalidToolOutput, invalid.Error!.Info!.Code);
    }

    [Fact]
    public async Task OutputContractViolation_BecomesError()
    {
        await using var harness = TestHarness.Create();
        harness.Tools.Register(new BrokenOutputTool());
        var result = await harness.Tools.Execute(Input(JsonSerializer.SerializeToElement(new { }), name: "broken"));
        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.InvalidToolOutput, result.Error!.Info!.Code);
    }

    [Fact]
    public async Task DeclaredTimeout_ProducesToolTimeoutError()
    {
        await using var harness = TestHarness.Create();
        var slow = new SlowTool(2000);
        harness.Tools.Register(slow);
        using var cts = new CancellationTokenSource();
        var input = Input(JsonSerializer.SerializeToElement(new { }), name: "slow") with { Signal = cts.Token };
        var result = await harness.Tools.Execute(input);
        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.ToolTimeout, result.Error!.Info!.Code);
    }

    private sealed class BrokenOutputTool : ToolDefinition<object, int>
    {
        public override string Name => "broken";
        public override string Description => "violates its output contract";
        public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();
        public override JsonSchema.Schema Output { get; } = JsonSchema.String();
        protected override Task<int> ExecuteTyped(object args, ToolRunContext exec) => Task.FromResult(42);
        protected override IReadOnlyList<ContentBlock> RenderTyped(object args, int value) => [new TextBlock(value.ToString())];
    }

    private sealed class SlowTool(int delayMs) : ToolDefinition<SlowTool.Args, string>
    {
        public sealed record Args;

        public override string Name => "slow";
        public override string Description => "always times out";
        public override int? TimeoutMs => 200;
        public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();
        public override JsonSchema.Schema Output { get; } = JsonSchema.String();

        protected override async Task<string> ExecuteTyped(Args args, ToolRunContext exec)
        {
            await Task.Delay(delayMs, exec.Signal).ConfigureAwait(false);
            return "done";
        }

        protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, string value) => [new TextBlock(value)];
    }
}

public class ToolScopingTests
{
    [Fact]
    public async Task ScopedRegistration_ShadowsGlobalAndSurvivesRestriction()
    {
        await using var harness = TestHarness.Create();
        var global = new ProbeTool("probe");
        var scoped = new ProbeTool("probe"); // same name: scoped shadows global
        harness.Tools.Register(global);
        Assert.NotNull(harness.Tools.Get("probe"));

        await using var scope = harness.Ctx.CreateScope(new object());
        harness.Tools.RegisterScoped(scope.Key, scoped);
        Assert.Same(scoped, harness.Tools.Get("probe", scope.Key));
        Assert.Same(global, harness.Tools.Get("probe")); // global view untouched

        // A restriction filtering the global set hides the global tool for the scope, but the
        // scope's own registrations are exempt.
        harness.Tools.Restrict(scope.Key, allow: new HashSet<string>(["read"]));
        Assert.Same(scoped, harness.Tools.Get("probe", scope.Key));

        var schemas = harness.Tools.Schemas(scope.Key);
        Assert.Contains(schemas, s => s.Name == "probe");
        Assert.DoesNotContain(schemas, s => s.Name == "bash"); // restricted global tool absent from the prompt
    }
}

public class SourceSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-grep-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void WriteTree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "first needle here\n");
        File.WriteAllText(Path.Combine(_root, "src", "b.razor"), "second needle here\n");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "third needle here\n");
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"),
            "needle in binary\0\0"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "dep"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "dep", "index.js"), "needle in deps\n");
    }

    private static ToolExecutionInput Input(string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = "call_test",
        Signal = CancellationToken.None,
    };

    private static List<string> Files(JsonElement value)
        => value.GetProperty("matches").EnumerateArray()
            .Select(m => m.GetProperty("file").GetString()!).ToList();

    [Fact]
    public async Task Grep_SkipsBinariesAndBuildDirs()
    {
        WriteTree();
        await using var harness = TestHarness.Create();

        var result = await harness.Tools.Execute(Input("grep", new { pattern = "needle", path = _root }));
        Assert.False(result.IsError);
        var files = Files(result.Value!.Value);
        Assert.Equal(3, files.Count); // a.cs, b.razor, notes.txt — no blob.bin, no node_modules
        Assert.DoesNotContain(files, f => f.EndsWith("blob.bin"));
        Assert.DoesNotContain(files, f => f.Contains("node_modules"));
    }

    [Fact]
    public async Task Grep_MultiIncludeFiltersByExtension()
    {
        WriteTree();
        await using var harness = TestHarness.Create();

        var result = await harness.Tools.Execute(Input("grep", new { pattern = "needle", path = _root, include = "*.cs,*.razor" }));
        Assert.False(result.IsError);
        var files = Files(result.Value!.Value);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.EndsWith("a.cs"));
        Assert.Contains(files, f => f.EndsWith("b.razor"));
    }

    [Fact]
    public async Task Grep_ExplicitBinaryPath_SearchesNothing()
    {
        WriteTree();
        await using var harness = TestHarness.Create();

        var result = await harness.Tools.Execute(Input("grep",
            new { pattern = "needle", path = Path.Combine(_root, "blob.bin") }));
        Assert.False(result.IsError);
        Assert.Empty(Files(result.Value!.Value));
    }
}
