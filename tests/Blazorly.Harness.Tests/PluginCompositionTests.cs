using Blazorly.Harness.Kernel;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

public class PluginLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-plugins-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void MissingDirectory_YieldsEmptyList()
    {
        Assert.Empty(PluginLoader.LoadFromDirectory(Path.Combine(_root, "absent")));
    }

    [Fact]
    public void NonAssemblyFiles_AreSkipped()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.dll"), "not a .NET assembly");
        Assert.Empty(PluginLoader.LoadFromDirectory(_root));
    }

    [Fact]
    public void ToolsAssembly_DiscoversItsPlugins()
    {
        var toolsDll = typeof(Blazorly.Harness.Tools.WebPlugin).Assembly.Location;
        var found = PluginLoader.LoadFromDirectory(Path.GetDirectoryName(toolsDll)!);
        var toolsFound = found.Where(f => f.AssemblyPath == toolsDll).ToList();
        Assert.True(toolsFound.Count >= 10, $"expected the Tools assembly plugins, got {toolsFound.Count}");
        Assert.Contains(toolsFound, f => f.Plugin.Name == "web");
        Assert.Contains(toolsFound, f => f.Plugin.Name == "jobs");
        // The loader's contract is assembly-agnostic: any project dll in the scanned
        // directory contributes its plugins (Core ships the time context plugin).
        Assert.Contains(found, f => f.Plugin.Name == "time");
    }

    [Fact]
    public void CopiedAssembly_LoadsFromItsOwnDirectory()
    {
        Directory.CreateDirectory(_root);
        var toolsDll = typeof(Blazorly.Harness.Tools.WebPlugin).Assembly.Location;
        File.Copy(toolsDll, Path.Combine(_root, Path.GetFileName(toolsDll)));
        // Host assemblies (Kernel/Core/...) unify with the loaded copies; only the
        // plugin directory is probed for the rest.
        var found = PluginLoader.LoadFromDirectory(_root);
        Assert.Contains(found, f => f.Plugin.Name == "web");
    }
}

public class PluginHostOrderingTests
{
    private sealed record Box(string Value);

    [Fact]
    public async Task InjectOrder_WinsOverListOrder()
    {
        await using var ctx = HarnessContext.CreateRoot();
        var applied = new List<string>();
        await PluginHost.ApplyAllAsync(ctx, [
            new MountPlugin("consumer", ["svc"], _ => Task.CompletedTask),
            new MountPlugin("provider", [], c => { c.Provide("svc", new Box("v")); return Task.CompletedTask; }),
        ], applied);
        Assert.Equal(["provider", "consumer"], applied);
        Assert.Equal("v", ctx.Get<Box>("svc").Value);
    }

    [Fact]
    public async Task DuplicateName_FailsFast()
    {
        await using var ctx = HarnessContext.CreateRoot();
        var ex = await Assert.ThrowsAsync<HarnessException>(() => PluginHost.ApplyAllAsync(ctx, [
            new MountPlugin("same", [], _ => Task.CompletedTask),
            new MountPlugin("same", [], _ => Task.CompletedTask),
        ]));
        Assert.Equal("DUPLICATE_PLUGIN", ex.Code);
    }

    [Fact]
    public async Task UnsatisfiableInject_ReportsDeadlock()
    {
        await using var ctx = HarnessContext.CreateRoot();
        var ex = await Assert.ThrowsAsync<HarnessException>(() => PluginHost.ApplyAllAsync(ctx, [
            new MountPlugin("needy", ["missing-service"], _ => Task.CompletedTask),
        ]));
        Assert.Equal("PLUGIN_DEADLOCK", ex.Code);
        Assert.Contains("missing-service", ex.Message);
    }
}

public class PatchTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "blazorly-patches-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private HarnessSettings Patched(string patchesJson)
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "patches.json"), patchesJson);
        var settings = new HarnessSettings();
        HarnessBootstrapper.ApplyPatches(settings, _home);
        return settings;
    }

    [Fact]
    public void MissingFile_IsNoop()
    {
        Directory.CreateDirectory(_home);
        var settings = new HarnessSettings { ContextWindowTokens = 11 };
        HarnessBootstrapper.ApplyPatches(settings, _home);
        Assert.Equal(11, settings.ContextWindowTokens);
    }

    [Fact]
    public void MalformedFile_IsNoop()
    {
        var settings = Patched("{oops");
        Assert.Equal(new HarnessSettings().ContextWindowTokens, settings.ContextWindowTokens);
    }

    [Fact]
    public void Set_AppliesKnownKeysCaseInsensitively()
    {
        var settings = Patched("""{"set": {"contextWindowTokens": 100000, "ENABLETEAMS": true}}""");
        Assert.Equal(100000, settings.ContextWindowTokens);
        Assert.True(settings.EnableTeams);
        Assert.Equal("deepseek", settings.Provider); // untouched keys survive
    }

    [Fact]
    public void Set_SkipsUnknownKeysAndBadValuesPerKey()
    {
        var settings = Patched("""{"set": {"frobnicate": 1, "contextWindowTokens": "huge", "enableTeams": true}}""");
        Assert.True(settings.EnableTeams);
        Assert.Equal(new HarnessSettings().ContextWindowTokens, settings.ContextWindowTokens);
    }

    [Fact]
    public void Disable_SkipsNamesWithoutFlags()
    {
        // llm/tools/frobnicate have no Enable flags: warned and skipped, nothing changes.
        var settings = Patched("""{"disable": ["llm", "tools", "frobnicate"]}""");
        Assert.True(settings.EnableWeb);
        Assert.True(settings.EnableCodeMode);
        Assert.True(settings.EnableTerminals);
    }

    [Fact]
    public void Disable_TurnsFlagsOff()
    {
        var settings = Patched("""{"disable": ["terminals", "workflows"]}""");
        Assert.False(settings.EnableTerminals);
        Assert.False(settings.EnableWorkflows);
        Assert.True(settings.EnableWeb);
    }
}

[Collection("BlazorlyHome")]
public class BootCompositionTests : BootstrapperTestBase
{
    [Fact]
    public async Task Boot_AppliesSpineBeforeCapabilities_InInjectOrder()
    {
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var applied = boot.AppliedPlugins.ToList();
            foreach (var name in new[] { "llm", "systemPrompt", "tools", "sessions", "agents", "agentLoop", "web", "session-query" })
                Assert.Contains(name, applied);
            Assert.True(applied.IndexOf("systemPrompt") < applied.IndexOf("tools"));
            Assert.True(applied.IndexOf("sessions") < applied.IndexOf("projections"));
            Assert.True(applied.IndexOf("agents") < applied.IndexOf("agentLoop"));
            Assert.True(applied.IndexOf("tools") < applied.IndexOf("web"));
            Assert.True(applied.IndexOf("tools") < applied.IndexOf("session-query"));
            Assert.Equal(applied.Count, applied.Distinct().Count()); // no duplicates
            Assert.NotNull(boot.Loop);
            Assert.NotNull(boot.Sessions);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Boot_HonorsPatchesDisableAndSettingsDisabledPlugins()
    {
        File.WriteAllText(Path.Combine(Home, "settings.json"),
            """{"provider":"deepseek","model":"deepseek-v4-flash","disabledPlugins":["web"]}""");
        File.WriteAllText(Path.Combine(Home, "patches.json"), """{"disable":["terminals"]}""");
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            Assert.DoesNotContain("web", boot.AppliedPlugins);
            Assert.DoesNotContain("terminals", boot.AppliedPlugins);
            Assert.Contains("tools", boot.AppliedPlugins); // spine untouched
            Assert.False(boot.Settings.EnableTerminals);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }
}
