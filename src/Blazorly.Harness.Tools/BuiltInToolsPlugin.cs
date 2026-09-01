using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Tools;

/// <summary>Mounts the built-in tool family: bash, read/write/edit, glob/grep, todo_write.</summary>
public sealed class BuiltInToolsPlugin(
    FsObservationTracker? tracker = null,
    SandboxPolicy? sandbox = null,
    bool registerBash = true,
    bool registerFs = true,
    bool registerSearch = true,
    bool registerTodo = true) : HarnessPlugin
{
    public override string Name => "built-in-tools";
    public override string[] Inject { get; } = ["tools", "systemPrompt"];

    public FsObservationTracker Tracker { get; } = tracker ?? new FsObservationTracker();
    public SandboxPolicy Sandbox { get; } = sandbox ?? new SandboxPolicy();

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        ctx.Provide("fsObservation", Tracker);
        ctx.Provide("sandboxPolicy", Sandbox);

        var tools = ctx.Get<ToolRuntime>("tools");
        if (registerBash) ctx.Effect(tools.Register(new BashTool()).Dispose);
        if (registerFs)
        {
            ctx.Effect(tools.Register(new ReadTool(Tracker)).Dispose);
            ctx.Effect(tools.Register(new WriteTool(Tracker, Sandbox)).Dispose);
            ctx.Effect(tools.Register(new EditTool(Tracker, Sandbox)).Dispose);
        }
        if (registerSearch)
        {
            ctx.Effect(tools.Register(new GlobTool()).Dispose);
            ctx.Effect(tools.Register(new GrepTool()).Dispose);
        }
        if (registerTodo) ctx.Effect(tools.Register(new TodoWriteTool()).Dispose);

        var prompt = ctx.Get<Core.SystemPrompt.SystemPromptService>("systemPrompt");
        var bashSection = prompt.RegisterSection("tool:bash", 105, _ =>
            "When running commands: each bash call is a fresh shell; pass workdir instead of cd. "
            + "Verify changes with commands before claiming completion.");
        var fsSection = prompt.RegisterSection("tool:fs", 106, _ =>
            "File editing: read a file before editing it (edit refuses otherwise). write replaces the whole file; "
            + "edit replaces an exact literal match. Mutations are confined to the workspace root.");
        ctx.Effect(() =>
        {
            bashSection.Dispose();
            fsSection.Dispose();
        });
        return Task.CompletedTask;
    }
}
