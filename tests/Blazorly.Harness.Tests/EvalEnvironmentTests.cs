using System.Text.Json;
using Blazorly.Harness.Cli;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// An eval score is only comparable when the backend, the pinned configuration, the plugin set and
/// the published tool schemas are recorded. These tests pin the contract: ambient behavior is never
/// inherited, unavailable backends are reported as skips (not passes), environment.json carries a
/// stable tool-schema hash, and a tool failure is recovered inside the same turn.
/// </summary>
[Collection("BlazorlyHome")]
public class EvalEnvironmentTests : BootstrapperTestBase
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "tasks")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private string WriteTasks(params (string Id, string Json)[] tasks)
    {
        var root = Path.Combine(Path.GetTempPath(), "blazorly-evalenv-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (id, json) in tasks)
        {
            var dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "task.json"), json);
        }
        return root;
    }

    private static string OutDir() => Path.Combine(Path.GetTempPath(), "blazorly-evalenv-o-" + Guid.NewGuid().ToString("N")[..8]);

    private const string SmokeTask = """{"description":"smoke","prompt":"run the scripted task","checks":[{"name":"always","run":"true"}]}""";

    [Fact]
    public async Task SeededHome_PinsBehavior_AndInheritsOnlyRoutes()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        // Ambient knobs that must NOT leak into a measured run.
        PatchAmbient("""{"sandboxMode":"danger-full-access","enableTeams":true,"contextWindowTokens":999999,"enableMcp":true}""");

        var tasks = WriteTasks(("smoke", SmokeTask));
        var output = OutDir();
        try
        {
            var summary = await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks,
                OutDir = output,
                Out = new StringWriter(),
                Sandbox = [EvalSandbox.None],
            });
            Assert.Equal(1, summary.Executed);

            using var settings = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(output, "home-none", "settings.json")));
            var root = settings.RootElement;
            // Pinned baseline
            Assert.Equal("danger-full-access", root.GetProperty("sandboxMode").GetString());
            Assert.False(root.GetProperty("enableTeams").GetBoolean());
            Assert.False(root.GetProperty("enableMcp").GetBoolean());
            Assert.Equal(65_536, root.GetProperty("contextWindowTokens").GetInt32());
            Assert.False(root.GetProperty("telemetryEnabled").GetBoolean());
            // Inherited route
            Assert.Equal("scripted", root.GetProperty("provider").GetString());
            Assert.Equal(server.BaseUrl, root.GetProperty("baseUrl").GetString());
            Assert.Equal("test-key", root.GetProperty("apiKey").GetString());
            Assert.True(root.GetProperty("customProviders").GetArrayLength() > 0);
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task LandlockBackend_PinsWorkspaceWriteAndFailClosed()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks(("smoke", SmokeTask));
        var output = OutDir();
        try
        {
            await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks,
                OutDir = output,
                Out = new StringWriter(),
                Sandbox = [EvalSandbox.Landlock],
            });
            using var settings = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(output, "home-landlock", "settings.json")));
            Assert.Equal("workspace-write", settings.RootElement.GetProperty("sandboxMode").GetString());
            Assert.True(settings.RootElement.GetProperty("sandboxFailClosedWhenUnsupported").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task UnavailableBackend_IsSkipped_NotPassed()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks(("smoke", SmokeTask));
        var output = OutDir();
        try
        {
            var summary = await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks,
                OutDir = output,
                Out = new StringWriter(),
                Sandbox = [EvalSandbox.Landlock, EvalSandbox.E2b, EvalSandbox.None],
            });

            Assert.Equal(3, summary.Total);
            Assert.Equal(1, summary.Executed); // only `none` can run on a host without Landlock or an E2B key
            Assert.Equal(2, summary.Skipped);
            Assert.Equal(0, summary.Failed); // a skip never fails the run, and never passes it either
            Assert.Equal(1, summary.Passed);

            var skipped = summary.Tasks.Where(t => t.IsSkipped).ToList();
            Assert.Contains(skipped, t => t.Sandbox == EvalSandbox.Landlock && t.Skipped!.StartsWith("confinement-unavailable"));
            Assert.Contains(skipped, t => t.Sandbox == EvalSandbox.E2b && t.Skipped!.StartsWith("e2b-not-configured"));
            Assert.All(summary.Tasks.Where(t => !t.IsSkipped), t => Assert.NotEqual("skipped", t.Finish));
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TaskLevelSandbox_RunsOnlyItsOwnBackend()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks(
            ("plain", SmokeTask),
            ("confined", """{"description":"confined","prompt":"run the scripted task","sandbox":"landlock","checks":[{"name":"always","run":"true"}]}"""));
        var output = OutDir();
        try
        {
            var summary = await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks,
                OutDir = output,
                Out = new StringWriter(),
            });

            // Default backend for `plain`, declared backend for `confined` — no cross product.
            Assert.Equal(EvalSandbox.Default, summary.Tasks.Single(t => t.Id == "plain").Sandbox);
            Assert.Equal(EvalSandbox.Landlock, summary.Tasks.Single(t => t.Id == "confined").Sandbox);
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task EnvironmentManifest_RecordsHostPluginsAndStableToolSchemaHash()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks(("smoke", SmokeTask));
        var first = OutDir();
        var second = OutDir();
        try
        {
            await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks, OutDir = first, Out = new StringWriter(), Sandbox = [EvalSandbox.None],
            });
            await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks, OutDir = second, Out = new StringWriter(), Sandbox = [EvalSandbox.None],
            });

            var manifestPath = Path.Combine(first, "environment.json");
            Assert.True(File.Exists(manifestPath));
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var manifest = doc.RootElement;

            Assert.True(manifest.GetProperty("generatedAt").GetString()!.Length > 0);
            Assert.True(manifest.GetProperty("harnessVersion").GetString()!.Length > 0);
            Assert.Contains("landlockSupported", manifest.EnumerateObject().Select(p => p.Name));
            Assert.Contains(manifest.GetProperty("defaultBackend").GetString(), EvalSandbox.All);
            Assert.Equal("scripted", manifest.GetProperty("provider").GetString());

            var backend = manifest.GetProperty("manifests").GetProperty("none");
            var hash = backend.GetProperty("toolSchemaHash").GetString()!;
            Assert.Equal(64, hash.Length); // SHA-256 hex
            var tools = backend.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToList();
            Assert.Contains("edit", tools);
            Assert.Contains("bash", tools);
            Assert.True(backend.GetProperty("appliedPlugins").GetArrayLength() > 0);
            // Credentials are masked, never copied into a report that may be committed.
            Assert.Equal("(set, 8 chars)", backend.GetProperty("settings").GetProperty("apiKey").GetString());

            // Same code, same pins → same hash. This is the attribution guarantee.
            using var secondDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(second, "environment.json")));
            Assert.Equal(hash, secondDoc.RootElement.GetProperty("manifests").GetProperty("none")
                .GetProperty("toolSchemaHash").GetString());

            var summary = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(first, "results.json")));
            Assert.Equal(hash, summary.GetProperty("toolSchemaHashes").GetProperty("none").GetString());
            Assert.True(File.ReadAllText(Path.Combine(first, "summary.md")).Contains("Tool schema hash"));
        }
        finally
        {
            foreach (var dir in new[] { first, second, tasks })
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public async Task ToolSchemaHash_ChangesWhenThePublishedSurfaceChanges()
    {
        var schema = Blazorly.Harness.Llm.ToolSchemaJson.FromJson("demo", "does a thing",
            """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}""");
        var renamed = Blazorly.Harness.Llm.ToolSchemaJson.FromJson("demo", "does a thing",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");
        var redescribed = Blazorly.Harness.Llm.ToolSchemaJson.FromJson("demo", "does a thing differently",
            """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}""");

        var baseline = EvalEnvironmentCapture.ToolSchemaHash([schema]);
        Assert.NotEqual(baseline, EvalEnvironmentCapture.ToolSchemaHash([renamed]));
        Assert.NotEqual(baseline, EvalEnvironmentCapture.ToolSchemaHash([redescribed]));
        // Key order must not matter: the canonical form sorts it.
        var reordered = Blazorly.Harness.Llm.ToolSchemaJson.FromJson("demo", "does a thing",
            """{"properties":{"target":{"type":"string"}},"required":["target"],"type":"object"}""");
        Assert.Equal(baseline, EvalEnvironmentCapture.ToolSchemaHash([reordered]));
    }

    [Fact]
    public async Task RecoverToolFailureTask_ScoresTheDurableRecovery()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);

        // Run the shipped task verbatim so the eval contract and the repo artifact stay in sync.
        var source = Path.Combine(RepoRoot(), "eval", "tasks", "recover-tool-failure");
        var tasks = WriteTasks(("recover-tool-failure", File.ReadAllText(Path.Combine(source, "task.json"))));
        var output = OutDir();
        try
        {
            var summary = await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks,
                OutDir = output,
                Out = new StringWriter(),
                Sandbox = [EvalSandbox.None],
            });

            var result = Assert.Single(summary.Tasks);
            Assert.True(result.Pass, string.Join("; ", result.Checks.Where(c => !c.Pass).Select(c => $"{c.Name}: {c.Output}")) + result.Error);
            Assert.Equal("completed", result.Finish);
            Assert.All(result.Checks, check => Assert.True(check.Pass, $"{check.Name}: {check.Output}"));
            Assert.Contains("recovered", File.ReadAllText(
                Path.Combine(output, "workspaces", EvalSandbox.None, "recover-tool-failure", "recovery.txt")));
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Merges extra keys into the ambient home settings (routes stay as written).</summary>
    private void PatchAmbient(string json)
    {
        var path = Path.Combine(Home, "settings.json");
        using var existing = JsonDocument.Parse(File.ReadAllText(path));
        using var patch = JsonDocument.Parse(json);
        var merged = new Dictionary<string, object?>();
        foreach (var property in existing.RootElement.EnumerateObject())
            merged[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        foreach (var property in patch.RootElement.EnumerateObject())
            merged[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        File.WriteAllText(path, JsonSerializer.Serialize(merged,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
