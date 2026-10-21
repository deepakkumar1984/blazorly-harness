using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Blazorly.Harness.Web.Services;

/// <summary>The browser sends an id; execution always uses a freshly discovered command.</summary>
public sealed record ProjectCommand(
    string Id, string Label, string Detail, string Group, string CommandLine, string WorkingDirectory,
    string? Executable = null, string[]? Arguments = null);

/// <summary>Discovers common project entry points, including src/apps/packages layouts.</summary>
public static partial class ProjectCommandDiscovery
{
    private static readonly string[] PreferredScripts = ["dev", "start", "serve", "watch", "test", "build"];

    public static IReadOnlyList<ProjectCommand> Discover(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return [];
        var full = WorkspaceFiles.Resolve(root, "");
        var found = new List<ProjectCommand>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((full, 0));
        var visited = 0;
        while (pending.TryDequeue(out var next) && visited++ < 256)
        {
            var group = WorkspaceFiles.SameRoot(full, next.Path) ? "This folder" : Path.GetRelativePath(full, next.Path).Replace('\\', '/');
            ScanNode(full, next.Path, group, found);
            ScanDotnet(next.Path, group, found);
            ScanPython(next.Path, group, found);
            ScanPhp(next.Path, group, found);
            if (Exists(next.Path, "go.mod")) { Add(found, "go run", group, next.Path, "go", "run", "."); Add(found, "go test", group, next.Path, "go", "test", "./..."); }
            if (Exists(next.Path, "Cargo.toml")) { Add(found, "cargo run", group, next.Path, "cargo", "run"); Add(found, "cargo test", group, next.Path, "cargo", "test"); }
            if (Exists(next.Path, "Makefile") || Exists(next.Path, "makefile")) Add(found, "make", group, next.Path, "make");
            if (next.Depth >= 3) continue;
            foreach (var child in SafeDirectories(next.Path))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || WorkspaceFiles.SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (IsLink(child)) continue;
                pending.Enqueue((child, next.Depth + 1));
            }
        }
        return found;
    }

    public static ProjectCommand? Find(string root, string id) => Discover(root).FirstOrDefault(c => c.Id == id);

    public static string IdFor(string workingDirectory, string commandLine)
    {
        var cwd = WorkspaceFiles.NormalizeRoot(workingDirectory);
        if (OperatingSystem.IsWindows()) cwd = cwd.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cwd + "\n" + commandLine)))[..12].ToLowerInvariant();
    }

    private static void ScanNode(string root, string dir, string group, List<ProjectCommand> found)
    {
        using var doc = ReadJson(dir, "package.json");
        var hasScripts = false;
        if (doc is not null && doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
        {
            var manager = PackageManager(root, dir, doc.RootElement);
            foreach (var script in scripts.EnumerateObject()
                         .Where(p => p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                         .OrderBy(p => ScriptRank(p.Name)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!CanRunThroughWindowsShim(manager, script.Name)) continue;
                Add(found, script.Name, group, dir, manager, "run", script.Name);
                hasScripts = true;
            }
        }
        if (hasScripts) return;
        var entries = new List<string>();
        if (doc is not null && doc.RootElement.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.String
            && main.GetString() is { Length: > 0 } mainEntry) entries.Add(mainEntry);
        entries.AddRange(["server.js", "app.js", "index.js", "server.mjs", "app.mjs", "index.mjs", "server.cjs", "app.cjs", "index.cjs"]);
        foreach (var entry in entries.Distinct(StringComparer.Ordinal))
        {
            if (Path.GetExtension(entry) is not (".js" or ".mjs" or ".cjs")) continue;
            try
            {
                var file = WorkspaceFiles.Resolve(root, Path.GetRelativePath(root, Path.Combine(dir, entry)));
                if (File.Exists(file)) Add(found, entry, group, dir, "node", entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        }
    }

    private static void ScanDotnet(string dir, string group, List<ProjectCommand> found)
    {
        foreach (var solution in SafeFiles(dir, "*.sln").Concat(SafeFiles(dir, "*.slnx")))
        {
            var name = Path.GetFileName(solution);
            Add(found, $"build {name}", group, dir, "dotnet", "build", name);
            Add(found, $"test {name}", group, dir, "dotnet", "test", name);
        }
        foreach (var project in SafeFiles(dir, "*.csproj").Concat(SafeFiles(dir, "*.fsproj")).Concat(SafeFiles(dir, "*.vbproj")))
        {
            var text = ReadText(project);
            if (text is null) continue;
            XDocument document;
            try { document = XDocument.Parse(text); }
            catch (System.Xml.XmlException) { continue; }
            var name = Path.GetFileName(project);
            var elements = document.Descendants().ToList();
            var test = elements.Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                || elements.Any(e => e.Name.LocalName == "PackageReference" && string.Equals((string?)e.Attribute("Include"), "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                || TestProjectName().IsMatch(Path.GetFileNameWithoutExtension(name));
            var executable = elements.Any(e => e.Name.LocalName == "OutputType" && e.Value.Trim() is "Exe" or "WinExe")
                || ((string?)document.Root?.Attribute("Sdk"))?.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true;
            Add(found, $"build {name}", group, dir, "dotnet", "build", name);
            if (test) Add(found, $"test {name}", group, dir, "dotnet", "test", name);
            else if (executable) Add(found, $"run {name}", group, dir, "dotnet", "run", "--project", name);
        }
    }

    private static void ScanPython(string dir, string group, List<ProjectCommand> found)
    {
        var localPython = Path.Combine(dir, ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        var python = File.Exists(localPython) ? localPython : OperatingSystem.IsWindows() ? "python" : "python3";
        if (Exists(dir, "manage.py")) Add(found, "runserver", group, dir, python, "manage.py", "runserver");
        else foreach (var entry in new[] { "app.py", "main.py" })
            if (Exists(dir, entry)) Add(found, entry, group, dir, python, entry);
        var pyproject = ReadText(Path.Combine(dir, "pyproject.toml"));
        var project = pyproject is not null || Exists(dir, "requirements.txt") || Exists(dir, "pytest.ini") || Exists(dir, "setup.py");
        var tests = Directory.Exists(Path.Combine(dir, "tests")) || Directory.Exists(Path.Combine(dir, "test"))
            || SafeFiles(dir, "test_*.py").Count > 0 || pyproject?.Contains("pytest", StringComparison.OrdinalIgnoreCase) == true;
        if (project && tests) Add(found, "pytest", group, dir, python, "-m", "pytest");
    }

    private static void ScanPhp(string dir, string group, List<ProjectCommand> found)
    {
        if (Exists(dir, "artisan")) Add(found, "serve", group, dir, "php", "artisan", "serve");
        else if (Exists(dir, "index.php")) Add(found, "php server", group, dir, "php", "-S", "localhost:8080");
        using var doc = ReadJson(dir, "composer.json");
        if (doc is null || !doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) return;
        foreach (var script in scripts.EnumerateObject())
        {
            if (script.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array)) continue;
            if (CanRunThroughWindowsShim("composer", script.Name)) Add(found, script.Name, group, dir, "composer", "run-script", script.Name);
        }
    }

    private static void Add(List<ProjectCommand> found, string label, string group, string dir, string executable, params string[] args)
    {
        var commandLine = string.Join(" ", new[] { executable }.Concat(args).Select(Quote));
        var id = IdFor(dir, commandLine);
        if (found.Any(c => c.Id == id)) return;
        found.Add(new(id, label, commandLine, group, commandLine, Path.GetFullPath(dir), executable, args));
    }

    private static string PackageManager(string root, string dir, JsonElement manifest)
    {
        if (manifest.TryGetProperty("packageManager", out var manager) && manager.ValueKind == JsonValueKind.String)
        {
            var name = manager.GetString()?.Split('@')[0];
            if (name is "pnpm" or "npm" or "yarn" or "bun") return name;
        }
        for (string? cursor = dir; cursor is not null && WorkspaceFiles.IsInside(root, cursor); cursor = Directory.GetParent(cursor)?.FullName)
        {
            if (Exists(cursor, "pnpm-lock.yaml")) return "pnpm";
            if (Exists(cursor, "yarn.lock")) return "yarn";
            if (Exists(cursor, "bun.lock") || Exists(cursor, "bun.lockb")) return "bun";
            if (Exists(cursor, "package-lock.json")) return "npm";
            if (WorkspaceFiles.SameRoot(cursor, root)) break;
        }
        return "npm";
    }

    internal static bool UsesWindowsShim(string executable) => executable is "npm" or "pnpm" or "yarn" or "composer";

    private static bool CanRunThroughWindowsShim(string executable, string name) =>
        !OperatingSystem.IsWindows() || !UsesWindowsShim(executable) || !name.Any(c => "\"%!\r\n&|<>^".Contains(c));

    private static int ScriptRank(string name)
    {
        var index = Array.IndexOf(PreferredScripts, name);
        return index < 0 ? 100 : index;
    }

    private static JsonDocument? ReadJson(string dir, string name)
    {
        var text = ReadText(Path.Combine(dir, name));
        if (text is null) return null;
        try
        {
            var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) && !IsLink(path) && new FileInfo(path).Length <= WorkspaceFiles.MaxBytes ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool Exists(string dir, string name) => File.Exists(Path.Combine(dir, name)) && !IsLink(Path.Combine(dir, name));
    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static List<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).Where(p => !IsLink(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static List<string> SafeDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string Quote(string value) => SafeToken().IsMatch(value) ? value
        : OperatingSystem.IsWindows() ? "\"" + value.Replace("\"", "\\\"") + "\""
        : "'" + value.Replace("'", "'\\''") + "'";

    [GeneratedRegex("^[A-Za-z0-9_.:@/-]+$")]
    private static partial Regex SafeToken();
    [GeneratedRegex("(^|[._-])(tests?|specs?)([._-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TestProjectName();
}
