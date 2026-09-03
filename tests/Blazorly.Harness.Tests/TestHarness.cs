using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Tools;

namespace Blazorly.Harness.Tests;

/// <summary>A canned harness composition: kernel services + replay LLM + built-in tools.</summary>
public sealed class TestHarness : IAsyncDisposable
{
    public HarnessContext Ctx { get; } = HarnessContext.CreateRoot();
    public LlmRuntime Llm { get; private set; } = null!;
    public SessionStore Sessions { get; private set; } = null!;
    public ToolRuntime Tools { get; private set; } = null!;
    public SystemPromptService Prompt { get; private set; } = null!;
    public AgentRuntime Agents { get; private set; } = null!;
    public AgentLoopService Loop { get; private set; } = null!;
    public ScriptedLlmAdapter ScriptedLlm { get; private set; } = null!;
    public FsObservationTracker Tracker { get; } = new();
    public SandboxPolicy Sandbox { get; } = new() { DefaultMode = SandboxPolicy.DangerFullAccess };

    public static TestHarness Create(Func<GenerateOptions, IReadOnlyList<StreamChunk>>? script = null, string? cwd = null, Blazorly.Harness.Core.Sessions.ISessionPersistence? persistence = null)
    {
        var harness = new TestHarness();
        harness.Llm = LlmRuntime.Mount(harness.Ctx);
        harness.ScriptedLlm = new ScriptedLlmAdapter(script ?? (_ => Scripted.Text("(no script)")));
        harness.Llm.RegisterAdapter(harness.ScriptedLlm);
        harness.Prompt = SystemPromptService.Mount(harness.Ctx);
        harness.Tools = ToolRuntime.Mount(harness.Ctx, harness.Prompt);
        harness.Sessions = SessionStore.Mount(harness.Ctx, persistence);
        harness.Agents = AgentRuntime.Mount(harness.Ctx);
        harness.Loop = AgentLoopService.Mount(harness.Ctx);
        harness.Loop.RegisterDefaultPrompt();
        new BuiltInToolsPlugin(harness.Tracker, harness.Sandbox).Apply(harness.Ctx);
        harness.Loop.DefaultSelection = new LlmCallConfig { Provider = "scripted", Model = "test" };
        return harness;
    }

    public Agent CreateAgent(string? cwd = null, AgentOptions? options = null)
        => Loop.Create(new SessionMeta(cwd ?? Directory.GetCurrentDirectory()), options);

    public async ValueTask DisposeAsync() => await Ctx.DisposeAsync().ConfigureAwait(false);
}

/// <summary>An echo tool for scheduler tests: sleeps briefly, returns its argument.</summary>
public sealed class ProbeTool : ToolDefinition<ProbeTool.Args, string>
{
    public sealed record Args(string Value, int DelayMs = 0, bool Fail = false);

    public override string Name { get; }
    public override string Description => "test probe";

    public List<string> CallLog { get; } = [];
    public List<int> CommitLog { get; } = [];
    public Func<Args, bool>? SafeClassifier { get; set; }
    public event Action<string>? BodyStarted;

    public ProbeTool(string name = "probe")
    {
        Name = name;
    }

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["value"] = JsonSchema.String(),
            ["delayMs"] = JsonSchema.Integer(),
            ["fail"] = JsonSchema.Boolean(),
        },
        required: ["value"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.String();

    protected override async Task<string> ExecuteTyped(Args args, ToolRunContext exec)
    {
        BodyStarted?.Invoke(args.Value);
        CallLog.Add(args.Value);
        if (args.DelayMs > 0) await Task.Delay(args.DelayMs, exec.Signal).ConfigureAwait(false);
        if (args.Fail) throw new ToolException("PROBE_FAIL", $"probe failure for {args.Value}");
        return $"echo:{args.Value}";
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, string value) => [new TextBlock(value)];

    protected override bool IsConcurrencySafeTyped(Args args) => SafeClassifier?.Invoke(args) ?? false;
}
