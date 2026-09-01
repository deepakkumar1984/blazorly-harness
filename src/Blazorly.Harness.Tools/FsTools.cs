using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

/// <summary>
/// Sandbox policy: per-session mode override (durable sandbox/mode event, latest wins) over
/// the deployment default, with mutations confined to the owning session's workspace root.
/// deny() paths produce the shared sandbox marker.
/// </summary>
public sealed class SandboxPolicy
{
    public const string ReadOnly = "read-only";
    public const string WorkspaceWrite = "workspace-write";
    public const string DangerFullAccess = "danger-full-access";

    /// <summary>Deployment default mode applied when a session carries no override.</summary>
    public string DefaultMode { get; set; } = WorkspaceWrite;

    public string? DenyWrite(string absolutePath, Blazorly.Harness.Core.Sessions.Session? session)
    {
        var mode = session?.LatestSandboxMode() ?? DefaultMode;
        if (mode == DangerFullAccess) return null;
        if (mode == ReadOnly) return $"[sandbox: file access denied under {mode} mode]";
        var root = Path.GetFullPath(session?.Header.Cwd ?? Directory.GetCurrentDirectory());
        var target = Path.GetFullPath(absolutePath);
        var prefixed = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefixed, StringComparison.Ordinal) && !string.Equals(target, root, StringComparison.Ordinal))
        {
            return $"[sandbox: file access denied under {mode} mode; mutations are confined to {root}]";
        }
        return null;
    }
}

/// <summary>Tracks which files the model has read this process lifetime; the read-before-edit guard.</summary>
public sealed class FsObservationTracker
{
    private readonly ConcurrentDictionary<string, byte> _observed = new(StringComparer.Ordinal);

    public void Observe(string absolutePath) => _observed[Path.GetFullPath(absolutePath)] = 1;

    public bool IsObserved(string absolutePath) => _observed.ContainsKey(Path.GetFullPath(absolutePath));
}

public sealed record ReadArgs([property: System.Text.Json.Serialization.JsonPropertyName("file_path")] string FilePath, int? Offset = null, int? Limit = null);

public sealed record ReadOutput(string Path, int Offset, IReadOnlyList<ReadLine> Lines, int TotalLines, string Language);

/// <summary>read: line-numbered window over a UTF-8 text file.</summary>
public sealed class ReadTool(FsObservationTracker tracker) : ToolDefinition<ReadArgs, ReadOutput>
{
    public const int DefaultLimit = 2000;

    public override string Name => "read";

    public override string Description =>
        "Read a UTF-8 text file and return line-numbered content. Use offset (1-based first line) "
        + "and limit (max lines, default 2000) for windows. Independent files may be read in parallel.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["file_path"] = JsonSchema.String("Path to read, resolved against the session workspace."),
            ["offset"] = JsonSchema.Number("1-based first line to return. Defaults to 1."),
            ["limit"] = JsonSchema.Number("Maximum number of lines to return. Defaults to 2000."),
        },
        required: ["file_path"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["path"] = JsonSchema.String(),
            ["offset"] = JsonSchema.Integer(),
            ["lines"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["number"] = JsonSchema.Integer(),
                    ["text"] = JsonSchema.String(),
                },
                Required = ["number", "text"],
                AdditionalProperties = false,
            }),
            ["totalLines"] = JsonSchema.Integer(),
            ["language"] = JsonSchema.String(),
        },
        required: ["path", "offset", "lines", "totalLines", "language"]);

    protected override bool IsConcurrencySafeTyped(ReadArgs args) => true;

    protected override async Task<ReadOutput> ExecuteTyped(ReadArgs args, ToolRunContext exec)
    {
        var path = Resolve(args.FilePath, exec);
        if (!File.Exists(path)) throw new ToolException("FILE_NOT_FOUND", $"file '{path}' does not exist");
        tracker.Observe(path);
        var lines = new List<string>();
        using (var reader = new StreamReader(path, Encoding.UTF8))
        {
            while (await reader.ReadLineAsync(exec.Signal).ConfigureAwait(false) is { } line) lines.Add(line);
        }
        var offset = Math.Max(1, args.Offset ?? 1);
        var limit = Math.Min(args.Limit is > 0 ? args.Limit.Value : DefaultLimit, DefaultLimit);
        var window = lines.Skip(offset - 1).Take(limit).Select((text, index) => new ReadLine(offset + index, text)).ToList();
        return new ReadOutput(path, offset, window, lines.Count, LanguageOf(path));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(ReadArgs args, ReadOutput output)
    {
        var builder = new StringBuilder();
        foreach (var line in output.Lines)
        {
            builder.Append(line.Number.ToString().PadLeft(6)).Append("→").AppendLine(line.Text);
        }
        if (output.Offset + output.Lines.Count - 1 < output.TotalLines)
        {
            builder.AppendLine($"… ({output.Lines.Count} of {output.TotalLines} lines shown, ending at line {output.Offset + output.Lines.Count - 1})");
        }
        return [new TextBlock(builder.ToString())];
    }

    protected override ToolCallView? PresentCallTyped(ReadArgs args) => new()
    {
        Card = "generic",
        Kind = "read",
        Title = Path.GetFileName(args.FilePath),
        Path = args.FilePath,
        Line = args.Offset,
    };

    protected override ToolResultView? PresentResultTyped(ReadArgs args, ToolExecutionResult result)
        => new()
        {
            Card = "read",
            Title = Path.GetFileName(args.FilePath),
            Language = LanguageOf(args.FilePath),
            ReadLines = DeserializeLines(result),
        };

    private static IReadOnlyList<ReadLine>? DeserializeLines(ToolExecutionResult result)
    {
        try
        {
            return result.Value?.Deserialize<ReadOutput>(SessionJson.Options)?.Lines;
        }
        catch
        {
            return null;
        }
    }

    internal static string Resolve(string path, ToolRunContext exec)
    {
        var root = exec.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(path, root);
    }

    internal static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript",
        ".py" => "python",
        ".rs" => "rust",
        ".go" => "go",
        ".md" => "markdown",
        ".json" or ".jsonc" => "json",
        ".yml" or ".yaml" => "yaml",
        ".sh" or ".bash" => "bash",
        ".sql" => "sql",
        ".html" or ".razor" => "html",
        ".css" => "css",
        _ => "text",
    };
}

public sealed record WriteArgs([property: System.Text.Json.Serialization.JsonPropertyName("file_path")] string FilePath, string Content);

public sealed record WriteOutput(string Path, string Operation, string? Before, string After);

/// <summary>write: create or fully replace a UTF-8 text file, confined by the sandbox.</summary>
public sealed class WriteTool(FsObservationTracker tracker, SandboxPolicy sandbox) : ToolDefinition<WriteArgs, WriteOutput>
{
    public override string Name => "write";

    public override string Description =>
        "Create or fully replace a UTF-8 text file. Reads the target before replacing so the "
        + "change renders as a diff. Mutations are confined to the workspace root.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["file_path"] = JsonSchema.String("Path to write, resolved against the session workspace."),
            ["content"] = JsonSchema.String("Full UTF-8 text content to write."),
        },
        required: ["file_path", "content"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["path"] = JsonSchema.String(),
            ["operation"] = JsonSchema.String(values: [JsonSerializer.SerializeToElement("create"), JsonSerializer.SerializeToElement("update")]),
            ["before"] = JsonSchema.String(),
            ["after"] = JsonSchema.String(),
        },
        required: ["path", "operation", "after"]);

    protected override async Task<WriteOutput> ExecuteTyped(WriteArgs args, ToolRunContext exec)
    {
        var path = ReadTool.Resolve(args.FilePath, exec);
        if (sandbox.DenyWrite(path, exec.Session) is { } denied) throw new ToolException("SANDBOX_DENIED", denied);
        string? before = null;
        if (File.Exists(path))
        {
            before = await File.ReadAllTextAsync(path, Encoding.UTF8, exec.Signal).ConfigureAwait(false);
            tracker.Observe(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, args.Content, Encoding.UTF8, exec.Signal).ConfigureAwait(false);
        tracker.Observe(path);
        return new WriteOutput(path, before is null ? "create" : "update", before, args.Content);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(WriteArgs args, WriteOutput output)
        => [new TextBlock($"The file {output.Path} has been {output.Operation switch { "create" => "created", _ => "updated" }} successfully.")];

    protected override ToolCallView? PresentCallTyped(WriteArgs args) => new()
    {
        Card = "diff",
        Kind = "edit",
        Title = Path.GetFileName(args.FilePath),
        Path = args.FilePath,
        Diff = new FileDiff(args.FilePath, null, args.Content),
    };
}

public sealed record EditArgs([property: System.Text.Json.Serialization.JsonPropertyName("file_path")] string FilePath, string OldString, string NewString, bool? ReplaceAll = null);

public sealed record EditOutput(string Path, string Before, string After);

/// <summary>edit: literal string replace with a read-before-edit guard and unique-match default.</summary>
public sealed class EditTool(FsObservationTracker tracker, SandboxPolicy sandbox) : ToolDefinition<EditArgs, EditOutput>
{
    public override string Name => "edit";

    public override string Description =>
        "Edit an existing UTF-8 text file by replacing literal text. old_string must match exactly; "
        + "by default it must appear exactly once (set replace_all to replace every occurrence). "
        + "Read the file before editing it.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["file_path"] = JsonSchema.String("Path to edit, resolved by the filesystem backend."),
            ["old_string"] = JsonSchema.String("Literal text to replace. Must match exactly."),
            ["new_string"] = JsonSchema.String("Literal replacement text. Use an empty string to delete the match."),
            ["replace_all"] = JsonSchema.Boolean("Replace all matches. Defaults to false; when false, old_string must appear exactly once."),
        },
        required: ["file_path", "old_string", "new_string"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["path"] = JsonSchema.String(),
            ["before"] = JsonSchema.String(),
            ["after"] = JsonSchema.String(),
        },
        required: ["path", "before", "after"]);

    protected override async Task<EditOutput> ExecuteTyped(EditArgs args, ToolRunContext exec)
    {
        var path = ReadTool.Resolve(args.FilePath, exec);
        if (!File.Exists(path)) throw new ToolException("FILE_NOT_FOUND", $"file '{path}' does not exist; use write to create it");
        if (!tracker.IsObserved(path))
            throw new ToolException("FS_NOT_OBSERVED", $"read '{path}' before editing it");
        if (sandbox.DenyWrite(path, exec.Session) is { } denied) throw new ToolException("SANDBOX_DENIED", denied);

        var before = await File.ReadAllTextAsync(path, Encoding.UTF8, exec.Signal).ConfigureAwait(false);
        var replaceAll = args.ReplaceAll == true;
        var count = CountOccurrences(before, args.OldString);
        if (count == 0) throw new ToolException("NO_MATCH", "old_string was not found in the file");
        if (!replaceAll && count > 1)
            throw new ToolException("NOT_UNIQUE", $"old_string appears {count} times; include more context to make it unique or set replace_all");
        var after = replaceAll ? before.Replace(args.OldString, args.NewString, StringComparison.Ordinal)
            : ReplaceFirst(before, args.OldString, args.NewString);
        await File.WriteAllTextAsync(path, after, Encoding.UTF8, exec.Signal).ConfigureAwait(false);
        tracker.Observe(path);
        return new EditOutput(path, before, after);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(EditArgs args, EditOutput output)
        => [new TextBlock($"The file {output.Path} has been updated successfully.")];

    protected override ToolCallView? PresentCallTyped(EditArgs args) => new()
    {
        Card = "diff",
        Kind = "edit",
        Title = Path.GetFileName(args.FilePath),
        Path = args.FilePath,
        Diff = new FileDiff(args.FilePath, args.OldString, args.NewString),
    };
}
