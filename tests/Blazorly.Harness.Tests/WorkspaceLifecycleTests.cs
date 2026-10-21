using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

[Collection("BlazorlyHome")]
public sealed class WorkspaceLifecycleTests : BootstrapperTestBase
{
    private async Task<HarnessBootstrapper> Boot(string persistence = "sqlite")
    {
        File.WriteAllText(Path.Combine(Home, "settings.json"), JsonSerializer.Serialize(new { persistence, enableSystemOne = false }));
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        return boot;
    }

    private Workspace Add(HarnessBootstrapper boot, string name)
    {
        var directory = Path.Combine(Home, name);
        Directory.CreateDirectory(directory);
        return boot.Workspaces.Add(name, directory);
    }

    [Fact]
    public async Task StartupAndSettingsChanges_DoNotRegisterTheHarnessDirectory()
    {
        await using var boot = await Boot();
        Assert.Empty(boot.Workspaces.List());
        boot.ApplyDefaultSelection();
        Assert.Empty(boot.Workspaces.List());
        var facade = new SessionFacade(boot, new UiEventBroker());
        Assert.Throws<InvalidOperationException>(() => facade.CreateSession());
        Assert.Empty(new WorkspaceRegistry(Home).List());
    }

    [Fact]
    public async Task LegacyDefault_IsRemovedWithoutDeletingFilesOrExplicitWorkspaces()
    {
        var project = Path.Combine(Home, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(Home, "workspaces.json"), JsonSerializer.Serialize(new
        {
            workspaces = new[] { new { id = "ws-default", name = "default", root = AppContext.BaseDirectory, order = 0 },
                new { id = "ws-user", name = "My project", root = project, order = 1 } },
            defaultWorkspaceId = "ws-default",
        }));
        await using var boot = await Boot();
        Assert.Equal("ws-user", Assert.Single(boot.Workspaces.List()).Id);
        Assert.Equal("ws-user", boot.Workspaces.Default().Id);
        Assert.Null(new WorkspaceRegistry(Home).Get("ws-default"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(project, "keep.txt")));
        Assert.True(File.Exists(typeof(HarnessBootstrapper).Assembly.Location));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("jsonl")]
    public async Task Deletion_RemovesDescendantsAttachmentsFilesAndOwnedProcesses_KeepingOtherWorkspaces(string persistence)
    {
        await using var boot = await Boot(persistence);
        using var runs = new RunSupervisor();
        var workspace = Add(boot, "project");
        var other = Add(boot, "other");
        var file = Path.Combine(workspace.Root, "read-only.txt");
        File.WriteAllText(file, "delete");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(other.Root, "keep.txt"), "keep");
        var parent = boot.Loop.Create(new SessionMeta(Cwd: workspace.Root));
        var child = boot.Sessions.Create(meta: new SessionMeta(workspace.Root, parent.Id, 1));
        var grandchild = boot.Sessions.Create(meta: new SessionMeta(other.Root, child.Id, 2));
        var kept = boot.Sessions.Create(meta: new SessionMeta(other.Root));
        boot.Workspaces.Archive(child.Id, true);
        await boot.Sessions.Persistence!.CreateAsync(new SessionHeader
        {
            Id = "cold-descendant", Cwd = workspace.Root, ParentSession = grandchild.Id,
            DelegationDepth = 3, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        var deletedAttachment = await boot.Attachments.SaveAsync(grandchild.Id, [1, 2, 3], "application/octet-stream");
        var keptAttachment = await boot.Attachments.SaveAsync(kept.Id, [4, 5, 6], "application/octet-stream");
        var ownedRun = runs.Start(SleepCommand("owned", workspace.Root));
        var keptRun = runs.Start(SleepCommand("kept", other.Root));
        var job = boot.Jobs.StartProcess("test", "owned background process", SleepStartInfo(workspace.Root), parent);
        var deletion = new WorkspaceDeletionService(boot, runs);
        var preview = await deletion.PreviewAsync(workspace.Id);
        Assert.Equal(4, preview.Sessions.Count);
        Assert.Contains(preview.Sessions, h => h.Id == "cold-descendant");
        Assert.DoesNotContain(preview.Sessions, h => h.Id == kept.Id);

        await deletion.DeleteAsync(workspace.Id, workspace.Root);

        Assert.False(Directory.Exists(workspace.Root));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(other.Root, "keep.txt")));
        Assert.Equal(kept.Id, Assert.Single(await boot.Sessions.ListPersistedAsync()).Id);
        Assert.Equal(kept.Id, Assert.Single(boot.Sessions.LiveSessions()).Id);
        Assert.Null(boot.Agents.Get(parent.Id));
        Assert.Null(await boot.Attachments.ReadAsync(deletedAttachment));
        Assert.Equal(new byte[] { 4, 5, 6 }, (await boot.Attachments.ReadAsync(keptAttachment))!.Data);
        Assert.False(boot.Workspaces.IsArchived(child.Id));
        Assert.Null(runs.Get(ownedRun.Id));
        Assert.True(runs.Get(keptRun.Id)!.Running);
        Assert.Equal("done", boot.Jobs.Get(job)!.Status);
        Assert.Equal(other.Id, Assert.Single(new WorkspaceRegistry(Home).List()).Id);
    }

    [Fact]
    public async Task Deletion_UnlinksChildJunctionsWithoutDeletingTheirTargets()
    {
        await using var boot = await Boot();
        using var runs = new RunSupervisor();
        var workspace = Add(boot, "project");
        var outside = Path.Combine(Home, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        var link = Path.Combine(workspace.Root, "linked");
        CreateDirectoryLink(link, outside);
        await new WorkspaceDeletionService(boot, runs).DeleteAsync(workspace.Id, workspace.Root);
        Assert.False(Directory.Exists(workspace.Root));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_StopsWindowsDescendantsAfterTheirIntermediateShellHasExited(bool backgroundJob)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var boot = await Boot();
        using var runs = new RunSupervisor();
        var workspace = Add(boot, "process-tree");
        var owner = boot.Loop.Create(new SessionMeta(Cwd: workspace.Root));
        var middleScript = SpawnPowerShell("Start-Sleep -Seconds 30", "leaf.pid");
        var rootScript = SpawnPowerShell(middleScript, "middle.pid") + "\nStart-Sleep -Seconds 30";
        var arguments = new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(rootScript)) };
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = workspace.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (backgroundJob) boot.Jobs.StartProcess("test", "descendants", start, owner);
        else runs.Start(new ProjectCommand("tree", "tree", "tree", "tests", "process tree", workspace.Root, start.FileName, arguments));
        var leafId = 0;
        try
        {
            var deadline = Environment.TickCount64 + 15_000;
            var middleId = 0;
            while (Environment.TickCount64 < deadline)
            {
                leafId = ReadPid(Path.Combine(workspace.Root, "leaf.pid"));
                middleId = ReadPid(Path.Combine(workspace.Root, "middle.pid"));
                if (leafId > 0 && middleId > 0 && !ProcessAlive(middleId)) break;
                await Task.Delay(50);
            }
            Assert.True(leafId > 0 && ProcessAlive(leafId), "The leaf process should still be running.");
            Assert.True(middleId > 0 && !ProcessAlive(middleId), "The intermediate shell should have exited.");
            await new WorkspaceDeletionService(boot, runs).DeleteAsync(workspace.Id, workspace.Root);
            Assert.False(ProcessAlive(leafId));
            Assert.False(Directory.Exists(workspace.Root));
        }
        finally
        {
            if (leafId > 0 && ProcessAlive(leafId))
                using (var remaining = Process.GetProcessById(leafId)) remaining.Kill(entireProcessTree: true);
        }
    }

    private static string SpawnPowerShell(string script, string pidFile) => $$"""
        $childStart = [System.Diagnostics.ProcessStartInfo]::new('powershell.exe')
        $childStart.Arguments = '-NoProfile -NonInteractive -EncodedCommand {{Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}}'
        $childStart.UseShellExecute = $false
        $childStart.CreateNoWindow = $true
        $childStart.WorkingDirectory = [Environment]::CurrentDirectory
        $childProcess = [System.Diagnostics.Process]::Start($childStart)
        [System.IO.File]::WriteAllText('{{pidFile}}', $childProcess.Id.ToString())
        """;

    private static int ReadPid(string path)
    {
        try { return int.TryParse(File.ReadAllText(path), out var id) ? id : 0; }
        catch (IOException) { return 0; }
    }

    private static bool ProcessAlive(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task Deletion_RejectsProtectedAndLinkedRoots_AndNestedRegisteredWorkspaces()
    {
        await using var boot = await Boot();
        using var runs = new RunSupervisor();
        var deletion = new WorkspaceDeletionService(boot, runs);
        foreach (var root in new[] { Path.GetPathRoot(Home)!, Home, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
        {
            var protectedWorkspace = boot.Workspaces.Add("Protected", root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.DeleteAsync(protectedWorkspace.Id, root));
            Assert.True(Directory.Exists(root));
            boot.Workspaces.Remove(protectedWorkspace.Id);
        }
        var parent = Add(boot, "parent");
        var nestedPath = Path.Combine(parent.Root, "nested");
        Directory.CreateDirectory(nestedPath);
        var nested = boot.Workspaces.Add("Nested", nestedPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.DeleteAsync(parent.Id, parent.Root));
        Assert.True(Directory.Exists(nested.Root));
        var link = Path.Combine(Home, "root-link");
        CreateDirectoryLink(link, parent.Root);
        try
        {
            var linked = boot.Workspaces.Add("Linked root", link);
            await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.DeleteAsync(linked.Id, link));
            Assert.True(Directory.Exists(parent.Root));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Deletion_RequiresTheConfirmedRoot_AndCanCleanUpAMissingFolder()
    {
        await using var boot = await Boot();
        using var runs = new RunSupervisor();
        var workspace = Add(boot, "project");
        var session = boot.Sessions.Create(meta: new SessionMeta(workspace.Root));
        var deletion = new WorkspaceDeletionService(boot, runs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.DeleteAsync(workspace.Id, Path.Combine(Home, "different")));
        Assert.True(Directory.Exists(workspace.Root));
        Assert.NotNull(boot.Sessions.Get(session.Id));
        Directory.Delete(workspace.Root);
        await deletion.DeleteAsync(workspace.Id, workspace.Root);
        Assert.Empty(boot.Workspaces.List());
        Assert.Empty(await boot.Sessions.ListPersistedAsync());
    }

    [Fact]
    public async Task FailedFolderDeletion_KeepsTheWorkspaceAndSessionsForRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var boot = await Boot();
        using var runs = new RunSupervisor();
        var workspace = Add(boot, "project");
        var session = boot.Sessions.Create(meta: new SessionMeta(workspace.Root));
        var file = Path.Combine(workspace.Root, "locked.txt");
        File.WriteAllText(file, "locked");
        var deletion = new WorkspaceDeletionService(boot, runs);
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => deletion.DeleteAsync(workspace.Id, workspace.Root));
        Assert.NotNull(boot.Workspaces.Get(workspace.Id));
        Assert.NotNull(boot.Sessions.Get(session.Id));
        await deletion.DeleteAsync(workspace.Id, workspace.Root);
        Assert.False(Directory.Exists(workspace.Root));
        Assert.Empty(await boot.Sessions.ListPersistedAsync());
    }

    private static ProjectCommand SleepCommand(string id, string cwd)
        => new(id, id, id, "tests", "sleep", cwd,
            OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"] : ["-c", "sleep 30"]);

    private static ProcessStartInfo SleepStartInfo(string cwd)
    {
        var command = SleepCommand("background", cwd);
        var info = new ProcessStartInfo(command.Executable!)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in command.Arguments!) info.ArgumentList.Add(argument);
        return info;
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
