using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record GlobArgs(string Pattern, string? Path = null);

public sealed record GlobOutput(IReadOnlyList<string> Files, bool Sampled, int TotalMatches);

/// <summary>glob: find files whose paths match a pattern, newest first, capped at 100.</summary>
public sealed class GlobTool : ToolDefinition<GlobArgs, GlobOutput>
{
    private const int Cap = 100;

    public override string Name => "glob";

    public override string Description =>
        "Find files whose paths match a glob pattern (e.g. \"**/*.cs\", \"src/**/*.ts\"). Returns matching "
        + "file paths — never directories — in modification-time order, up to 100; larger results are sampled.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["pattern"] = JsonSchema.String("Glob pattern to match file paths against."),
            ["path"] = JsonSchema.String("Directory to search in. Defaults to the session workspace."),
        },
        required: ["pattern"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["files"] = JsonSchema.Array(JsonSchema.String()),
            ["sampled"] = JsonSchema.Boolean(),
            ["totalMatches"] = JsonSchema.Integer(),
        },
        required: ["files", "sampled", "totalMatches"]);

    protected override bool IsConcurrencySafeTyped(GlobArgs args) => true;

    protected override Task<GlobOutput> ExecuteTyped(GlobArgs args, ToolRunContext exec)
    {
        var cwd = exec.Agent?.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
        var root = string.IsNullOrWhiteSpace(args.Path)
            ? cwd
            : Path.GetFullPath(args.Path, cwd);
        var matcher = new GlobMatcher(args.Pattern);
        var files = new List<string>();
        foreach (var file in EnumerateFiles(root))
        {
            var relative = Path.GetRelativePath(root, file);
            if (matcher.Matches(relative.Replace(Path.DirectorySeparatorChar, '/')))
            {
                files.Add(file);
            }
        }
        var total = files.Count;
        IReadOnlyList<string> result = files;
        var sampled = false;
        if (total > Cap)
        {
            result = Sample(files, Cap);
            sampled = true;
        }
        return Task.FromResult(new GlobOutput([.. result], sampled, total));
    }

    private static List<string> Sample(List<string> files, int cap)
    {
        var step = (double)files.Count / cap;
        var sampled = new List<string>(cap);
        for (var i = 0; i < cap; i++) sampled.Add(files[(int)(i * step)]);
        return sampled;
    }

    internal static IEnumerable<string> EnumerateFiles(string root)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal) { ".git", "node_modules", "bin", "obj", ".venv", "__pycache__", "dist", ".next", "target" };
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (ignored.Contains(name)) continue;
                if (Directory.Exists(entry))
                {
                    queue.Enqueue(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(GlobArgs args, GlobOutput output)
    {
        if (output.Files.Count == 0) return [new TextBlock("No files matched the pattern.")];
        var builder = new StringBuilder();
        foreach (var file in output.Files) builder.AppendLine(file);
        if (output.Sampled) builder.AppendLine($"(showing {output.Files.Count} of {output.TotalMatches} matches, sampled)");
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(GlobArgs args) => new()
    {
        Card = "generic",
        Kind = "search",
        Title = args.Pattern,
        Description = "glob file search",
    };
}

/// <summary>Shared file-scan hygiene for glob/grep: binary detection.</summary>
public static class SourceScan
{
    private const int SniffBytes = 8192;

    /// <summary>True when the file looks binary (a NUL byte in the first 8KB).</summary>
    public static bool IsBinary(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(SniffBytes, stream.Length > int.MaxValue ? SniffBytes : (int)Math.Min(stream.Length, SniffBytes))];
            if (buffer.Length == 0) return false;
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0) return true;
            }
            return false;
        }
        catch
        {
            return false; // unreadable here means the read step will skip it with its own guard
        }
    }

    /// <summary>Comma-separated glob filters; empty means match everything.</summary>
    public static IReadOnlyList<GlobMatcher> Includes(string? include)
    {
        if (string.IsNullOrWhiteSpace(include)) return [];
        return include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pattern => new GlobMatcher(pattern))
            .ToList();
    }

    public static bool MatchesAny(IReadOnlyList<GlobMatcher> includes, string fileName)
        => includes.Count == 0 || includes.Any(m => m.Matches(fileName));
}

/// <summary>Minimal glob matcher: **, *, ?, and {a,b} alternation.</summary>
public sealed partial class GlobMatcher(string pattern)
{
    private readonly Regex _regex = GlobRegex(BuildRegex(pattern));

    [GeneratedRegex("^(.*)$", RegexOptions.Compiled)]
    private static partial Regex EmptyRegex();

    private static Regex GlobRegex(string expression) => new(expression, RegexOptions.Compiled);

    private static string BuildRegex(string glob)
    {
        var builder = new StringBuilder("^");
        foreach (var ch in glob)
        {
            switch (ch)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                case '{':
                case '}':
                case ',':
                    builder.Append(ch is '{' ? "(" : ch is '}' ? ")" : "|");
                    break;
                default:
                    builder.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }
        builder.Append('$');
        return builder.ToString();
    }

    public bool Matches(string path) => _regex.IsMatch(path);
}

public sealed record GrepArgs(string Pattern, string? Path = null, string? Include = null);

public sealed record GrepOutput(IReadOnlyList<SearchResultLine> Matches, bool Capped, int TotalMatches);

/// <summary>grep: search file contents with a regular expression, first 250 matches inline.</summary>
public sealed class GrepTool : ToolDefinition<GrepArgs, GrepOutput>
{
    private const int Cap = 250;

    public override string Name => "grep";

    public override string Description =>
        "Search file contents with a regular expression. Returns matching lines with line numbers, "
        + "grouped by file, up to 250 matches. Build output dirs (.git, node_modules, bin, obj, …) are "
        + "skipped and binary files are never searched. Use read on a matched file for surrounding context.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["pattern"] = JsonSchema.String("Regular expression to search for (.NET syntax)."),
            ["path"] = JsonSchema.String("File or directory to search. Defaults to the session workspace."),
            ["include"] = JsonSchema.String("Comma-separated glob filters for which files to search (e.g. \"*.cs,*.razor\")."),
        },
        required: ["pattern"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["matches"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["file"] = JsonSchema.String(),
                    ["line"] = JsonSchema.Integer(),
                    ["text"] = JsonSchema.String(),
                    ["matched"] = JsonSchema.Boolean(),
                },
                Required = ["file", "line", "text", "matched"],
                AdditionalProperties = false,
            }),
            ["capped"] = JsonSchema.Boolean(),
            ["totalMatches"] = JsonSchema.Integer(),
        },
        required: ["matches", "capped", "totalMatches"]);

    protected override bool IsConcurrencySafeTyped(GrepArgs args) => true;

    protected override Task<GrepOutput> ExecuteTyped(GrepArgs args, ToolRunContext exec)
    {
        var cwd = exec.Agent?.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
        var root = string.IsNullOrWhiteSpace(args.Path)
            ? cwd
            : Path.GetFullPath(args.Path, cwd);
        var regex = new Regex(args.Pattern, RegexOptions.Compiled);
        var includes = SourceScan.Includes(args.Include);
        var matches = new List<SearchResultLine>();
        var capped = false;

        IEnumerable<string> files;
        if (File.Exists(root))
        {
            files = [root];
        }
        else
        {
            files = GlobTool.EnumerateFiles(root).Where(f => SourceScan.MatchesAny(includes, Path.GetFileName(f)));
        }

        foreach (var file in files)
        {
            if (SourceScan.IsBinary(file)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); }
            catch (UnauthorizedAccessException) { continue; }
            catch (System.IO.IOException) { continue; }
            catch (DecoderFallbackException) { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    if (matches.Count >= Cap)
                    {
                        capped = true;
                        break;
                    }
                    matches.Add(new SearchResultLine(file, i + 1, lines[i].Length > 500 ? lines[i][..500] : lines[i], true));
                }
            }
            if (capped) break;
        }
        var total = capped ? Cap : matches.Count;
        return Task.FromResult(new GrepOutput(matches, capped, total));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(GrepArgs args, GrepOutput output)
    {
        if (output.Matches.Count == 0) return [new TextBlock("No matches found.")];
        var builder = new StringBuilder();
        string? currentFile = null;
        foreach (var match in output.Matches)
        {
            if (match.File != currentFile)
            {
                if (currentFile is not null) builder.AppendLine();
                builder.AppendLine(match.File + ":");
                currentFile = match.File;
            }
            builder.AppendLine($"  {match.Line}: {match.Text}");
        }
        if (output.Capped) builder.AppendLine($"(capped at {Cap} matches)");
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(GrepArgs args) => new()
    {
        Card = "generic",
        Kind = "search",
        Title = args.Pattern,
        Description = "grep content search",
    };
}

public sealed record TodoWriteArgs(IReadOnlyList<TodoInput> Todos);

public sealed record TodoInput(string Content, string Status);

public sealed record TodoWriteOutput(IReadOnlyList<TodoItem> Todos, int Pending, int InProgress, int CompletedCount);

/// <summary>todo_write: whole-list snapshot appended as a durable todo/write event.</summary>
public sealed class TodoWriteTool : ToolDefinition<TodoWriteArgs, TodoWriteOutput>
{
    public override string Name => "todo_write";

    public override string Description =>
        "Update the task list with a whole-list snapshot. Every call replaces the previous list; "
        + "mark exactly one task in_progress at a time and completed tasks stay visible.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["todos"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["content"] = JsonSchema.String("The task description."),
                    ["status"] = JsonSchema.String(values:
                    [
                        JsonSerializer.SerializeToElement(TodoItem.Pending),
                        JsonSerializer.SerializeToElement(TodoItem.InProgress),
                        JsonSerializer.SerializeToElement(TodoItem.Completed),
                    ]),
                },
                Required = ["content", "status"],
                AdditionalProperties = false,
            }, minItems: 1),
        },
        required: ["todos"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["todos"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["content"] = JsonSchema.String(),
                    ["status"] = JsonSchema.String(),
                },
                Required = ["content", "status"],
                AdditionalProperties = false,
            }),
            ["pending"] = JsonSchema.Integer(),
            ["inProgress"] = JsonSchema.Integer(),
            ["completedCount"] = JsonSchema.Integer(),
        },
        required: ["todos", "pending", "inProgress", "completedCount"]);

    protected override Task<TodoWriteOutput> ExecuteTyped(TodoWriteArgs args, ToolRunContext exec)
    {
        var todos = args.Todos
            .Select(t => new TodoItem(t.Content.Trim(), t.Status))
            .ToList();
        if (todos.Any(t => t.Content.Length == 0))
            throw new ToolException("INVALID_ARGS", "todo content must be non-empty");
        if (todos.Select(t => t.Content).Distinct().Count() != todos.Count)
            throw new ToolException("INVALID_ARGS", "todo contents must be unique");
        if (todos.Count(t => t.Status == TodoItem.InProgress) > 1)
            throw new ToolException("INVALID_ARGS", "only one todo may be in_progress at a time");
        if (todos.Any(t => t.Status is not (TodoItem.Pending or TodoItem.InProgress or TodoItem.Completed)))
            throw new ToolException("INVALID_ARGS", "invalid todo status");
        exec.Session.Append(SessionEventTypes.TodoWrite, new SessionPayloads.TodoWrite(todos));
        return Task.FromResult(new TodoWriteOutput(
            todos,
            todos.Count(t => t.Status == TodoItem.Pending),
            todos.Count(t => t.Status == TodoItem.InProgress),
            todos.Count(t => t.Status == TodoItem.Completed)));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TodoWriteArgs args, TodoWriteOutput output)
        => [new TextBlock($"Updated todo list: {output.Pending} pending, {output.InProgress} in progress, {output.CompletedCount} completed.")];

    protected override ToolCallView? PresentCallTyped(TodoWriteArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Update todo list",
        Description = $"{args.Todos.Count} items",
    };
}
