using System.Security.Cryptography;
using System.Text;

namespace Blazorly.Harness.Web.Services;

public sealed record WorkspaceNode(string Name, string RelativePath, bool IsDirectory);

public sealed record WorkspaceDocument(string RelativePath, string Text, bool Binary, bool TooLarge, long Bytes,
    string Version = "", bool Utf8Bom = false);

public sealed class WorkspaceConflictException() : InvalidOperationException(
    "This file changed on disk. Reload it before saving to avoid overwriting those changes.");

/// <summary>Bounded text editing inside a workspace. Child links and junctions are never followed.</summary>
public static class WorkspaceFiles
{
    public const int MaxBytes = 512 * 1024;
    private static readonly object WriteGate = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly string[] SkipDirs =
    [
        ".git", "node_modules", "bin", "obj", ".venv", "venv", "__pycache__",
        "dist", ".next", "target", ".cache", "vendor", "out", "coverage",
    ];

    public static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public static bool SameRoot(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(NormalizeRoot(left), NormalizeRoot(right), PathComparison);

    public static bool IsInside(string root, string candidate)
    {
        var rootFull = NormalizeRoot(root);
        var full = Path.GetFullPath(candidate);
        return string.Equals(full, rootFull, PathComparison)
            || full.StartsWith(Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar, PathComparison);
    }

    public static string Resolve(string root, string? relative)
    {
        var rootFull = NormalizeRoot(root);
        if (!Directory.Exists(rootFull)) throw new InvalidOperationException($"Workspace folder is missing: {rootFull}. Select an existing workspace from the switcher.");
        relative = (relative ?? "").Replace('\\', '/');
        if (Path.IsPathRooted(relative) || (OperatingSystem.IsWindows() && relative.Contains(':')))
            throw new InvalidOperationException("Choose a relative path inside the workspace.");
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(rootFull, full)) throw new InvalidOperationException("Path escapes the workspace.");

        var cursor = rootFull;
        foreach (var part in Path.GetRelativePath(rootFull, full).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            cursor = Path.Combine(cursor, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(cursor); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked files and folders cannot be opened in this workspace editor.");
        }
        return full;
    }

    public static IReadOnlyList<WorkspaceNode> List(string root, string? relative = null)
    {
        var dir = Resolve(root, relative);
        if (!Directory.Exists(dir)) throw new InvalidOperationException("This folder no longer exists.");
        var nodes = new List<WorkspaceNode>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(path);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || name.StartsWith(".blazorly-save-", StringComparison.Ordinal)) continue;
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (directory && SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            nodes.Add(new(name, Path.GetRelativePath(NormalizeRoot(root), path).Replace('\\', '/'), directory));
        }
        return nodes.OrderByDescending(n => n.IsDirectory).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static WorkspaceDocument Read(string root, string relative)
    {
        var path = Resolve(root, relative);
        if (Directory.Exists(path) || !File.Exists(path)) throw new InvalidOperationException("This file no longer exists.");
        var canonical = Path.GetRelativePath(NormalizeRoot(root), path).Replace('\\', '/');
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxBytes) return new(canonical, "", false, true, stream.Length);
        var buffer = new byte[MaxBytes + 1];
        var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (count > MaxBytes) return new(canonical, "", false, true, count);
        var bytes = buffer.AsSpan(0, count);
        var version = Convert.ToHexString(SHA256.HashData(bytes));
        var bom = bytes.StartsWith(Encoding.UTF8.Preamble);
        if (bytes.Contains((byte)0)) return new(canonical, "", true, false, count, version);
        try { return new(canonical, Utf8.GetString(bom ? bytes[3..] : bytes), false, false, count, version, bom); }
        catch (DecoderFallbackException) { return new(canonical, "", true, false, count, version); }
    }

    public static WorkspaceDocument Write(string root, string relative, string content, string? expectedVersion = null)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("Choose a file, not the workspace root.");
        lock (WriteGate)
        {
            var path = Resolve(root, relative);
            if (Directory.Exists(path)) throw new InvalidOperationException("That path is a folder.");
            var current = File.Exists(path) ? Read(root, relative) : null;
            if (expectedVersion is not null && current?.Version != expectedVersion) throw new WorkspaceConflictException();
            if (current is { Binary: true } or { TooLarge: true }) throw new InvalidOperationException("This file cannot be edited as UTF-8 text.");
            if (current is not null && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("This file is read-only.");
            var bytes = Utf8.GetBytes(content);
            if (current?.Utf8Bom == true) bytes = [.. Encoding.UTF8.Preamble, .. bytes];
            if (bytes.Length > MaxBytes) throw new InvalidOperationException("File is too large to save from the editor.");
            var parent = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(parent);
            Resolve(root, relative);
            var temporary = Path.Combine(parent, ".blazorly-save-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (!OperatingSystem.IsWindows() && current is not null)
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
                // Recheck after preparing the replacement; an agent may have written in between.
                if (expectedVersion is not null && (!File.Exists(path) || Read(root, relative).Version != expectedVersion))
                    throw new WorkspaceConflictException();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return new(Path.GetRelativePath(NormalizeRoot(root), path).Replace('\\', '/'), content,
                false, false, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), current?.Utf8Bom == true);
        }
    }
}
