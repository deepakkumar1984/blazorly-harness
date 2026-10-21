using Blazorly.Harness.Core.Instructions;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Cli;

/// <summary>
/// <c>blazorly init</c> — AI-drafted hierarchical AGENTS.md docs for a workspace. The model
/// only sees a deterministic repo brief, and every draft is path-verified with one
/// correction retry. Dry run by default; writes only with <c>--write</c>.
/// <c>--deterministic</c> skips the model and writes the template draft (offline).
/// </summary>
public static class InitCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        var depth = DocsInitService.DefaultDepth;
        var write = false;
        var force = false;
        var deterministic = false;
        string? provider = null;
        string? model = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h" or "help":
                    return Help();
                case "--root" when i + 1 < args.Length:
                    root = args[++i];
                    break;
                case "--depth" when i + 1 < args.Length && int.TryParse(args[++i], out var parsed):
                    depth = Math.Clamp(parsed, 0, 5);
                    break;
                case "--provider" when i + 1 < args.Length:
                    provider = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--write":
                    write = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--deterministic":
                    deterministic = true;
                    break;
                default:
                    Console.Error.WriteLine($"unknown init flag '{args[i]}' — try `blazorly init --help`");
                    return 1;
            }
        }

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"init: directory not found: {root}");
            return 1;
        }

        if (deterministic) return Deterministic(root, depth, write, force);

        var bootstrapper = new HarnessBootstrapper();
        bootstrapper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            if (!string.IsNullOrWhiteSpace(provider))
                bootstrapper.Settings.SelectProvider(bootstrapper.Settings.Provider, provider, model);
            else if (!string.IsNullOrWhiteSpace(model)) bootstrapper.Settings.Model = model;
            bootstrapper.ApplyProviderSelection();
            bootstrapper.ApplyDefaultSelection();
            var route = $"{bootstrapper.Settings.Provider}/{bootstrapper.Settings.Model}";

            if (!write)
            {
                var preview = DocsInitService.Preview(root, depth);
                Console.WriteLine($"init: {preview.Root} (dry run — AI drafts with {route}; pass --write to create)");
                foreach (var file in preview.Files)
                    Console.WriteLine($"  {(file.Exists ? "refresh" : "create"),-9} {file.RelativePath}");
                PrintStale(preview.StaleReferences);
                return 0;
            }

            Console.WriteLine($"init: drafting with {route}…");
            var complete = AiDocsDrafter.CompleteWith(bootstrapper.Llm,
                bootstrapper.Settings.Provider, bootstrapper.Settings.Model);
            var result = await AiDocsDrafter.DraftAndApplyAsync(root, depth, route, complete, force)
                .ConfigureAwait(false);
            Console.WriteLine($"init: {result.Root}");
            foreach (var path in result.Written) Console.WriteLine($"  wrote     {path}");
            foreach (var path in result.Unchanged) Console.WriteLine($"  unchanged {path}");
            if (result.Unresolved.Count > 0)
            {
                Console.WriteLine($"  WARNING: {result.Unresolved.Count} invented path(s) survived correction:");
                foreach (var item in result.Unresolved.Take(20))
                    Console.WriteLine($"    {item.DocPath}:{item.Line}: `{item.Reference}` does not exist");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"init: {ex.Message}");
            return 1;
        }
    }

    private static int Deterministic(string root, int depth, bool write, bool force)
    {
        if (write)
        {
            var result = DocsInitService.Apply(root, depth, force);
            Console.WriteLine($"init: {result.Root} (deterministic template, no model calls)");
            foreach (var path in result.Written) Console.WriteLine($"  wrote     {path}");
            foreach (var path in result.Unchanged) Console.WriteLine($"  unchanged {path}");
            PrintStale(result.StaleReferences);
            return 0;
        }

        var preview = DocsInitService.Preview(root, depth);
        Console.WriteLine($"init: {preview.Root} (dry run — pass --write to create these files)");
        foreach (var file in preview.Files)
            Console.WriteLine($"  {(file.Exists ? "refresh" : "create"),-9} {file.RelativePath} ({file.Content.Length} chars)");
        PrintStale(preview.StaleReferences);
        return 0;
    }

    private static void PrintStale(IReadOnlyList<DocsInitService.StaleReference> stale)
    {
        if (stale.Count == 0)
        {
            Console.WriteLine("  references: all path references in existing instruction files resolve");
            return;
        }
        Console.WriteLine($"  unresolved references ({stale.Count}):");
        foreach (var item in stale.Take(30))
            Console.WriteLine($"    {item.DocPath}:{item.Line}: `{item.Reference}` does not exist");
        if (stale.Count > 30) Console.WriteLine($"    … and {stale.Count - 30} more");
    }

    private static int Help()
    {
        Console.WriteLine("""
            blazorly init — AI-drafted hierarchical AGENTS.md docs for a workspace

            Usage:
              blazorly init [--root PATH] [--depth N] [--provider P] [--model M] [--write] [--force]
              blazorly init --deterministic [--write]   template draft, no model calls

            The model only sees a deterministic repo brief (tree, manifests, verified
            commands). Every draft is path-verified; invented paths trigger one correction
            retry, and survivors are reported — never silently trusted. Re-runs preserve
            everything below the <!-- MANUAL: --> marker.

            Flags:
              --root PATH      workspace to document (default: current directory)
              --depth N        subdirectory depth (default: 0 = root file only)
              --provider P     provider route (default: settings selection)
              --model M        model id (default: settings selection)
              --write          draft and create/refresh the files (default is dry run)
              --force          on --write, replace existing files instead of merging
              --deterministic  offline template draft instead of AI drafting
            """);
        return 0;
    }
}
