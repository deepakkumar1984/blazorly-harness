using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Blazorly.Harness.Core.Harnessing;

/// <summary>The deliberately limited behavioral surface an experiment may change.</summary>
public sealed record HarnessProfileSettings
{
    public bool? TokenSaverToolOutput { get; init; }
    public bool? TokenSaverTerseOutput { get; init; }
    public bool? TokenSaverMinimalCode { get; init; }
    public string? AnswerStyle { get; init; }
    public double? CompactionThreshold { get; init; }
    public int? CompactionPrunerChars { get; init; }
}

/// <summary>A portable, content-addressed configuration. Contains no routes, credentials or permissions.</summary>
public sealed record HarnessProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "baseline";
    public string? ParentId { get; init; }
    public string Instructions { get; init; } = "";
    public string ToolGuidance { get; init; } = "";
    public HarnessProfileSettings Settings { get; init; } = new();
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    public static HarnessProfile Baseline => new();
    public static HarnessProfile Compression => new()
    {
        Name = "compressed-output",
        Settings = new() { TokenSaverToolOutput = true },
    };

    public static HarnessProfile Terse => new()
    {
        Name = "terse-output",
        Settings = new() { TokenSaverTerseOutput = true },
    };

    [JsonIgnore]
    public string Id => HarnessArtifacts.Hash(JsonSerializer.SerializeToElement(new
    {
        SchemaVersion,
        Instructions,
        ToolGuidance,
        Settings,
        Files,
    }, HarnessArtifacts.Json));

    public static HarnessProfile Load(string path) => Parse(File.ReadAllText(path));

    public static HarnessProfile Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        HarnessArtifacts.RejectDuplicateKeys(document.RootElement);
        var profile = JsonSerializer.Deserialize<HarnessProfile>(json, HarnessArtifacts.Json)
            ?? throw new ArgumentException("profile must be an object");
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("unsupported profile schemaVersion (expected 1)");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new ArgumentException("profile name must contain 1–80 characters");
        if (ParentId is not null && !Regex.IsMatch(ParentId, "^[a-f0-9]{64}$")) throw new ArgumentException("parentId must be a profile SHA-256 id");
        if (Instructions is null || ToolGuidance is null || Settings is null || Files is null)
            throw new ArgumentException("profile instructions, toolGuidance, settings and files cannot be null");
        if (Instructions.Length + ToolGuidance.Length > 24_000) throw new ArgumentException("profile instructions and guidance exceed 24,000 characters");
        if (Files.Count > 64 || Files.Sum(f => f.Value?.Length ?? 0) > 200_000)
            throw new ArgumentException("profile package exceeds 64 files or 200,000 characters");
        if (Settings.AnswerStyle is { } style && style is not ("simple" or "layered" or "technical"))
            throw new ArgumentException("answerStyle must be simple, layered or technical");
        if (Settings.CompactionThreshold is { } threshold && (!double.IsFinite(threshold) || threshold < 0.1 || threshold > 0.95))
            throw new ArgumentException("compactionThreshold must be between 0.1 and 0.95");
        if (Settings.CompactionPrunerChars is { } chars && chars is < 256 or > 20_000)
            throw new ArgumentException("compactionPrunerChars must be between 256 and 20,000");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, body) in Files)
        {
            if (!IsPackagePath(path) || !names.Add(path)) throw new ArgumentException($"invalid or colliding profile path: {path}");
            if (body is null) throw new ArgumentException($"profile file cannot be null: {path}");
            CheckHelperSyntax(path, body);
            if (path.EndsWith("/SKILL.md", StringComparison.Ordinal))
            {
                var folder = path.Split('/')[1];
                var frontmatter = Regex.Match(body, "\\A---\\r?\\n(?<body>.*?)\\r?\\n---(?:\\r?\\n|$)", RegexOptions.Singleline);
                if (!frontmatter.Success || !Regex.IsMatch(frontmatter.Groups["body"].Value, $"^name:\\s*{Regex.Escape(folder)}\\s*$", RegexOptions.Multiline)
                    || !Regex.IsMatch(frontmatter.Groups["body"].Value, "^description:\\s*\\S", RegexOptions.Multiline))
                    throw new ArgumentException($"{path} requires frontmatter name matching its folder and a description");
            }
            CheckReferences(path, body);
        }
        foreach (var file in Files.Keys.Where(p => p.StartsWith("skills/", StringComparison.Ordinal)))
        {
            var skill = "skills/" + file.Split('/')[1] + "/SKILL.md";
            if (!Files.ContainsKey(skill)) throw new ArgumentException($"{file} has no packaged SKILL.md");
        }
        CheckReferences("instructions", Instructions);
        CheckReferences("toolGuidance", ToolGuidance);
    }

    public static bool IsPackagePath(string path) => Regex.IsMatch(path,
        "^(scripts/[a-z0-9][a-z0-9_.-]*\\.(py|sh|csx|js|mjs|json|md|txt)|skills/[a-z0-9][a-z0-9_-]*/(SKILL\\.md|scripts/[a-z0-9][a-z0-9_.-]*\\.(py|sh|csx|js|mjs|json|md|txt)))$");

    private static void CheckHelperSyntax(string path, string body)
    {
        var extension = Path.GetExtension(path);
        if (extension is ".py" or ".sh" or ".js" or ".mjs")
        {
            var error = InterpreterSyntax(extension, body);
            if (error is not null) throw new ArgumentException($"{path}: {error}");
            return;
        }
        if (extension != ".csx") return;
        var tree = CSharpSyntaxTree.ParseText(body, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse, SourceCodeKind.Script));
        var diagnostic = tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (diagnostic is not null) throw new ArgumentException($"{path}: {diagnostic.GetMessage()}");
    }

    private static string? InterpreterSyntax(string extension, string body)
    {
        var file = Path.Combine(Path.GetTempPath(), "blz-syntax-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.WriteAllText(file, body, new UTF8Encoding(false));
            var (program, args) = extension switch
            {
                ".py" => ("python3", new[] { "-c", "import ast,sys; ast.parse(open(sys.argv[1], encoding='utf-8').read())", file }),
                ".sh" => ("bash", new[] { "-n", file }),
                _ => ("node", new[] { "--check", file }),
            };
            var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new ArgumentException($"{program} could not be started");
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return $"{program} syntax check timed out";
            }
            if (process.ExitCode == 0) return null;
            var stderr = process.StandardError.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(stderr) ? $"{program} rejected the helper (exit {process.ExitCode})" : stderr;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return ex.Message;
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private void CheckReferences(string source, string body)
    {
        foreach (Match match in Regex.Matches(body, "(?<![a-zA-Z0-9_/])(?:skills/[a-z0-9][a-z0-9_-]*/)?scripts/[a-z0-9][a-z0-9_.-]*\\.(?:py|sh|csx|js|mjs)"))
        {
            var relative = match.Value;
            var local = source.StartsWith("skills/", StringComparison.Ordinal)
                ? "skills/" + source.Split('/')[1] + "/" + relative : relative;
            if (!Files.ContainsKey(relative) && !Files.ContainsKey(local))
                throw new ArgumentException($"{source} references missing packaged script {relative}");
        }
    }

    public void Save(string path)
    {
        Validate();
        HarnessArtifacts.WriteJson(path, this);
    }

    /// <summary>Assets live inside the trial workspace so confined tools can use them.</summary>
    public string Materialize(string workspace)
    {
        Validate();
        var root = PackageRoot(workspace);
        foreach (var (relative, body) in Files)
        {
            var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            HarnessArtifacts.AssertNoLinks(workspace, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, body, new UTF8Encoding(false));
        }
        return root;
    }

    public string PackageRoot(string workspace) => Path.Combine(Path.GetFullPath(workspace), ".blazorly", "harness-profiles", Id);

    public void VerifyMaterialized(string workspace)
    {
        var root = PackageRoot(workspace);
        var actual = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal) : [];
        if (!actual.SetEquals(Files.Keys) || Files.Any(p => File.ReadAllText(Path.Combine(root, p.Key)) != p.Value))
            throw new ArgumentException("profile package was changed during the trial");
    }

    public string Render(string packageRoot) => string.Join("\n\n", new[]
    {
        Instructions, ToolGuidance,
        Files.Count == 0 ? "" : $"Harness profile package: {packageRoot}\nResolve packaged scripts and skill references against this directory. Use search_skills then skill to load relevant skills.",
    }.Where(s => !string.IsNullOrWhiteSpace(s))).Replace("{{profileRoot}}", packageRoot, StringComparison.Ordinal);
}

public static class HarnessArtifacts
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value)))).ToLowerInvariant();
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText(),
    };

    public static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new ArgumentException($"duplicate JSON property: {p.Name}");
                RejectDuplicateKeys(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }

    public static void WriteJson<T>(string path, T value)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void AssertNoLinks(string workspace, string target)
    {
        var root = Path.GetFullPath(workspace);
        var full = Path.GetFullPath(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("profile path escapes workspace");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"profile path traverses a symbolic link: {current}");
        }
    }
}
