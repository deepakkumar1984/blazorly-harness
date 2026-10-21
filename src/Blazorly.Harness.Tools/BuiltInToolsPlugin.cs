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
            + "Never start servers or long-running work with shell backgrounding (&, nohup, disown): "
            + "use bash with run_in_background: true and follow it with job_output / job_kill. "
            + "Verify changes with commands before claiming completion.");
        var fsSection = prompt.RegisterSection("tool:fs", 106, _ =>
            "File editing: read a file before editing it (edit refuses otherwise). write replaces the whole file; "
            + "edit replaces an exact literal match. Mutations are confined to the workspace root.");
        // Separate from the identity body on purpose: workspace-level custom instructions replace
        // the body below the header, but this task-list discipline must survive that override.
        var todoSection = prompt.RegisterSection("tool:todo", 107, _ =>
            "Task list: for multi-step work, create the todo_write list first and keep it accurate as you go — "
            + "exactly one task in_progress, mark each completed immediately when it finishes, add newly "
            + "discovered tasks. Never finish with a pending or in_progress task left open.");
        ctx.Effect(() =>
        {
            bashSection.Dispose();
            fsSection.Dispose();
            todoSection.Dispose();
        });
        return Task.CompletedTask;
    }
}
