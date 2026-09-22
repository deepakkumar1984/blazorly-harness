using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

public class WorkspaceUxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-ux-" + Guid.NewGuid().ToString("N")[..8]);

    public WorkspaceUxTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void PackageScripts_PreferTheLockfileManager_AndRankDevFirst()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), """
            { "scripts": { "build": "tsc", "dev": "vite", "lint": "eslint ." } }
            """);
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "lockfileVersion: 9\n");

        var commands = ProjectCommandDiscovery.Discover(_root);

        Assert.Equal("pnpm run dev", commands[0].CommandLine);
        Assert.Contains(commands, c => c.CommandLine == "pnpm run build");
        Assert.Equal(commands[0].Id, ProjectCommandDiscovery.Find(_root, commands[0].Id)!.Id);
    }

    [Fact]
    public void Dotnet_DoesNotOfferRunForATestProject()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        File.WriteAllText(Path.Combine(_root, "App.Tests.csproj"), "<Project />");

        var commands = ProjectCommandDiscovery.Discover(_root);

        Assert.Contains(commands, c => c.CommandLine == "dotnet run --project App.csproj");
        Assert.Contains(commands, c => c.CommandLine == "dotnet test App.Tests.csproj");
        Assert.Contains(commands, c => c.CommandLine == "dotnet build App.csproj");
        Assert.DoesNotContain(commands, c => c.CommandLine.Contains("run --project App.Tests", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedManifest_IsGroupedByFolder_AndJunkDirsAreSkipped()
    {
        var web = Path.Combine(_root, "web");
        var modules = Path.Combine(_root, "node_modules", "left-pad");
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(modules);
        File.WriteAllText(Path.Combine(web, "package.json"), """{ "scripts": { "start": "node server.js" } }""");
        File.WriteAllText(Path.Combine(modules, "package.json"), """{ "scripts": { "test": "echo no" } }""");

        var commands = ProjectCommandDiscovery.Discover(_root);

        Assert.Contains(commands, c => c.Group == "web" && c.CommandLine == "npm run start");
        Assert.DoesNotContain(commands, c => c.CommandLine.Contains("left-pad", StringComparison.Ordinal) || c.Detail.Contains("echo no", StringComparison.Ordinal));
    }

    [Fact]
    public void Editor_RejectsPathsThatEscapeTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_root, "inside.txt"), "ok");
        var outside = Path.Combine(Path.GetTempPath(), "blazorly-ux-outside-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Read(_root, "../" + Path.GetFileName(outside)));
            Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Write(_root, "../escaped.txt", "nope"));
            Assert.Equal("ok", WorkspaceFiles.Read(_root, "inside.txt").Text.Trim());
        }
        finally
        {
            try { File.Delete(outside); } catch (IOException) { }
        }
    }

    [Fact]
    public void Editor_RoundTripsAFile_AndRefusesBinariesAndTheRoot()
    {
        WorkspaceFiles.Write(_root, "src/generated.cs", "class Generated {}\n");
        var doc = WorkspaceFiles.Read(_root, "src/generated.cs");
        Assert.Equal("class Generated {}\n", doc.Text);
        Assert.Contains(WorkspaceFiles.List(_root), n => n.Name == "src" && n.IsDirectory);

        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [0x00, 0x01, 0x02]);
        Assert.True(WorkspaceFiles.Read(_root, "blob.bin").Binary);
        Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Write(_root, "", "no"));
    }

    [Fact]
    public async Task RunSupervisor_StartsAndStopsATrackedProcess()
    {
        using var runs = new RunSupervisor();
        var echo = new ProjectCommand("echo", "echo", "echo hello-run", "test", "echo hello-run", _root);
        var started = runs.Start(echo);
        var done = await WaitUntilAsync(() =>
        {
            var snap = runs.Get(started.Id);
            return snap is { Running: false } && snap.Output.Contains("hello-run", StringComparison.Ordinal);
        });
        Assert.True(done, runs.Get(started.Id)?.Output);

        var sleep = OperatingSystem.IsWindows()
            ? new ProjectCommand("sleep", "sleep", "sleep", "test", "powershell -NoProfile -Command Start-Sleep -Seconds 30", _root)
            : new ProjectCommand("sleep", "sleep", "sleep", "test", "sleep 30", _root);
        var hanging = runs.Start(sleep);
        Assert.True(runs.Get(hanging.Id)!.Running);
        runs.Stop(hanging.Id);
        Assert.True(await WaitUntilAsync(() => runs.Get(hanging.Id) is { Running: false }));
        Assert.Contains(runs.ForRoot(_root), r => r.Id == hanging.Id);
    }

    [Fact]
    public void Solution_OffersExecutableProjectsUnderSrc_NotASolutionRunOrLibraryRun()
    {
        File.WriteAllText(Path.Combine(_root, "Product.slnx"), "<Solution />");
        var web = Path.Combine(_root, "src", "My Web App");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "My Web App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        File.WriteAllText(Path.Combine(web, "Library.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(web, "Checks.csproj"), "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");

        var commands = ProjectCommandDiscovery.Discover(_root);

        Assert.Contains(commands, c => c.CommandLine == "dotnet build Product.slnx");
        var run = Assert.Single(commands, c => c.Arguments?.FirstOrDefault() == "run");
        Assert.Equal("dotnet", run.Executable);
        Assert.Equal(["run", "--project", "My Web App.csproj"], run.Arguments!);
        Assert.Equal(web, run.WorkingDirectory);
        Assert.Contains(commands, c => c.CommandLine == "dotnet test Checks.csproj");
        Assert.DoesNotContain(commands, c => c.CommandLine == "dotnet run");
    }

    [Fact]
    public void PackageScripts_AreNotTruncated_AndInheritTheWorkspacePackageManager()
    {
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "lockfileVersion: 9\n");
        var app = Path.Combine(_root, "packages", "web");
        Directory.CreateDirectory(app);
        var scripts = Enumerable.Range(0, 25).ToDictionary(i => $"command-{i}", _ => "echo ok");
        File.WriteAllText(Path.Combine(app, "package.json"), System.Text.Json.JsonSerializer.Serialize(new { scripts }));

        var commands = ProjectCommandDiscovery.Discover(_root);

        Assert.Equal(25, commands.Count);
        Assert.All(commands, c => Assert.StartsWith("pnpm run ", c.CommandLine));
        Assert.All(commands, c => Assert.Equal("packages/web", c.Group));
    }

    [Fact]
    public void PlainNodeAndComposerArrayScripts_AreDiscovered_DespiteMalformedOtherManifests()
    {
        File.WriteAllText(Path.Combine(_root, "server.js"), "console.log('hello');");
        File.WriteAllText(Path.Combine(_root, "package.json"), "[]");
        File.WriteAllText(Path.Combine(_root, "composer.json"), """{"scripts":{"check":["php -l index.php","echo checked"]}}""");
        var commands = ProjectCommandDiscovery.Discover(_root);
        Assert.Contains(commands, c => c.CommandLine == "node server.js");
        Assert.Contains(commands, c => c.CommandLine == "composer run-script check");
    }

    [Fact]
    public void Editor_RejectsAStaleSaveWithoutOverwritingTheAgentsChanges()
    {
        File.WriteAllText(Path.Combine(_root, "shared.txt"), "original");
        var open = WorkspaceFiles.Read(_root, "shared.txt");
        File.WriteAllText(Path.Combine(_root, "shared.txt"), "changed by agent");

        Assert.Throws<WorkspaceConflictException>(() => WorkspaceFiles.Write(_root, "shared.txt", "my edits", open.Version));
        Assert.Equal("changed by agent", File.ReadAllText(Path.Combine(_root, "shared.txt")));
        var current = WorkspaceFiles.Read(_root, "shared.txt");
        var saved = WorkspaceFiles.Write(_root, "shared.txt", "reviewed edits", current.Version);
        Assert.Equal("reviewed edits", WorkspaceFiles.Read(_root, "shared.txt").Text);
        Assert.NotEqual(current.Version, saved.Version);
    }

    [Fact]
    public void Editor_PreservesUtf8Bom_AndRejectsInvalidUtf8()
    {
        File.WriteAllBytes(Path.Combine(_root, "bom.txt"), [0xef, 0xbb, 0xbf, .. System.Text.Encoding.UTF8.GetBytes("original\r\n")]);
        var open = WorkspaceFiles.Read(_root, "bom.txt");
        Assert.Equal("original\r\n", open.Text);
        WorkspaceFiles.Write(_root, "bom.txt", "updated\r\n", open.Version);
        Assert.Equal([0xef, 0xbb, 0xbf], File.ReadAllBytes(Path.Combine(_root, "bom.txt")).Take(3).ToArray());
        File.WriteAllBytes(Path.Combine(_root, "bad.txt"), [0xff, 0xfe, 0x41]);
        Assert.True(WorkspaceFiles.Read(_root, "bad.txt").Binary);
    }

    [Fact]
    public void Editor_RejectsLinkedDirectories_AndCommandDiscoveryDoesNotFollowThem()
    {
        var outside = _root + "-outside";
        var link = Path.Combine(_root, "linked");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "outside");
        File.WriteAllText(Path.Combine(outside, "package.json"), """{"scripts":{"outside":"echo outside"}}""");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var create = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/d /c mklink /J \"{link}\" \"{outside}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                })!;
                create.WaitForExit();
                Assert.Equal(0, create.ExitCode);
            }
            else Directory.CreateSymbolicLink(link, outside);

            Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Read(_root, "linked/private.txt"));
            Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Write(_root, "linked/private.txt", "changed"));
            Assert.DoesNotContain(WorkspaceFiles.List(_root), n => n.Name == "linked");
            Assert.Empty(ProjectCommandDiscovery.Discover(_root));
            Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "private.txt")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void WorkspacePaths_PreserveDriveRoots_AndNormalizeRegistryMembership()
    {
        var driveRoot = Path.GetPathRoot(_root)!;
        Assert.Equal(driveRoot, WorkspaceFiles.NormalizeRoot(driveRoot));
        Assert.True(WorkspaceFiles.IsInside(driveRoot, _root));
        var registry = new WorkspaceRegistry(Path.Combine(_root, "state"));
        var workspace = registry.Add("Example", _root + Path.DirectorySeparatorChar);
        var lookup = OperatingSystem.IsWindows() ? _root.ToUpperInvariant() : _root;
        Assert.Equal(workspace.Id, registry.ForRoot(lookup)!.Id);
        Assert.Equal(workspace.Id, registry.Ensure(lookup).Id);
        Assert.Throws<InvalidOperationException>(() => registry.Add("Duplicate", lookup));
    }

    [Fact]
    public void RemovingTheLastWorkspace_SurvivesReload_WithoutDeletingItsFolder()
    {
        var state = Path.Combine(_root, "state");
        var registry = new WorkspaceRegistry(state);
        registry.Add("Example", _root);
        registry.Remove(registry.Default().Id);
        var reloaded = new WorkspaceRegistry(state);
        Assert.Empty(reloaded.List());
        Assert.Throws<InvalidOperationException>(() => reloaded.Default());
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task RunSupervisor_StreamsPartialLines_AndPreventsDuplicateActiveRuns()
    {
        using var runs = new RunSupervisor();
        var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('ready-without-newline'); Start-Sleep -Seconds 30" }
            : new[] { "-c", "printf ready-without-newline; sleep 30" };
        var command = new ProjectCommand("partial", "partial", "partial", "test", "partial", _root, executable, arguments);
        var started = runs.Start(command);
        Assert.Equal(started.Id, runs.Start(command).Id);
        Assert.True(await WaitUntilAsync(() => runs.Get(started.Id) is { Running: true } live && live.Output.Contains("ready-without-newline", StringComparison.Ordinal)));
        runs.Stop(started.Id);
        Assert.True(await WaitUntilAsync(() => runs.Get(started.Id) is { Running: false }));
        Assert.True(runs.Get(started.Id)!.StopRequested);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> ready)
    {
        var deadline = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < deadline)
        {
            if (ready()) return true;
            await Task.Delay(50);
        }
        return ready();
    }
}
