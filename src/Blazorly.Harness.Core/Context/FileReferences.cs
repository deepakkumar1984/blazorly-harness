using System.Text;
using Blazorly.Harness.Core.Attachments;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Context;

/// <summary>An "@token" found in user text (token excludes the leading @).</summary>
public sealed record FileReferenceToken(string Token);

/// <summary>One attached file: what was referenced and how it was handled.</summary>
public sealed record FileReferenceAttachment(string Token, string Path, string Kind, string? Note = null);

/// <summary>Expansion outcome: message content blocks plus a report of every reference.</summary>
public sealed record FileReferenceResult(IReadOnlyList<ContentBlock> Blocks, IReadOnlyList<FileReferenceAttachment> Attached)
{
    public static FileReferenceResult Plain(string text) => new([new TextBlock(text)], []);
}

/// <summary>A file/directory candidate for @-mention autocomplete.</summary>
public sealed record FileCandidate(string Path, bool IsDir, long Size);

/// <summary>
/// "@path" references in user text: parses tokens, resolves them against the session cwd,
/// and expands them into message content blocks (text bodies, images via the attachment
/// store, or notices for misses/binary/oversized files). Never throws — a bad reference
/// becomes a notice so the model knows why the file is absent.
/// </summary>
public static class FileReferences
{
    public const int MaxTextBytes = 262_144; // 256 KB of text per referenced file
    public const int MaxImageBytes = 8 * 1024 * 1024;
    private const int SniffBytes = 8192;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp",
    };

    private static readonly HashSet<string> JunkDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".venv", "__pycache__", "dist", ".next", "target",
    };

    /// <summary>Finds @tokens: a @ at string start or after whitespace, followed by path-ish
    /// characters (letters, digits, . / - _ + ~). Mid-word @ (emails, handles) never matches.</summary>
    public static IReadOnlyList<FileReferenceToken> Parse(string text)
    {
        var tokens = new List<FileReferenceToken>();
        var i = 0;
        while (i < text.Length)
        {
            var at = text.IndexOf('@', i);
            if (at < 0) break;
            var start = at + 1;
            if (at > 0 && !char.IsWhiteSpace(text[at - 1]))
            {
                i = start;
                continue;
            }
            var end = start;
            while (end < text.Length && IsTokenChar(text[end])) end++;
            if (end > start)
            {
                tokens.Add(new FileReferenceToken(text[start..end]));
                i = end;
            }
            else
            {
                i = start;
            }
        }
        return tokens;
    }

    private static bool IsTokenChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '.' or '/' or '-' or '_' or '+' or '~';

    /// <summary>Resolves a token against the session cwd; ~ expands to the user profile.</summary>
    public static string Resolve(string token, string cwd)
    {
        var expanded = token.StartsWith("~/", StringComparison.Ordinal) || token == "~"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), token == "~" ? "" : token[2..])
            : token;
        return Path.GetFullPath(expanded, string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd);
    }

    /// <summary>Expands @references in user text into content blocks. The first block is always
    /// the text as typed; attachments and notices follow. Duplicate paths attach once.</summary>
    public static async Task<FileReferenceResult> ExpandAsync(
        string text,
        string cwd,
        string sessionId,
        AttachmentService? attachments,
        CancellationToken ct = default)
    {
        var tokens = Parse(text);
        if (tokens.Count == 0) return FileReferenceResult.Plain(text);

        var blocks = new List<ContentBlock> { new TextBlock(text) };
        var attached = new List<FileReferenceAttachment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            ct.ThrowIfCancellationRequested();
            string path;
            try
            {
                path = Resolve(token.Token, cwd);
            }
            catch (Exception ex)
            {
                attached.Add(new FileReferenceAttachment(token.Token, token.Token, "error", $"unresolvable path ({ex.Message})"));
                continue;
            }
            if (!seen.Add(path)) continue; // the same file attaches once per message
            try
            {
                await ExpandOneAsync(blocks, attached, token.Token, path, sessionId, attachments, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                attached.Add(new FileReferenceAttachment(token.Token, path, "error", ex.Message));
                blocks.Add(new TextBlock($"\n@{token.Token}: could not be read ({ex.Message})"));
            }
        }
        return attached.Count == 0 ? FileReferenceResult.Plain(text) : new FileReferenceResult(blocks, attached);
    }

    private static async Task ExpandOneAsync(
        List<ContentBlock> blocks,
        List<FileReferenceAttachment> attached,
        string token,
        string path,
        string sessionId,
        AttachmentService? attachments,
        CancellationToken ct)
    {
        if (Directory.Exists(path))
        {
            attached.Add(new FileReferenceAttachment(token, path, "directory", "directories are not attached"));
            blocks.Add(new TextBlock($"\n@{token}: a directory; individual files must be referenced to be attached"));
            return;
        }
        if (!File.Exists(path))
        {
            // A bare word is usually prose ("@here", "@everyone") — ignore silently. Anything
            // path-shaped (a slash, or a dotted name) gets a notice so the model does not
            // hallucinate the file's contents.
            if (!token.Contains('/') && !token.Contains('.'))
            {
                attached.Add(new FileReferenceAttachment(token, path, "missing", null));
                return;
            }
            attached.Add(new FileReferenceAttachment(token, path, "missing", "file not found"));
            blocks.Add(new TextBlock($"\n@{token}: file not found (checked {path})"));
            return;
        }

        var info = new FileInfo(path);
        if (ImageExtensions.Contains(Path.GetExtension(path)))
        {
            if (info.Length > MaxImageBytes)
            {
                attached.Add(new FileReferenceAttachment(token, path, "image", $"exceeds the {MaxImageBytes / (1024 * 1024)} MB image cap"));
                blocks.Add(new TextBlock($"\n@{token}: image is {info.Length} bytes, over the {MaxImageBytes / (1024 * 1024)} MB cap — not attached"));
                return;
            }
            if (attachments is null)
            {
                attached.Add(new FileReferenceAttachment(token, path, "image", "no attachment store mounted"));
                blocks.Add(new TextBlock($"\n@{token}: image skipped (no attachment store mounted)"));
                return;
            }
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var id = await attachments.SaveAsync(sessionId, bytes, MimeOf(path), ct).ConfigureAwait(false);
            attached.Add(new FileReferenceAttachment(token, path, "image", null));
            blocks.Add(new TextBlock($"\n@{token}: image attached ({bytes.Length} bytes, {MimeOf(path)})."));
            blocks.Add(new ImageBlock(id, MimeOf(path)));
            return;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[MaxTextBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        if (LooksBinary(buffer, Math.Min(read, SniffBytes)))
        {
            attached.Add(new FileReferenceAttachment(token, path, "binary", "binary files are not attached as text"));
            blocks.Add(new TextBlock($"\n@{token}: binary file ({info.Length} bytes) — not attached; use the read_image or bash tool instead"));
            return;
        }
        var truncated = read > MaxTextBytes;
        var content = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, MaxTextBytes));
        var lineCount = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (truncated ? 0 : content.EndsWith('\n') ? 0 : 1);
        attached.Add(new FileReferenceAttachment(token, path, "text", truncated ? $"first {MaxTextBytes / 1024} KB of {info.Length} bytes" : null));
        var linesLabel = $"{lineCount}{(truncated ? "+" : "")} lines{(truncated ? $", truncated from {info.Length} bytes" : "")}";
        var header = $"\n--- @{token} ({path}, {linesLabel}) ---\n";
        blocks.Add(new TextBlock(header + content + (truncated ? "\n… (truncated)" : "") + $"\n--- end @{token} ---"));
    }

    /// <summary>Autocomplete candidates under cwd matching query (case-insensitive substring on
    /// the relative path). Bounded walk, junk directories skipped, best matches first.</summary>
    public static IReadOnlyList<FileCandidate> ListCandidates(string cwd, string query, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            var needle = query.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            if (needle.Length == 0) return [];
            var entries = new List<FileCandidate>();
            Walk(new DirectoryInfo(cwd), string.Empty, entries, 5_000);
            var ranked = new List<(FileCandidate Entry, int Score, int Length)>();
            foreach (var entry in entries)
            {
                var rel = entry.Path.Replace('\\', '/').ToLowerInvariant();
                if (!rel.Contains(needle, StringComparison.Ordinal)) continue;
                var name = Path.GetFileName(entry.Path.TrimEnd('/')).ToLowerInvariant();
                var score = name.StartsWith(needle, StringComparison.Ordinal) ? 2
                    : rel.StartsWith(needle, StringComparison.Ordinal) ? 1 : 0;
                ranked.Add((entry, score, rel.Length));
            }
            return [.. ranked
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Length)
                .Select(r => r.Entry)
                .Take(max)];
        }
        catch
        {
            return []; // enumeration failures just yield no candidates
        }
    }

    private static void Walk(DirectoryInfo dir, string prefix, List<FileCandidate> into, int cap)
    {
        if (into.Count >= cap) return;
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = dir.EnumerateFileSystemInfos();
        }
        catch
        {
            return; // unreadable directories are skipped
        }
        foreach (var child in children)
        {
            if (into.Count >= cap) return;
            if (child is DirectoryInfo d)
            {
                if (JunkDirs.Contains(d.Name)) continue;
                into.Add(new FileCandidate(prefix + d.Name + "/", true, 0));
                Walk(d, prefix + d.Name + "/", into, cap);
            }
            else
            {
                into.Add(new FileCandidate(prefix + child.Name, false, child is FileInfo f ? f.Length : 0));
            }
        }
    }

    private static bool LooksBinary(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == 0) return true;
        }
        return false;
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };
}
