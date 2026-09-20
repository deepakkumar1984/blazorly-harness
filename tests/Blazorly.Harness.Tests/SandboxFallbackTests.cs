using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Landlock is Linux-only, so macOS/Windows hosts cannot confine a spawned process. An unconfigured
/// sandbox must run bash/run_code directly instead of answering every command with
/// SANDBOX_UNAVAILABLE, while filesystem confinement (enforced in-process) and explicit per-session
/// overrides keep their guarantees everywhere.
/// </summary>
public class SandboxFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-fallback-" + Guid.NewGuid().ToString("N")[..8]);

    public SandboxFallbackTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Normalize_MapsTheLegacySpelling_EverywhereItIsRead()
    {
        Assert.Equal(SandboxPolicy.FullAccess, SandboxPolicy.Normalize("danger-full-access"));
        Assert.Equal(SandboxPolicy.FullAccess, SandboxPolicy.DangerFullAccess); // alias, not a second value
        Assert.Equal(SandboxPolicy.ReadOnly, SandboxPolicy.Normalize(SandboxPolicy.ReadOnly));
        Assert.Equal(SandboxPolicy.WorkspaceWrite, SandboxPolicy.Normalize(SandboxPolicy.WorkspaceWrite));
        Assert.Null(SandboxPolicy.Normalize(null));

        // a session log written before the rename still resolves to full access
        var policy = new SandboxPolicy { DefaultMode = SandboxPolicy.WorkspaceWrite };
        Assert.Equal(SandboxPolicy.FullAccess, policy.ResolveMode("danger-full-access"));
        Assert.Equal(SandboxPolicy.FullAccess, SandboxPolicy.ResolveProcessMode("danger-full-access", SandboxPolicy.WorkspaceWrite));
    }

    [Fact]
    public void ProcessMode_DeploymentDefault_DegradesOnlyWhereConfinementIsImpossible()
    {
        var resolved = SandboxPolicy.ResolveProcessMode(sessionMode: null, SandboxPolicy.WorkspaceWrite);
        Assert.Equal(SandboxPolicy.ConfinementSupported ? SandboxPolicy.WorkspaceWrite : SandboxPolicy.DangerFullAccess, resolved);
    }

    [Fact]
    public void ProcessMode_SessionOverride_IsNeverWidened()
    {
        Assert.Equal(SandboxPolicy.ReadOnly, SandboxPolicy.ResolveProcessMode(SandboxPolicy.ReadOnly, SandboxPolicy.WorkspaceWrite));
        Assert.Equal(SandboxPolicy.WorkspaceWrite, SandboxPolicy.ResolveProcessMode(SandboxPolicy.WorkspaceWrite, SandboxPolicy.DangerFullAccess));
    }

    [Fact]
    public void ProcessMode_FailClosedSetting_KeepsTheConfiningDefault()
    {
        Assert.Equal(SandboxPolicy.WorkspaceWrite,
            SandboxPolicy.ResolveProcessMode(sessionMode: null, SandboxPolicy.WorkspaceWrite, allowUnconfinedFallback: false));
    }

    [Fact]
    public void FileMode_IsHostIndependent()
    {
        // read-only needs no Landlock: it is enforced in-process, so it must never be widened.
        var policy = new SandboxPolicy { DefaultMode = SandboxPolicy.ReadOnly };
        Assert.Equal(SandboxPolicy.ReadOnly, policy.ResolveMode(sessionMode: null));
        Assert.Equal(SandboxPolicy.WorkspaceWrite, policy.ResolveMode(SandboxPolicy.WorkspaceWrite));
    }

    [Fact]
    public async Task Bash_RunsDirectly_WhenNoSandboxWasConfigured()
    {
        await using var harness = TestHarness.Create(cwd: _root);
        harness.Sandbox.DefaultMode = SandboxPolicy.WorkspaceWrite; // the deployment default
        var agent = harness.CreateAgent(_root);

        var result = await harness.Tools.Execute(Bash(agent, $"echo payload > {_root}/out.txt && cat {_root}/out.txt"));

        Assert.False(result.IsError, result.Error?.Message);
        Assert.Contains("payload", Assert.IsType<TextBlock>(result.Content.Single()).Text);
        Assert.Equal("payload", (await File.ReadAllTextAsync(Path.Combine(_root, "out.txt"))).Trim());
    }

    [Fact]
    public async Task Write_StaysConfinedToTheWorkspace_OnEveryHost()
    {
        await using var harness = TestHarness.Create(cwd: _root);
        harness.Sandbox.DefaultMode = SandboxPolicy.WorkspaceWrite;
        var agent = harness.CreateAgent(_root);
        var outside = Path.Combine(Path.GetTempPath(), $"blazorly-escape-{Guid.NewGuid():N}.txt");

        var denied = await harness.Tools.Execute(Write(agent, outside, "nope"));
        Assert.True(denied.IsError);
        Assert.Contains("mutations are confined to", denied.Error!.Message);
        Assert.False(File.Exists(outside));

        var inside = await harness.Tools.Execute(Write(agent, Path.Combine(_root, "in.txt"), "yes"));
        Assert.False(inside.IsError, inside.Error?.Message);
        Assert.Equal("yes", await File.ReadAllTextAsync(Path.Combine(_root, "in.txt")));
    }

    [Fact]
    public async Task Write_UnderAnExplicitReadOnlyOverride_StillFailsClosed()
    {
        await using var harness = TestHarness.Create(cwd: _root);
        harness.Sandbox.DefaultMode = SandboxPolicy.WorkspaceWrite;
        var agent = harness.CreateAgent(_root);
        agent.Session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload(SandboxPolicy.ReadOnly));

        var result = await harness.Tools.Execute(Write(agent, Path.Combine(_root, "blocked.txt"), "nope"));

        Assert.True(result.IsError);
        Assert.Contains("[sandbox: file access denied under read-only mode]", result.Error!.Message);
        Assert.False(File.Exists(Path.Combine(_root, "blocked.txt")));
    }

    [Fact]
    public async Task Bash_UnderAnExplicitReadOnlyOverride_FailsClosedOnUnconfinableHosts()
    {
        if (SandboxPolicy.ConfinementSupported) return;
        await using var harness = TestHarness.Create(cwd: _root);
        harness.Sandbox.DefaultMode = SandboxPolicy.WorkspaceWrite;
        var agent = harness.CreateAgent(_root);
        agent.Session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload(SandboxPolicy.ReadOnly));

        var result = await harness.Tools.Execute(Bash(agent, "echo nope > blocked.txt"));

        Assert.True(result.IsError);
        Assert.Equal("SANDBOX_UNAVAILABLE", result.Error!.Info?.Code);
        Assert.False(File.Exists(Path.Combine(_root, "blocked.txt")));
    }

    private static ToolExecutionInput Bash(Agent agent, string command) => Call(agent, "bash", new { command, description = "sandbox fallback check" });

    private static ToolExecutionInput Write(Agent agent, string path, string content)
        => Call(agent, "write", new { file_path = path, content });

    private static ToolExecutionInput Call(Agent agent, string name, object arguments) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(arguments),
        CallId = $"call_fb_{Guid.NewGuid().ToString("N")[..6]}",
        Signal = CancellationToken.None,
        Agent = agent,
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
