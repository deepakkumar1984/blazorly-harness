using System.Reflection;
using System.Runtime.Loader;

namespace Blazorly.Harness.Kernel;

/// <summary>One third-party plugin discovered on disk, with the assembly it came from.</summary>
public sealed record DiscoveredPlugin(string AssemblyPath, IHarnessPlugin Plugin);

/// <summary>
/// Discovers third-party plugins: every managed <c>*.dll</c> directly inside a plugin
/// directory is loaded into its own <see cref="AssemblyLoadContext"/> and scanned for
/// concrete <see cref="IHarnessPlugin"/> implementations with public parameterless
/// constructors. Host assemblies (Blazorly.Harness.*) always resolve to the already-loaded
/// copies, so plugin types unify with the host's <c>IHarnessPlugin</c>; anything else
/// resolves from the plugin's own directory. Unmanaged or unloadable files are skipped
/// with a diagnostic — discovery never fails a boot. Contexts are not unloaded:
/// updating a plugin takes an app restart.
/// </summary>
public static class PluginLoader
{
    /// <summary>Loads plugins from a directory; a missing directory yields an empty list.</summary>
    public static IReadOnlyList<DiscoveredPlugin> LoadFromDirectory(string dir, TextWriter? log = null)
    {
        if (!Directory.Exists(dir)) return [];
        var found = new List<DiscoveredPlugin>();
        foreach (var dll in Directory.GetFiles(dir, "*.dll").OrderBy(Path.GetFileName))
            found.AddRange(LoadFromAssembly(dll, log));
        return found;
    }

    private static IReadOnlyList<DiscoveredPlugin> LoadFromAssembly(string dll, TextWriter? log)
    {
        Assembly assembly;
        try
        {
            assembly = new PluginLoadContext(Path.GetDirectoryName(dll)!).LoadFromAssemblyPath(dll);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            log?.WriteLine($"[plugins] skipping '{Path.GetFileName(dll)}': not a loadable assembly ({ex.GetType().Name})");
            return [];
        }
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
            log?.WriteLine($"[plugins] '{Path.GetFileName(dll)}': some types failed to load; continuing with the rest");
        }
        var found = new List<DiscoveredPlugin>();
        foreach (var type in types)
        {
            if (!type.IsAssignableTo(typeof(IHarnessPlugin)) || type.IsAbstract || type.IsInterface) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                log?.WriteLine($"[plugins] '{type.FullName}': no public parameterless constructor; skipped");
                continue;
            }
            try
            {
                found.Add(new DiscoveredPlugin(dll, (IHarnessPlugin)Activator.CreateInstance(type)!));
            }
            catch (Exception ex)
            {
                log?.WriteLine($"[plugins] '{type.FullName}': constructor threw ({ex.GetType().Name}); skipped");
            }
        }
        return found;
    }

    private sealed class PluginLoadContext(string pluginDir) : AssemblyLoadContext(name: $"blazorly-plugin:{Path.GetFileName(pluginDir)}", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Host assemblies unify with the already-loaded copies; returning null falls
            // back to the default context, which resolves them.
            if (assemblyName.Name is not null
                && (assemblyName.Name.StartsWith("Blazorly.Harness.", StringComparison.Ordinal)
                    || Default.Assemblies.Any(a => a.GetName().Name == assemblyName.Name)))
                return null;
            var candidate = Path.Combine(pluginDir, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
