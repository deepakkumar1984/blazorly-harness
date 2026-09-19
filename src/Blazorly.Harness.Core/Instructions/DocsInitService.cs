using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Blazorly.Harness.Core.Instructions;

/// <summary>
/// Deterministic workspace-documentation generator behind <c>blazorly init</c> and the web
/// "Generate docs" action. Scans the repo tree (no LLM in the loop, so every emitted path
/// is derived from files that exist) and drafts hierarchical AGENTS.md docs: a short root
/// index plus one file per directory down to the requested depth. Re-runs preserve the
/// <c>&lt;!-- MANUAL: --&gt;</c> tail and bump the Updated stamp instead of regenerating it.
/// </summary>
public static partial class DocsInitService
{
    public const string ManualMarker = "<!-- MANUAL:";
    public const int DefaultDepth = 0;

    /// <summary>Root doc budget: kept small so the index survives the 24k project-instructions budget next to subdir docs.</summary>
    public const int RootBudgetChars = 6_000;

    public static readonly IReadOnlyList<string> ExcludedDirs =
        ["node_modules", ".git", "bin", "obj", "dist", "build", "__pycache__", ".venv",
         "coverage", ".next", ".nuxt", "target", "vendor", "TestResults"];

    /// <summary>Workspace junk: never listed as key files, never counted, never briefed.</summary>
    public static readonly IReadOnlyList<string> ExcludedFiles =
        [".ds_store", "thumbs.db", "desktop.ini"];

    public static bool IsExcludedFile(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return ExcludedFiles.Contains(lower, StringComparer.Ordinal)
            || lower.EndsWith(".log", StringComparison.Ordinal);
    }

    public sealed record InitFilePlan(string RelativePath, string FullPath, string Content, bool Exists, bool Changed);
    public sealed record StaleReference(string DocPath, int Line, string Reference);
    public sealed record InitPreview(string Root, IReadOnlyList<InitFilePlan> Files, IReadOnlyList<StaleReference> StaleReferences);
    public sealed record InitApplyResult(string Root, IReadOnlyList<string> Written, IReadOnlyList<string> Unchanged, IReadOnlyList<StaleReference> StaleReferences);

    internal static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Dry run: planned docs plus stale path references in existing instruction files. Writes nothing.</summary>
    public static InitPreview Preview(string root, int depth = DefaultDepth)
    {
        var full = Path.GetFullPath(root);
        var dirs = Walk(full, depth);
        var planned = dirs.ToHashSet(StringComparer.Ordinal);
        var plans = dirs.Select(d => Plan(d, full, planned)).ToList();
        return new InitPreview(full, plans, Verify(full, depth));
    }

    /// <summary>Writes planned docs. Without <paramref name="force"/>, existing AGENTS.md files are merged (manual tail kept).</summary>
    public static InitApplyResult Apply(string root, int depth = DefaultDepth, bool force = false)
    {
        var preview = Preview(root, depth);
        var written = new List<string>();
        var unchanged = new List<string>();
        foreach (var plan in preview.Files)
        {
            var content = plan.Exists && !force ? MergeManual(plan.FullPath, plan.Content) : plan.Content;
            if (plan.Exists && !force && FilesEqual(plan.FullPath, content))
            {
                unchanged.Add(plan.RelativePath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(plan.FullPath)!);
            File.WriteAllText(plan.FullPath, content, FileEncoding);
            written.Add(plan.RelativePath);
        }
        return new InitApplyResult(preview.Root, written, unchanged, preview.StaleReferences);
    }

    /// <summary>Factual grounding brief for AI drafting: tree, manifests, commands. Capped for prompts.</summary>
    public const int BriefBudgetChars = 10_000;

    public sealed record RepoBrief(string Root, string Text);

    public static RepoBrief BuildBrief(string root, int depth = DefaultDepth)
    {
        var full = Path.GetFullPath(root);
        var body = new StringBuilder();
        body.AppendLine($"Workspace: {Path.GetFileName(full)} ({full})");
        body.AppendLine($"Stack: {DescribeStackDeep(full)}");
        var rootLead = ReadmeLead(full);
        if (rootLead is not null) body.AppendLine($"Root README: {rootLead}");
        foreach (var dir in Walk(full, Math.Max(depth, 1)))
        {
            var rel = Path.GetRelativePath(full, dir).Replace(Path.DirectorySeparatorChar, '/');
            if (rel == ".") rel = "(root)";
            body.AppendLine();
            body.AppendLine($"## {rel}/ — {DescribeStack(dir)}");
            var lead = dir == full ? null : ReadmeLead(dir);
            if (lead is not null) body.AppendLine($"README: {lead}");
            var manifest = DescribeManifest(dir);
            if (manifest is not null) body.AppendLine($"Manifest: {manifest}");
            var keys = RankFiles(dir).Take(8).Select(f => f.File).ToList();
            if (keys.Count > 0) body.AppendLine($"Key files: {string.Join(", ", keys)}");
            var subs = ListDirs(dir).Select(Path.GetFileName).ToList();
            if (subs.Count > 0) body.AppendLine($"Subdirs: {string.Join(", ", subs)} ({CountFiles(dir)} files)");
            foreach (var (label, command) in DetectCommands(dir))
                body.AppendLine($"Verified command ({label}): {command}");
            var deps = DetectDependencies(dir).Take(10).ToList();
            if (deps.Count > 0) body.AppendLine($"Dependencies: {string.Join(", ", deps)}");
            if (body.Length > BriefBudgetChars) break;
        }
        var text = body.ToString();
        if (text.Length > BriefBudgetChars) text = text[..BriefBudgetChars] + "\n[… brief truncated …]";
        return new RepoBrief(full, text);
    }

    private static string? DescribeManifest(string dir)
    {
        var packageJson = Path.Combine(dir, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                var root = doc.RootElement;
                var name = root.TryGetProperty("name", out var n) ? n.GetString() : Path.GetFileName(dir);
                var scripts = root.TryGetProperty("scripts", out var s)
                    ? string.Join(", ", s.EnumerateObject().Select(p => p.Name).Take(8)) : "";
                var deps = new List<string>();
                foreach (var section in new[] { "dependencies", "devDependencies" })
                    if (root.TryGetProperty(section, out var d))
                        deps.AddRange(d.EnumerateObject().Select(p => p.Name));
                return $"package {name}" + (scripts.Length > 0 ? $"; scripts: {scripts}" : "")
                    + (deps.Count > 0 ? $"; deps: {string.Join(", ", deps.Take(10))}" : "");
            }
            catch { }
        }
        var csproj = SafeEnumerate(dir, "*.csproj").FirstOrDefault();
        if (csproj is not null) return $"{Path.GetFileName(csproj)} (.NET)";
        if (File.Exists(Path.Combine(dir, "Cargo.toml"))) return "Cargo.toml (Rust)";
        if (File.Exists(Path.Combine(dir, "go.mod"))) return "go.mod (Go)";
        if (File.Exists(Path.Combine(dir, "pyproject.toml"))) return "pyproject.toml (Python)";
        return null;
    }

    /// <summary>Checks one markdown document's path references against a directory (used on AI drafts).</summary>
    public static IReadOnlyList<StaleReference> VerifyContent(string docDir, string docPath, string content)
    {
        var stale = new List<StaleReference>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            foreach (var reference in ExtractPathReferences(lines[i]))
                if (!Resolves(docDir, reference))
                    stale.Add(new StaleReference(docPath, i + 1, reference));
        return stale;
    }

    /// <summary>Flags path-like references in existing instruction files that resolve to nothing.</summary>
    public static IReadOnlyList<StaleReference> Verify(string root, int depth = DefaultDepth)
    {
        var full = Path.GetFullPath(root);
        var stale = new List<StaleReference>();
        foreach (var dir in Walk(full, depth))
        {
            foreach (var name in ProjectInstructionsService.FileNames)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                string content;
                try { content = File.ReadAllText(path); }
                catch { continue; }
                stale.AddRange(VerifyContent(dir, path, content));
            }
        }
        return stale;
    }

    internal static List<string> WalkDirs(string fullRoot, int depth) => Walk(fullRoot, depth);

    internal static bool ContentsEqual(string path, string content) => FilesEqual(path, content);

    // ---- planning ----

    private static InitFilePlan Plan(string dir, string root, HashSet<string> planned)
    {
        var isRoot = string.Equals(dir, root, StringComparison.Ordinal);
        var content = isRoot ? RenderRoot(dir, planned) : RenderDir(dir, root, planned);
        var full = Path.Combine(dir, "AGENTS.md");
        var exists = File.Exists(full);
        var changed = !exists || !FilesEqual(full, MergeManual(full, content));
        var relative = Path.GetRelativePath(root, full);
        return new InitFilePlan(relative, full, content, exists, changed);
    }

    private static string RenderRoot(string root, HashSet<string> planned)
    {
        var name = Path.GetFileName(root);
        var body = new StringBuilder();
        body.AppendLine($"<!-- Generated: {Stamp()} by blazorly init -->");
        body.AppendLine();
        body.AppendLine($"# {name}");
        body.AppendLine();
        body.AppendLine("## Purpose");
        body.AppendLine(ReadmeLead(root) ?? $"{name} — {DescribeStackDeep(root)}.");
        body.AppendLine();
        AppendKeyFiles(body, root);
        AppendSubdirs(body, root, detailed: false, planned);
        AppendAgentNotes(body, root);
        AppendDependencies(body, root);
        body.AppendLine(ManualMarker + " Custom project notes below this line are preserved on regeneration -->");
        return EnforceBudget(body.ToString());
    }

    private static string RenderDir(string dir, string root, HashSet<string> planned)
    {
        var name = Path.GetFileName(dir);
        var rel = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
        var body = new StringBuilder();
        body.AppendLine("<!-- Parent: ../AGENTS.md -->");
        body.AppendLine($"<!-- Generated: {Stamp()} by blazorly init -->");
        body.AppendLine();
        body.AppendLine($"# {name}");
        body.AppendLine();
        body.AppendLine("## Purpose");
        body.AppendLine(ReadmeLead(dir) ?? $"{name}/ — {DescribeStack(dir)} module ({rel}/).");
        body.AppendLine();
        AppendKeyFiles(body, dir);
        AppendSubdirs(body, dir, detailed: true, planned);
        AppendAgentNotes(body, dir);
        body.AppendLine(ManualMarker + " -->");
        return body.ToString();
    }

    private static void AppendKeyFiles(StringBuilder body, string dir)
    {
        var files = RankFiles(dir).Take(12).ToList();
        if (files.Count == 0) return;
        body.AppendLine("## Key Files");
        body.AppendLine("| File | Description |");
        body.AppendLine("|------|-------------|");
        foreach (var (file, description) in files)
            body.AppendLine($"| `{file}` | {description} |");
        body.AppendLine();
    }

    private static void AppendSubdirs(StringBuilder body, string dir, bool detailed, HashSet<string> planned)
    {
        var subdirs = ListDirs(dir).Take(20).ToList();
        if (subdirs.Count == 0) return;
        body.AppendLine("## Subdirectories");
        body.AppendLine("| Directory | Purpose |");
        body.AppendLine("|-----------|---------|");
        foreach (var sub in subdirs)
        {
            var hint = ReadmeLead(sub) ?? DescribeStack(sub);
            var count = CountFiles(sub);
            var suffix = !detailed && planned.Contains(sub) ? $" (see `{Path.GetFileName(sub)}/AGENTS.md`)" : "";
            body.AppendLine($"| `{Path.GetFileName(sub)}/` | {Truncate(hint, 90)}{suffix} — {count} files |");
        }
        body.AppendLine();
    }

    private static void AppendAgentNotes(StringBuilder body, string dir)
    {
        var commands = DetectCommands(dir);
        if (commands.Count == 0) return;
        body.AppendLine("## For AI Agents");
        body.AppendLine();
        body.AppendLine("### Verified commands (detected from manifests in this directory)");
        foreach (var (label, command) in commands)
            body.AppendLine($"- `{command}` — {label}");
        body.AppendLine("- Verify file paths against the workspace before referencing them; never assume renamed or moved paths.");
        body.AppendLine();
    }

    private static void AppendDependencies(StringBuilder body, string dir)
    {
        var deps = DetectDependencies(dir).Take(12).ToList();
        if (deps.Count == 0) return;
        body.AppendLine("## Dependencies");
        body.AppendLine();
        body.AppendLine("### External (direct, from manifests)");
        foreach (var dep in deps)
            body.AppendLine($"- `{dep}`");
        body.AppendLine();
    }

    // ---- discovery ----

    private static List<string> Walk(string root, int depth)
    {
        var result = new List<string> { root };
        if (!Directory.Exists(root) || depth < 1) return result;
        var frontier = new List<(string Dir, int Level)> { (root, 0) };
        while (frontier.Count > 0)
        {
            var (dir, level) = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            if (level >= depth) continue;
            foreach (var sub in ListDirs(dir))
            {
                if (IsEmpty(sub) && level + 1 >= depth) continue;
                result.Add(sub);
                frontier.Add((sub, level + 1));
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static List<string> ListDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Where(d => !ExcludedDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    private static bool IsEmpty(string dir)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch { return true; }
    }

    internal static int CountFiles(string dir)
    {
        // Manual recursion so excluded dirs (node_modules, .git, …) and junk files are not counted.
        var count = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        try
        {
            while (stack.Count > 0 && count <= 100_000)
            {
                var current = stack.Pop();
                foreach (var file in Directory.EnumerateFiles(current))
                    if (!IsExcludedFile(Path.GetFileName(file))) count++;
                foreach (var sub in ListDirs(current)) stack.Push(sub);
            }
        }
        catch { }
        return count;
    }

    private static List<(string File, string Description)> RankFiles(string dir)
    {
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir).Select(Path.GetFileName)
                .Where(f => f is not null && !IsExcludedFile(f)).Cast<string>().ToList();
        }
        catch { return []; }
        return files
            .Select(f => (File: f, Description: DescribeFile(dir, f), Rank: RankFile(f)))
            .OrderBy(t => t.Rank)
            .ThenBy(t => t.File, StringComparer.OrdinalIgnoreCase)
            .Select(t => (t.File, t.Description))
            .ToList();
    }

    private static int RankFile(string file)
    {
        var lower = file.ToLowerInvariant();
        if (lower is "readme.md" or "package.json" or "cargo.toml" or "go.mod" or "pyproject.toml" or "pom.xml" or "build.gradle"
            || lower.EndsWith(".slnx") || lower.EndsWith(".sln") || lower.EndsWith(".csproj")) return 0;
        if (lower is ".env.example" or "dockerfile" or "docker-compose.yml" or "docker-compose.yaml" or "makefile") return 1;
        if (lower is "tsconfig.json" or "vite.config.ts" or "eslint.config.js" or ".editorconfig") return 2;
        var stem = Path.GetFileNameWithoutExtension(lower);
        if (stem is "index" or "main" or "mod" or "lib" or "app" or "program" or "server" or "cli") return 3;
        if (lower is "agents.md" or "claude.md") return 9;
        return 4;
    }

    private static string DescribeFile(string dir, string file)
    {
        var lower = file.ToLowerInvariant();
        if (KnownFiles.TryGetValue(lower, out var known)) return known;
        if (lower.EndsWith(".slnx") || lower.EndsWith(".sln")) return ".NET solution";
        if (lower.EndsWith(".csproj")) return ".NET project";
        if (lower is "agents.md" or "claude.md") return "Project instructions for AI agents";
        var ext = Path.GetExtension(lower);
        if (SourceExt.TryGetValue(ext, out var lang)) return $"{lang} source";
        long size = 0;
        try { size = new FileInfo(Path.Combine(dir, file)).Length; } catch { }
        return $"{FormatSize(size)} file";
    }

    private static readonly Dictionary<string, string> KnownFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["readme.md"] = "Project overview",
        ["package.json"] = "Node dependencies and scripts",
        ["package-lock.json"] = "Locked Node dependency tree",
        ["tsconfig.json"] = "TypeScript configuration",
        ["cargo.toml"] = "Rust manifest",
        ["go.mod"] = "Go module definition",
        ["pyproject.toml"] = "Python project manifest",
        ["requirements.txt"] = "Pinned Python dependencies",
        ["pom.xml"] = "Maven build definition",
        ["build.gradle"] = "Gradle build definition",
        [".env.example"] = "Environment variable template",
        ["dockerfile"] = "Container image definition",
        ["docker-compose.yml"] = "Container orchestration",
        ["docker-compose.yaml"] = "Container orchestration",
        ["makefile"] = "Build shortcuts",
        [".editorconfig"] = "Editor formatting rules",
    };

    private static readonly Dictionary<string, string> SourceExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#", [".ts"] = "TypeScript", [".tsx"] = "TypeScript React", [".js"] = "JavaScript",
        [".jsx"] = "JavaScript React", [".py"] = "Python", [".rs"] = "Rust", [".go"] = "Go",
        [".java"] = "Java", [".razor"] = "Blazor", [".css"] = "Stylesheet", [".html"] = "HTML page",
        [".sql"] = "SQL migration", [".sh"] = "Shell script", [".json"] = "JSON data",
        [".yml"] = "YAML config", [".yaml"] = "YAML config", [".md"] = "Markdown doc",
    };

    private static string DescribeStack(string dir)
    {
        var stacks = StacksOf(dir);
        return stacks.Count > 0 ? string.Join(" + ", stacks) : "general-purpose source";
    }

    /// <summary>Own manifests first; a manifest-less root aggregates its immediate subdirectories.</summary>
    private static string DescribeStackDeep(string dir)
    {
        var own = StacksOf(dir);
        if (own.Count > 0) return string.Join(" + ", own);
        var byStack = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var sub in ListDirs(dir))
            foreach (var stack in StacksOf(sub))
            {
                if (!byStack.TryGetValue(stack, out var names)) byStack[stack] = names = [];
                names.Add(Path.GetFileName(sub) + "/");
            }
        if (byStack.Count == 0) return "general-purpose source";
        return string.Join("; ", byStack.Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value)})"));
    }

    private static List<string> StacksOf(string dir)
    {
        var stacks = new List<string>();
        try
        {
            var names = Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f).ToLowerInvariant()).ToHashSet();
            if (names.Contains("package.json")) stacks.Add("Node.js");
            if (names.Any(n => n.EndsWith(".csproj") || n.EndsWith(".slnx") || n.EndsWith(".sln"))) stacks.Add(".NET");
            if (names.Contains("cargo.toml")) stacks.Add("Rust");
            if (names.Contains("go.mod")) stacks.Add("Go");
            if (names.Contains("pyproject.toml") || names.Contains("requirements.txt") || names.Contains("setup.py")) stacks.Add("Python");
            if (names.Contains("pom.xml") || names.Contains("build.gradle")) stacks.Add("JVM");
        }
        catch { }
        return stacks;
    }

    private static string? ReadmeLead(string dir)
    {
        foreach (var candidate in new[] { "README.md", "readme.md", "Readme.md" })
        {
            var path = Path.Combine(dir, candidate);
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadLines(path).Take(30))
                {
                    var trimmed = line.Trim().TrimStart('#').Trim();
                    if (trimmed.Length < 20 || trimmed.StartsWith('<') || trimmed.StartsWith('[') || trimmed.StartsWith("![")) continue;
                    return Truncate(trimmed, 280);
                }
            }
            catch { }
        }
        return null;
    }

    private static List<(string Label, string Command)> DetectCommands(string dir)
    {
        var commands = new List<(string, string)>();
        var packageJson = Path.Combine(dir, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                if (doc.RootElement.TryGetProperty("scripts", out var scripts))
                    foreach (var label in new[] { "test", "lint", "build", "typecheck" })
                        if (scripts.TryGetProperty(label, out _))
                            commands.Add(($"{label} script", $"npm run {label}"));
            }
            catch { }
        }
        try
        {
            var sln = Directory.EnumerateFiles(dir, "*.slnx").Select(Path.GetFileName)
                .Concat(Directory.EnumerateFiles(dir, "*.sln").Select(Path.GetFileName))
                .FirstOrDefault();
            if (sln is not null) commands.Add(("build solution", $"dotnet build {sln}"));
            else if (Directory.EnumerateFiles(dir, "*.csproj").Any()) commands.Add(("build project", "dotnet build"));
            if (File.Exists(Path.Combine(dir, "Cargo.toml"))) commands.Add(("run tests", "cargo test"));
            if (File.Exists(Path.Combine(dir, "go.mod"))) commands.Add(("run tests", "go test ./..."));
            if (File.Exists(Path.Combine(dir, "pyproject.toml")) || File.Exists(Path.Combine(dir, "requirements.txt")))
                commands.Add(("run tests", "pytest"));
        }
        catch { }
        return commands;
    }

    private static IEnumerable<string> DetectDependencies(string dir)
    {
        var packageJson = Path.Combine(dir, "package.json");
        if (File.Exists(packageJson))
        {
            List<string> names = [];
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                foreach (var section in new[] { "dependencies", "devDependencies" })
                    if (doc.RootElement.TryGetProperty(section, out var deps))
                        names.AddRange(deps.EnumerateObject().Select(p => p.Name));
            }
            catch { }
            foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                yield return name;
            yield break;
        }
        foreach (var csproj in SafeEnumerate(dir, "*.csproj"))
        {
            string text;
            try { text = File.ReadAllText(csproj); } catch { continue; }
            foreach (Match match in PackageReferenceRegex().Matches(text))
                yield return match.Groups[1].Value;
        }
    }

    // ---- staleness check ----

    private static readonly char[] ReferencePoison = [' ', '\t', '$', '*', '|', '<', '>', '"', '\'', ';', '&', '(', ')', '{', '}', '\\'];

    internal static IEnumerable<string> ExtractPathReferences(string line)
    {
        foreach (Match match in InlineCodeRegex().Matches(line))
        {
            var candidate = match.Groups[1].Value.Trim().Trim('\'', '"', '.', ',', ';', ':');
            if (IsPathLike(candidate)) yield return StripSuffix(candidate);
        }
        foreach (Match match in MarkdownLinkRegex().Matches(line))
        {
            var candidate = match.Groups[1].Value.Trim();
            if (IsPathLike(candidate)) yield return StripSuffix(candidate);
        }
    }

    private static bool IsPathLike(string candidate)
    {
        if (candidate.Length is 0 or > 180) return false;
        if (candidate.StartsWith('@') || candidate.StartsWith('#') || candidate.StartsWith('-')) return false;
        if (candidate.Contains("://", StringComparison.Ordinal)) return false;
        if (candidate.IndexOfAny(ReferencePoison) >= 0) return false;
        // Bare filenames (package.json) are usually generic mentions; slashed paths are explicit references.
        return candidate.Contains('/');
    }

    private static string StripSuffix(string candidate)
    {
        var cut = candidate.IndexOfAny(['?', '#']);
        return cut >= 0 ? candidate[..cut] : candidate;
    }

    private static bool Resolves(string docDir, string reference)
    {
        var trimmed = reference.Trim();
        if (trimmed.StartsWith("~/")) return true; // home-relative: environment-specific, not checkable here
        if (Path.IsPathRooted(trimmed)) return true; // absolute: environment-specific
        var clean = trimmed.StartsWith("./", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
        var full = Path.GetFullPath(Path.Combine(docDir, clean));
        return File.Exists(full) || Directory.Exists(full);
    }

    // ---- merge + format helpers ----

    internal static string MergeManual(string existingPath, string fresh)
    {
        string existing;
        try { existing = File.ReadAllText(existingPath); }
        catch { return fresh; }
        var marker = existing.IndexOf(ManualMarker, StringComparison.Ordinal);
        if (marker < 0) return fresh;
        var manualTail = existing[marker..].TrimEnd() + "\n";
        var cut = fresh.IndexOf(ManualMarker, StringComparison.Ordinal);
        var head = cut >= 0 ? fresh[..cut] : fresh;
        head = BumpUpdated(existing, head);
        return head + manualTail;
    }

    private static string BumpUpdated(string existing, string head)
    {
        var generated = GeneratedStampRegex().Match(existing);
        if (!generated.Success) return head;
        var stamped = GeneratedStampRegex().Replace(head, $"<!-- Generated: {generated.Groups[1].Value} | Updated: {Stamp()} by blazorly init -->");
        return stamped;
    }

    private static bool FilesEqual(string path, string content)
    {
        try { return File.ReadAllText(path) == content; }
        catch { return false; }
    }

    private static string EnforceBudget(string content)
    {
        if (content.Length <= RootBudgetChars) return content;
        // Drop subdirectory rows (least critical in the index) until the root doc fits.
        var lines = content.Split('\n').ToList();
        var start = lines.FindIndex(l => l.StartsWith("## Subdirectories", StringComparison.Ordinal));
        var end = start >= 0 ? lines.FindIndex(start, l => l.StartsWith("## ", StringComparison.Ordinal) && !l.StartsWith("## Subdirectories")) : -1;
        if (start < 0) return content[..RootBudgetChars] + "\n";
        if (end < 0) end = lines.FindIndex(start, l => l.StartsWith(ManualMarker, StringComparison.Ordinal));
        if (end < 0) end = lines.Count;
        var dropped = 0;
        while (string.Join('\n', lines).Length > RootBudgetChars && end - start > 5)
        {
            lines.RemoveAt(end - 2); // last table row before the blank line
            end--;
            dropped++;
        }
        if (dropped > 0) lines.Insert(end - 1, $"| … | +{dropped} more subdirectories omitted for brevity |");
        var joined = string.Join('\n', lines);
        return joined.Length <= RootBudgetChars ? joined : joined[..RootBudgetChars] + "\n";
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
        catch { return []; }
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyy-MM-dd");
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd() + "…";
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / (1024 * 1024)} MB",
    };

    [GeneratedRegex("`([^`\\n]{1,120})`")]
    private static partial Regex InlineCodeRegex();
    [GeneratedRegex("\\[[^\\]]*\\]\\(([^)\\s]+)\\)")]
    private static partial Regex MarkdownLinkRegex();
    [GeneratedRegex("PackageReference\\s+Include\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex PackageReferenceRegex();
    [GeneratedRegex("<!-- Generated: ([^>|]+?)(?: \\| Updated: [^>]+?)? by blazorly init -->")]
    private static partial Regex GeneratedStampRegex();
}
