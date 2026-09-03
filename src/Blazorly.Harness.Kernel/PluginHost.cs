namespace Blazorly.Harness.Kernel;

/// <summary>A unit of composition: contributes services, listeners, and reversible effects to a context.</summary>
public interface IHarnessPlugin
{
    /// <summary>Stable plugin id used in composition and diagnostics.</summary>
    string Name { get; }

    /// <summary>Service keys that must exist before <see cref="Apply"/> runs. Load order derives from these.</summary>
    string[] Inject { get; }
}

/// <summary>Plugin with a synchronous apply body.</summary>
public abstract class HarnessPlugin : IHarnessPlugin
{
    public abstract string Name { get; }
    public virtual string[] Inject { get; } = [];

    public void Apply(HarnessContext ctx) => ApplyAsync(ctx).GetAwaiter().GetResult();

    protected virtual Task ApplyAsync(HarnessContext ctx) => Task.CompletedTask;
}

/// <summary>Plugin with an asynchronous apply body.</summary>
public abstract class AsyncHarnessPlugin : IHarnessPlugin
{
    public abstract string Name { get; }
    public virtual string[] Inject { get; } = [];

    public abstract Task ApplyAsync(HarnessContext ctx);
}

/// <summary>
/// Applies plugins to a context in dependency order derived from <c>Inject</c> keys:
/// a plugin waits until every key it injects is provided, so load order is expressed
/// through service availability rather than boot sequencing.
/// </summary>
public static class PluginHost
{
    public static async Task<HarnessContext> BootAsync(IEnumerable<IHarnessPlugin> plugins)
    {
        var ctx = HarnessContext.CreateRoot();
        await ApplyAllAsync(ctx, plugins).ConfigureAwait(false);
        return ctx;
    }

    public static Task ApplyAllAsync(HarnessContext ctx, IEnumerable<IHarnessPlugin> plugins)
        => ApplyAllAsync(ctx, plugins, appliedOrder: null);

    public static async Task ApplyAllAsync(
        HarnessContext ctx, IEnumerable<IHarnessPlugin> plugins, IList<string>? appliedOrder)
    {
        var pending = plugins.ToList();
        var applied = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var ready = pending.Where(p => p.Inject.All(k => ctx.TryGet<object>(k) is not null)).ToList();
            if (ready.Count == 0)
            {
                var blocked = string.Join(", ", pending.Select(p => $"{p.Name}[{string.Join(",", p.Inject.Where(k => ctx.TryGet<object>(k) is null))}]"));
                throw new HarnessException("PLUGIN_DEADLOCK", $"no plugin can load next; blocked: {blocked}");
            }
            foreach (var plugin in ready)
            {
                switch (plugin)
                {
                    case AsyncHarnessPlugin asyncPlugin:
                        await asyncPlugin.ApplyAsync(ctx).ConfigureAwait(false);
                        break;
                    case HarnessPlugin syncPlugin:
                        syncPlugin.Apply(ctx);
                        break;
                    default:
                        throw new HarnessException("PLUGIN_KIND", $"{plugin.Name} must derive from HarnessPlugin or AsyncHarnessPlugin");
                }
                if (!applied.Add(plugin.Name))
                    throw new HarnessException("DUPLICATE_PLUGIN", $"plugin '{plugin.Name}' applied twice");
                appliedOrder?.Add(plugin.Name);
                pending.Remove(plugin);
            }
        }
    }
}
