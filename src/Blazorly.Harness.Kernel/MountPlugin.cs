namespace Blazorly.Harness.Kernel;

/// <summary>
/// Adapts a mount function into the plugin composition: the spine (core services) joins the
/// same topological boot as capability and third-party plugins. The name feeds diagnostics
/// and duplicate detection; inject declares the service keys this mount needs first.
/// </summary>
public sealed class MountPlugin(string name, string[] inject, Func<HarnessContext, Task> mount) : AsyncHarnessPlugin
{
    public override string Name => name;
    public override string[] Inject => inject;

    public override Task ApplyAsync(HarnessContext ctx) => mount(ctx);

    public static MountPlugin Sync(string name, string[] inject, Action<HarnessContext> mount)
        => new(name, inject, ctx => { mount(ctx); return Task.CompletedTask; });
}
