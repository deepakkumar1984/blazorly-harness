using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

file static class TerminalTestInput
{
    public static ToolExecutionInput For(Agent agent, string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = Ids.NewCallId(),
        Signal = CancellationToken.None,
        Agent = agent,
    };

    internal static JsonElement V(this ToolExecutionResult result) => result.Value ?? default;
}

/// <summary>Terminal kill/interrupt/clear semantics: the shell lives in its own process
/// group, kills never leak outside it, SIGINT interrupts running commands, and Clear
/// empties the server-side buffer.</summary>
public class TerminalLifecycleTests
{
    private static async Task<string> OpenShellAsync(TestHarness harness, Agent agent)
    {
        var opened = await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_open", new { type = "shell" }));
        Assert.False(opened.IsError);
        return opened.V().GetProperty("sessionId").GetString()!;
    }

    private static IReadOnlyList<int> ProcChildren(int pid)
    {
        var children = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !char.IsAsciiDigit(name[0])) continue;
            try
            {
                var text = File.ReadAllText(Path.Combine(dir, "stat"));
                var close = text.LastIndexOf(')');
                if (close < 0) continue;
                var fields = text[(close + 2)..].Split(' ');
                if (fields.Length >= 2 && int.Parse(fields[1]) == pid)
                    children.Add(int.Parse(name));
            }
            catch { /* exited mid-scan */ }
        }
        return children;
    }

    private static bool PidAlive(int pid)
    {
        try { return Directory.Exists($"/proc/{pid}"); } // /proc/<pid> is a directory; File.Exists is false for those
        catch { return false; }
    }

    private static string Cmdline(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace("\0", " ").Trim(); }
        catch { return "<gone>"; }
    }

    [Fact]
    public async Task Shell_RunsInItsOwnProcessGroup()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var harness = TestHarness.Create();
        new TerminalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetTempPath());
        var id = await OpenShellAsync(harness, agent);
        var info = harness.Ctx.Get<Tools.TerminalService>(Tools.TerminalService.ServiceKey).Info(agent, id);

        // own session => pgid == pid, and it is not the harness's group.
        // setsid execs bash in place; under load the stat read can race the exec, so poll.
        var pgrp = -1;
        for (var attempt = 0; attempt < 20 && pgrp != info.Pid; attempt++)
        {
            try
            {
                var statText = File.ReadAllText($"/proc/{info.Pid}/stat");
                var after = statText[(statText.LastIndexOf(')') + 2)..].Split(' ');
                pgrp = int.Parse(after[2]); // state pgrp ppid …
            }
            catch { /* exec raced the read */ }
            if (pgrp != info.Pid) await Task.Delay(100);
        }
        Assert.Equal(info.Pid, pgrp);
        Assert.NotEqual(Environment.ProcessId, pgrp);
    }

    [Fact]
    public async Task CloseShell_KillsItsChildrenAndStopsThere()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var harness = TestHarness.Create();
        new TerminalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetTempPath());
        var service = harness.Ctx.Get<Tools.TerminalService>(Tools.TerminalService.ServiceKey);
        var id = await OpenShellAsync(harness, agent);
        var pid = service.Info(agent, id).Pid;

        // a long-running child under the shell
        await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_send", new { session_id = id, text = "sleep 60" }));
        var children = ProcChildren(pid);
        var diag = string.Join(",", children.Select(c => $"{c}:{PidAlive(c)}:{Cmdline(c)}"));
        Assert.True(children.Count > 0 && children.Any(PidAlive), $"live child expected under {pid}; got [{diag}]");
        var sleepPid = children.First(PidAlive);

        var closed = await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_close", new { session_id = id }));
        Assert.False(closed.IsError);
        await Task.Delay(400);

        Assert.False(PidAlive(pid));                       // the shell died
        if (sleepPid > 0) Assert.False(PidAlive(sleepPid)); // …and so did its child
        Assert.True(PidAlive(Environment.ProcessId));       // the harness did not
    }

    [Fact]
    public async Task Sigint_InterruptsTheRunningCommand()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var harness = TestHarness.Create();
        new TerminalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetTempPath());
        var service = harness.Ctx.Get<Tools.TerminalService>(Tools.TerminalService.ServiceKey);
        var id = await OpenShellAsync(harness, agent);
        var pid = service.Info(agent, id).Pid;

        await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_send", new { session_id = id, text = "sleep 30" }));
        var children = ProcChildren(pid);
        Assert.NotEmpty(children);
        var sleepPid = children[0];

        var signalled = await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_signal",
            new { session_id = id, signal = "SIGINT" }));
        Assert.False(signalled.IsError);

        for (var attempt = 0; attempt < 50 && PidAlive(sleepPid); attempt++) await Task.Delay(100);
        Assert.False(PidAlive(sleepPid)); // the interrupt terminated the running command

        // the shell itself survives and keeps taking commands
        var probe = await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_send",
            new { session_id = id, text = "echo still-here" }));
        Assert.False(probe.IsError);
        Assert.Contains("still-here", probe.V().GetProperty("output").GetString());
    }

    [Fact]
    public async Task Sigint_EscalatesToSigtermForIntIgnoringCommands()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var harness = TestHarness.Create();
        new TerminalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetTempPath());
        var service = harness.Ctx.Get<Tools.TerminalService>(Tools.TerminalService.ServiceKey);
        var id = await OpenShellAsync(harness, agent);
        var pid = service.Info(agent, id).Pid;

        // a command that ignores SIGINT exactly like children of a background-started harness do
        await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_send",
            new { session_id = id, text = "trap '' INT; sleep 45" }));
        var children = ProcChildren(pid);
        Assert.NotEmpty(children);
        var sleepPid = children[0];

        await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_signal",
            new { session_id = id, signal = "SIGINT" }));

        // INT is ignored; the TERM escalation (200ms) must still stop it
        for (var attempt = 0; attempt < 50 && PidAlive(sleepPid); attempt++) await Task.Delay(100);
        Assert.False(PidAlive(sleepPid));

        var probe = await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_send",
            new { session_id = id, text = "echo still-here" }));
        Assert.False(probe.IsError);
        Assert.Contains("still-here", probe.V().GetProperty("output").GetString());
    }

    [Fact]
    public async Task Clear_EmptiesTheBufferForReaders()
    {
        await using var harness = TestHarness.Create();
        new TerminalPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetTempPath());
        var service = harness.Ctx.Get<Tools.TerminalService>(Tools.TerminalService.ServiceKey);
        var id = await OpenShellAsync(harness, agent);
        var sent = await harness.Tools.Execute(TerminalTestInput.For(agent, "terminal_send", new { session_id = id, text = "echo noise" }));
        Assert.False(sent.IsError);
        Assert.Contains("noise", service.Read(agent, id));

        service.Clear(agent, id);
        Assert.Equal("", service.Read(agent, id));
    }
}
