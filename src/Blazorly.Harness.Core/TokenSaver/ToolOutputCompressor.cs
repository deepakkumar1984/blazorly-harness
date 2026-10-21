using System.Text;
using System.Text.RegularExpressions;

namespace Blazorly.Harness.Core.TokenSaver;

/// <summary>
/// Native RTK-style filtering of tool output, following the approach of RTK
/// (https://github.com/rtk-ai/rtk): shell and search output is reshaped into the compact
/// form a model actually needs — grouped, de-duplicated, noise-free — before it enters
/// history. This covers the highest-volume shapes (git status/log/diff, grep-style
/// matches, file listings, repetitive logs) rather than RTK's full command catalog.
///
/// Every filter is fail-safe: when the output does not match the expected shape closely
/// enough, the original text passes through untouched. File contents (read/write/edit
/// results) are never filtered — exactness there is the point, and spill already bounds
/// their size. Failed commands keep their full error output; only ANSI codes, blank-line
/// runs, and exact-repeat lines are collapsed.
/// </summary>
public static partial class ToolOutputCompressor
{
    /// <summary>Outputs shorter than this are not worth reshaping.</summary>
    public const int MinCharsToAct = 600;

    private static readonly HashSet<string> FilterableTools = new(StringComparer.Ordinal)
    {
        "bash", "job_output", "run_code", "run_remote", "grep", "glob",
    };

    /// <summary>
    /// Returns the compressed text (with a short marker line when the saving is real), or
    /// null to keep the original. <paramref name="bashCommand"/> is the shell command for
    /// bash-shaped tools when known; it only selects a filter, never gates the generic pass.
    /// </summary>
    public static string? Compress(string toolName, string? bashCommand, string text, bool isError)
    {
        if (text.Length < MinCharsToAct || !FilterableTools.Contains(toolName)) return null;

        var failed = isError || ExitCodeFailedRegex().IsMatch(text);
        var lines = SplitLines(text);
        string? filtered = null;
        var isDiff = false;

        if (!failed)
        {
            (filtered, isDiff) = (toolName, bashCommand) switch
            {
                ("grep", _) => (GrepFilter(lines), false),
                ("glob", _) => (ListingFilter(lines), false),
                (_, { } command) => CommandFilter(command, lines),
                _ => (null, false),
            };
        }

        var working = filtered is null ? lines : SplitLines(filtered);
        var compressed = string.Join("\n", GenericPass(working, dedupe: !isDiff)).TrimEnd();
        if (compressed.Length == 0) return null;

        var saved = text.Length - compressed.Length;
        if (saved < 200 && saved < text.Length / 10) return null;
        return $"{compressed}\n[compressed by token saver: {text.Length}→{compressed.Length} chars]";
    }

    private static (string? Filtered, bool IsDiff) CommandFilter(string command, List<string> lines)
    {
        // Only a single simple command gets a semantic filter; pipelines and compounds
        // change the output shape in ways the filters cannot predict.
        var trimmed = command.Trim();
        if (trimmed.Contains('|') || trimmed.Contains("&&") || trimmed.Contains("||")
            || trimmed.Contains(';') || trimmed.Contains('\n') || trimmed.Contains('`') || trimmed.Contains("$("))
            return (null, false);

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SkipWhile(t => t.Contains('=') && !t.StartsWith('-')) // leading VAR=value assignments
            .ToList();
        if (tokens.Count == 0) return (null, false);
        var exe = tokens[0][(tokens[0].LastIndexOf('/') + 1)..];

        return exe switch
        {
            "git" when tokens.Count > 1 && tokens[1] == "status"
                && !tokens.Any(t => t is "-s" or "--short" || t.StartsWith("--porcelain")) => (GitStatusFilter(lines), false),
            "git" when tokens.Count > 1 && tokens[1] == "log"
                && !tokens.Any(t => t is "--oneline" or "--stat" or "-p" or "--patch" || t.StartsWith("--format") || t.StartsWith("--pretty")) => (GitLogFilter(lines), false),
            "git" when tokens.Count > 1 && tokens[1] == "diff" => (DiffFilter(lines), true),
            "grep" or "rg" or "ag" or "ack" => (GrepFilter(lines), false),
            "ls" or "tree" or "find" => (ListingFilter(lines), false),
            _ => (null, false),
        };
    }

    // ----- semantic filters (each returns null when the shape does not match) -----

    private static string? GitStatusFilter(List<string> lines)
    {
        if (lines.Count == 0 || !(lines[0].StartsWith("On branch ") || lines[0].StartsWith("HEAD detached")))
            return null;

        var output = new List<string> { lines[0].TrimEnd() };
        string? section = null;
        var entries = new List<string>();
        var sawSection = false;

        void Flush()
        {
            if (section is null) return;
            var shown = entries.Take(12);
            var line = $"{section} ({entries.Count}): {string.Join("; ", shown)}";
            if (entries.Count > 12) line += $"; … +{entries.Count - 12} more";
            output.Add(line);
            entries.Clear();
        }

        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();
            switch (trimmed)
            {
                case "Changes to be committed:":
                    Flush(); section = "staged"; sawSection = true; continue;
                case "Changes not staged for commit:":
                    Flush(); section = "unstaged"; sawSection = true; continue;
                case "Untracked files:":
                    Flush(); section = "untracked"; sawSection = true; continue;
                case "Unmerged paths:":
                    Flush(); section = "unmerged"; sawSection = true; continue;
            }
            if (trimmed.Length == 0 || trimmed.StartsWith("(use ")) continue;
            if (section is not null && (raw.StartsWith('\t') || raw.StartsWith("        ")))
            {
                // "modified:   src/a.cs" → "modified src/a.cs"; untracked entries stay bare.
                entries.Add(trimmed.Replace(":   ", " ").Replace(":\t", " "));
                continue;
            }
            Flush();
            section = null;
            output.Add(trimmed);
        }
        Flush();
        return sawSection ? string.Join("\n", output) : null;
    }

    private static string? GitLogFilter(List<string> lines)
    {
        var commits = new List<string>();
        string? hash = null;
        var inBody = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("commit ") && line.Length > 14)
            {
                hash = line[7..].Trim().Split(' ')[0];
                inBody = false;
                continue;
            }
            if (hash is null) continue;
            if (!inBody)
            {
                if (line.Length == 0) inBody = true;
                continue;
            }
            var subject = line.Trim();
            if (subject.Length == 0) continue;
            commits.Add($"{hash[..Math.Min(7, hash.Length)]} {subject}");
            hash = null;
        }
        if (commits.Count < 2) return null;
        var kept = commits.Take(50).ToList();
        if (commits.Count > kept.Count) kept.Add($"… +{commits.Count - kept.Count} more commits");
        return string.Join("\n", kept);
    }

    private static string? DiffFilter(List<string> lines)
    {
        if (!lines.Any(l => l.StartsWith("@@ "))) return null;
        var output = new List<string>();
        var context = new List<string>();

        void FlushContext(bool trailing)
        {
            if (context.Count == 0) return;
            if (context.Count <= 2 || trailing && context.Count <= 1)
            {
                output.AddRange(context);
            }
            else
            {
                output.Add(context[0]);
                output.Add("  …");
                if (!trailing) output.Add(context[^1]);
            }
            context.Clear();
        }

        var sawChange = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ") || line.StartsWith("--- ") || line.StartsWith("+++ ")
                || line.StartsWith("@@ ") || line.StartsWith("index ") || line.StartsWith("Binary files"))
            {
                FlushContext(trailing: true);
                output.Add(line);
            }
            else if (line.StartsWith('+') || line.StartsWith('-') || line.StartsWith('\\'))
            {
                FlushContext(trailing: false);
                sawChange = true;
                output.Add(line);
            }
            else
            {
                context.Add(line);
            }
        }
        FlushContext(trailing: true);
        return sawChange ? string.Join("\n", output) : null;
    }

    private static string? GrepFilter(List<string> lines)
    {
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        if (nonEmpty.Count < 5) return null;
        var groups = new List<(string Path, List<string> Hits)>();
        var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var matched = 0;
        foreach (var line in nonEmpty)
        {
            var m = GrepLineRegex().Match(line);
            if (!m.Success) continue;
            matched++;
            var path = m.Groups[1].Value;
            if (!byPath.TryGetValue(path, out var index))
            {
                index = groups.Count;
                byPath[path] = index;
                groups.Add((path, []));
            }
            var hitText = m.Groups[3].Value.Trim();
            if (hitText.Length > 160) hitText = hitText[..160] + "…";
            groups[index].Hits.Add($"  {m.Groups[2].Value}: {hitText}");
        }
        if (matched < nonEmpty.Count * 0.6) return null;

        var builder = new StringBuilder();
        foreach (var (path, hits) in groups)
        {
            builder.Append(path).Append(" (").Append(hits.Count).Append("):\n");
            foreach (var hit in hits.Take(20)) builder.Append(hit).Append('\n');
            if (hits.Count > 20) builder.Append($"  … +{hits.Count - 20} more in {path}\n");
        }
        return builder.ToString().TrimEnd();
    }

    private static string? ListingFilter(List<string> lines)
    {
        var paths = new List<string>();
        var notes = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // Tree-drawing characters and ls detail columns mean the shape is not a plain list.
            if (line.Contains('│') || line.Contains('├') || line.Contains('└') || LsDetailRegex().IsMatch(line))
                return null;
            if (LooksLikePath(line)) paths.Add(line);
            else notes.Add(line);
        }
        if (paths.Count < 15 || paths.Count < (paths.Count + notes.Count) * 0.7) return null;

        var groups = new List<(string Dir, List<string> Names)>();
        var byDir = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/');
            var dir = slash >= 0 ? path[..slash] : ".";
            var name = slash >= 0 ? path[(slash + 1)..] : path;
            if (!byDir.TryGetValue(dir, out var index))
            {
                index = groups.Count;
                byDir[dir] = index;
                groups.Add((dir, []));
            }
            groups[index].Names.Add(name);
        }

        var builder = new StringBuilder();
        foreach (var (dir, names) in groups)
        {
            builder.Append(dir).Append("/: ").Append(string.Join(", ", names.Take(8)));
            if (names.Count > 8) builder.Append($", … ({names.Count} files)");
            builder.Append('\n');
        }
        foreach (var note in notes) builder.Append(note).Append('\n');
        return builder.ToString().TrimEnd();
    }

    private static bool LooksLikePath(string line)
        => !line.Contains("  ") && (line.Contains('/') || !line.Contains(' ') || line.Contains('.'));

    // ----- generic pass -----

    private static List<string> GenericPass(List<string> lines, bool dedupe)
    {
        var output = new List<string>(lines.Count);
        var blankRun = 0;
        string? lastLine = null;
        var repeatRun = 0;

        void FlushRepeats()
        {
            if (repeatRun > 3 && lastLine is not null)
            {
                // Three copies are already emitted (see below); note the collapsed rest.
                output.Add($"[… same line repeated {repeatRun - 3} more times]");
            }
            repeatRun = 0;
        }

        foreach (var raw in lines)
        {
            var line = AnsiRegex().Replace(raw.TrimEnd('\r'), "");
            if (line.Trim().Length == 0)
            {
                FlushRepeats();
                lastLine = null;
                if (++blankRun <= 1) output.Add("");
                continue;
            }
            blankRun = 0;
            if (dedupe && line == lastLine)
            {
                repeatRun++;
                if (repeatRun <= 3) output.Add(line);
                continue;
            }
            FlushRepeats();
            lastLine = line;
            repeatRun = 1;
            output.Add(line);
        }
        FlushRepeats();
        return output;
    }

    private static List<string> SplitLines(string text) => [.. text.Split('\n')];

    [GeneratedRegex(@"\[exit code: (?!0\])")]
    private static partial Regex ExitCodeFailedRegex();

    [GeneratedRegex(@"^(.+?):(\d+)[:\-](.*)$")]
    private static partial Regex GrepLineRegex();

    [GeneratedRegex(@"^[-d][rwx-]{9}\s")]
    private static partial Regex LsDetailRegex();

    [GeneratedRegex("\x1B\\[[0-9;?]*[A-Za-z]|\x1B\\][^\x07]*\x07")]
    private static partial Regex AnsiRegex();
}
