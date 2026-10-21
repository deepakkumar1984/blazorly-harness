using System.Security.Cryptography;
using System.Text;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Instructions;

/// <summary>
/// Loads project instruction files (AGENTS.md / CLAUDE.md and their .local.md overlays) from
/// the harness home, the workspace root, and every directory whose files the session touched
/// with read/write/edit. Renders into the runtime-context snapshot (a dynamic context
/// section), least-specific first, with per-directory dedup, an inclusion budget that drops
/// whole broader files before truncating the most specific one, and literal
/// &lt;/system-reminder&gt; escaping.
/// </summary>
public sealed class ProjectInstructionsService
{
    public const string ServiceKey = "projectInstructions";

    /// <summary>Inclusion budget in characters (≈6k tokens at the 4-chars-per-token heuristic).</summary>
    public const int BudgetChars = 24_000;

    public static readonly IReadOnlyList<string> FileNames = ["AGENTS.md", "AGENTS.local.md", "CLAUDE.md", "CLAUDE.local.md"];

    private readonly HarnessContext _ctx;
    private readonly string _home;
    private readonly object _gate = new();
    private readonly Dictionary<object, HashSet<string>> _touched = [];

    private ProjectInstructionsService(HarnessContext ctx, string home)
    {
        _ctx = ctx;
        _home = home;
    }

    public static ProjectInstructionsService Mount(HarnessContext ctx, string home)
    {
        var service = new ProjectInstructionsService(ctx, home);
        ctx.Provide(ServiceKey, service);

        // Touch-driven discovery: the pipeline emits tools/result for every terminal tool path.
        _ = ctx.Events.On<ToolPostExecute>("tools/result", (payload, _) =>
        {
            if (payload.Execution.Input.Name is "read" or "write" or "edit")
            {
                var args = payload.Execution.Input.Arguments;
                var touched = args.TryGetProperty("file_path", out var filePath) && filePath.ValueKind == System.Text.Json.JsonValueKind.String
                    ? filePath.GetString()
                    : args.TryGetProperty("path", out var path) && path.ValueKind == System.Text.Json.JsonValueKind.String
                        ? path.GetString()
                        : null;
                if (touched is { Length: > 0 })
                {
                    service.NoteTouch(payload.Execution.Input.Agent?.ScopeKey, touched);
                }
            }
            return Task.CompletedTask;
        });

        ctx.Get<SystemPromptService>(SystemPromptService.ServiceKey)
            .RegisterContext("project-instructions", 40, context => service.Render(context.Agent?.ScopeKey, context.Cwd));
        return service;
    }

    public void NoteTouch(object? scopeKey, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory)) return;
        lock (_gate)
        {
            var key = scopeKey ?? (object)"__global__";
            if (!_touched.TryGetValue(key, out var dirs))
            {
                dirs = new HashSet<string>(StringComparer.Ordinal);
                _touched[key] = dirs;
            }
            dirs.Add(directory);
        }
    }

    /// <summary>Directories to consider, least-specific first: home, workspace root, touched dirs by depth.</summary>
    private List<string> OrderedDirectories(object? scopeKey, string? cwd)
    {
        var dirs = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return;
            if (dirs.Any(d => string.Equals(d, full, StringComparison.Ordinal))) return;
            dirs.Add(full);
        }
        Add(_home);
        Add(cwd);
        List<string> touched;
        lock (_gate)
        {
            touched = _touched.TryGetValue(scopeKey ?? (object)"__global__", out var scopeDirs) ? [.. scopeDirs] : [];
        }
        dirs.AddRange(touched
            .OrderBy(d => d.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(d => d, StringComparer.Ordinal));
        return dirs;
    }

    /// <summary>Collects (path, content) entries least-specific first with per-directory dedup.</summary>
    public IReadOnlyList<(string Path, string Content)> Collect(object? scopeKey, string? cwd)
    {
        var entries = new List<(string Path, string Content)>();
        foreach (var dir in OrderedDirectories(scopeKey, cwd))
        {
            var dirRendered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in FileNames)
            {
                var path = Path.Combine(dir, name);
                string content;
                try
                {
                    if (!File.Exists(path)) continue;
                    content = File.ReadAllText(path);
                }
                catch
                {
                    continue; // unreadable instruction files never break a request
                }
                var trimmed = content.Trim();
                if (trimmed.Length == 0) continue;
                if (!dirRendered.Add(Hash(trimmed))) continue; // CLAUDE.md duplicating AGENTS.md renders once
                entries.Add((path, Escape(trimmed)));
            }
        }
        return entries;
    }

    /// <summary>Renders the context-section body: budget applied most-specific-first, empty when nothing applies.</summary>
    public string Render(object? scopeKey, string? cwd)
    {
        var entries = Collect(scopeKey, cwd);
        if (entries.Count == 0) return string.Empty;

        // Include the most specific files first; broader files that no longer fit are dropped whole.
        var included = new List<(string Path, string Content)>();
        var remaining = BudgetChars;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var (path, content) = entries[i];
            var cost = HeaderCost(path) + content.Length + 2;
            if (cost <= remaining)
            {
                included.Add((path, content));
                remaining -= cost;
                continue;
            }
            if (included.Count == 0)
            {
                // The most specific file alone exceeds the budget: truncate it rather than drop everything.
                var head = content[..Math.Max(0, Math.Min(content.Length, remaining - HeaderCost(path) - 20))];
                included.Add((path, head + "\n\n[truncated]"));
            }
            break;
        }
        included.Reverse();

        var body = string.Join("\n\n", included.Select(e => $"{e.Path}:\n{e.Content}"));
        return "Project instructions (AGENTS.md / CLAUDE.md):\n" + body;
    }

    private static int HeaderCost(string path) => path.Length + 2;

    private static string Hash(string content) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content.Trim())));

    private static string Escape(string content)
        => content.Replace("</system-reminder>", "<\\/system-reminder>", StringComparison.Ordinal);
}
