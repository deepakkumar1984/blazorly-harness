using Blazorly.Harness.Core.Instructions;
using Xunit;

namespace Blazorly.Harness.Tests;

public class DocsInitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-docsinit-" + Guid.NewGuid().ToString("N")[..8]);

    public DocsInitTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Demo\n\nA demo workspace for doc generation tests.\n");
        File.WriteAllText(Path.Combine(_root, "package.json"),
            """{"name":"demo","scripts":{"test":"vitest run","build":"vite build"},"dependencies":{"react":"18.0.0"}}""");
        Directory.CreateDirectory(Path.Combine(_root, "backend"));
        File.WriteAllText(Path.Combine(_root, "backend", "server.ts"), "export {};\n");
        File.WriteAllText(Path.Combine(_root, "backend", "CLAUDE.md"),
            "# Backend\n\nEntry point is `src/middleware/tenant.ts` and config lives in `backend/config.ts`.\n");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "junk"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "junk", "x.js"), "junk\n");
        File.WriteAllText(Path.Combine(_root, ".DS_Store"), "junk\n");
        File.WriteAllText(Path.Combine(_root, "debug.log"), "junk\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Preview_PlansRootAndSubdirDocs_SkipsExcludedDirs()
    {
        var preview = DocsInitService.Preview(_root, depth: 1);

        var paths = preview.Files.Select(f => f.RelativePath).ToList();
        Assert.Contains("AGENTS.md", paths);
        Assert.Contains(Path.Combine("backend", "AGENTS.md"), paths);
        Assert.DoesNotContain(paths, p => p.Contains("node_modules"));
    }

    [Fact]
    public void Preview_DefaultsToRootFileOnly()
    {
        var preview = DocsInitService.Preview(_root);

        Assert.Equal("AGENTS.md", Assert.Single(preview.Files).RelativePath);
    }

    [Fact]
    public void Preview_RootDocMentionsOnlyRealFiles()
    {
        var preview = DocsInitService.Preview(_root, depth: 1);

        var root = preview.Files.Single(f => f.RelativePath == "AGENTS.md");
        Assert.Contains("package.json", root.Content);
        Assert.Contains("backend/", root.Content);
        Assert.Contains("npm run test", root.Content);
        Assert.Contains("<!-- MANUAL:", root.Content);
        Assert.True(root.Content.Length <= DocsInitService.RootBudgetChars);
    }

    [Fact]
    public void Preview_ExcludesJunkFilesAndCounts()
    {
        var preview = DocsInitService.Preview(_root, depth: 1);

        var root = preview.Files.Single(f => f.RelativePath == "AGENTS.md");
        Assert.DoesNotContain(".DS_Store", root.Content);
        Assert.DoesNotContain("debug.log", root.Content);
        Assert.DoesNotContain("node_modules", root.Content);
    }

    [Fact]
    public void Brief_AggregatesChildStacksAtManifestLessRoot()
    {
        var bare = Directory.CreateDirectory(Path.Combine(_root, "bare"));
        Directory.CreateDirectory(Path.Combine(bare.FullName, "web"));
        File.WriteAllText(Path.Combine(bare.FullName, "web", "package.json"), """{"name":"web"}""");

        var brief = DocsInitService.BuildBrief(bare.FullName, depth: 0);

        Assert.Contains("Node.js (web/)", brief.Text);
    }

    [Fact]
    public void Brief_CountsExcludeJunkDirs()
    {
        var brief = DocsInitService.BuildBrief(_root, depth: 0);

        Assert.DoesNotContain("node_modules", brief.Text);
        Assert.Contains("(4 files)", brief.Text); // README, package.json, server.ts, CLAUDE.md
    }

    [Fact]
    public void Preview_SubdirDocLinksParent()
    {
        var preview = DocsInitService.Preview(_root, depth: 1);

        var sub = preview.Files.Single(f => f.RelativePath == Path.Combine("backend", "AGENTS.md"));
        Assert.Contains("<!-- Parent: ../AGENTS.md -->", sub.Content);
        Assert.Contains("server.ts", sub.Content);
    }

    [Fact]
    public void Verify_FlagsStaleSlashedReference_IgnoresBareMentions()
    {
        // backend/CLAUDE.md references src/middleware/tenant.ts (missing) and backend/config.ts (missing).
        var stale = DocsInitService.Verify(_root, depth: 1);

        Assert.Contains(stale, s => s.Reference == "src/middleware/tenant.ts" && s.Line == 3);
        Assert.Contains(stale, s => s.Reference == "backend/config.ts");
    }

    [Fact]
    public void Verify_IgnoresBareFilenamesAndUrls()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"),
            "# Root\n\nSee `package.json` and [docs](https://example.com/a/b) plus `npm run test`.\n");

        var stale = DocsInitService.Verify(_root, depth: 0);

        Assert.Empty(stale);
    }

    [Fact]
    public void Apply_WritesDocs_PreservesManualTailOnRegen()
    {
        var first = DocsInitService.Apply(_root, depth: 1);

        Assert.Contains("AGENTS.md", first.Written);
        var path = Path.Combine(_root, "AGENTS.md");
        File.AppendAllText(path, "My hand-written note.\n");

        var second = DocsInitService.Apply(_root, depth: 1);

        // Second run rewrites once to bump the Updated stamp, keeping the manual tail.
        Assert.Contains("My hand-written note.", File.ReadAllText(path));
        Assert.Contains("| Updated:", File.ReadAllText(path));

        var third = DocsInitService.Apply(_root, depth: 1);
        Assert.DoesNotContain("AGENTS.md", third.Written);
        Assert.Contains("AGENTS.md", third.Unchanged);
    }

    [Fact]
    public void GeneratedDocs_AreVerifyClean()
    {
        DocsInitService.Apply(_root, depth: 1);
        File.Delete(Path.Combine(_root, "backend", "CLAUDE.md")); // pre-existing stale fixture

        var stale = DocsInitService.Verify(_root, depth: 1);

        Assert.Empty(stale);
    }
}
