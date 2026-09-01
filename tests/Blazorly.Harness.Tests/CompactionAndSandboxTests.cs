using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Compaction;
using Blazorly.Harness.Core.Jobs;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

public class CompactionTests
{
    private static string Big(int chars) => new('x', chars);

    [Fact]
    public async Task PreStepCompactsWhenPressureExceedsThreshold()
    {
        // Compaction calls the provider with purpose=compaction; route those separately.
        var calls = 0;
        await using var harness = TestHarness.Create(options =>
        {
            calls++;
            return options.Purpose == "compaction"
                ? ReplayScript.Text("SUMMARY: the task is underway.")
                : ReplayScript.Text("ok");
        });
        var compaction = CompactionService.Mount(harness.Ctx, new CompactionOptions
        {
            ContextWindowTokens = 8_192,
            Threshold = 0.27,  // trigger ~2211 tokens, between pre- and post-compaction pressure
            KeepRatio = 0.05,  // keep ~410 tokens of tail verbatim
        });
        var agent = harness.CreateAgent();
        // Stuff the surface with big messages until pressure crosses the threshold.
        for (var i = 0; i < 6; i++)
        {
            agent.Session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(i + 1));
            agent.Session.Append(SessionEventTypes.UserMessage, Message.CreateUserText(Big(800) + $" message {i}"),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
            agent.Session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(i + 1, new TurnEndReason.Completed()));
        }
        Assert.True(compaction.ShouldCompact(agent));

        var shadowed = await compaction.CompactAsync(agent);
        Assert.True(shadowed > 0);
        Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.CompactionStart);
        Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.CompactionSummary);
        Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.CompactionEnd);

        // The surface shrank: derived history is the summary message + the kept tail.
        var derived = agent.Session.DeriveMessages();
        Assert.True(derived.Count < 7);
        Assert.Contains(derived, m => m.FlattenText().Contains("SUMMARY: the task is underway."));
        // Pressure fell below the trigger.
        Assert.False(compaction.ShouldCompact(agent));
        Assert.Equal(1, calls); // exactly one compaction call so far (the loop calls are pending)
    }

    [Fact]
    public async Task RequestErrorContextOverflow_CompactsAndRetries()
    {
        var attempts = 0;
        await using var harness = TestHarness.Create(options =>
        {
            if (options.Purpose == "compaction") return ReplayScript.Text("SUMMARY: compacted.");
            attempts++;
            return attempts == 1
                ? ReplayScript.Error(LlmErrorCodes.ContextWindowExceeded, "too long")
                : ReplayScript.Text("recovered after compaction");
        });
        CompactionService.Mount(harness.Ctx, new CompactionOptions
        {
            ContextWindowTokens = 8_192,
            Threshold = 0.27,
            KeepRatio = 0.05,
        });
        var agent = harness.CreateAgent();
        for (var i = 0; i < 4; i++)
        {
            agent.Session.Append(SessionEventTypes.UserMessage, Message.CreateUserText(Big(700)),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        }

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var last = agent.Session.Events.Where(e => e.Type == SessionEventTypes.AssistantMessage).Last();
        Assert.Contains("recovered", SessionEventRead.AssistantMessageOf(last).Message.FlattenText());
        Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.CompactionSummary);
    }

    [Fact]
    public async Task NoCompactionBelowThreshold()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("ok"));
        var compaction = CompactionService.Mount(harness.Ctx, new CompactionOptions { ContextWindowTokens = 1_000_000 });
        var agent = harness.CreateAgent();
        agent.Session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        agent.Session.Append(SessionEventTypes.UserMessage, Message.CreateUserText("tiny"),
            new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        agent.Session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
        Assert.False(compaction.ShouldCompact(agent));
    }
}

public class SandboxedBashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-sbx-" + Guid.NewGuid().ToString("N")[..8]);

    private (TestHarness Harness, Agent Agent) Create(string mode = SandboxPolicy.WorkspaceWrite)
    {
        Directory.CreateDirectory(_root);
        var harness = TestHarness.Create(cwd: _root);
        harness.Sandbox.DefaultMode = mode;
        var agent = harness.CreateAgent(_root);
        return (harness, agent);
    }

    private static ToolExecutionInput Bash(Agent agent, string command) => new()
    {
        Name = "bash",
        Arguments = JsonSerializer.SerializeToElement(new { command, description = "test" }),
        CallId = "call_sb_" + Guid.NewGuid().ToString("N")[..6],
        Signal = CancellationToken.None,
        Agent = agent,
    };

    public static string? SkipReason()
    {
        if (!OperatingSystem.IsLinux()) return "landlock is linux-only";
        return LandlockSandbox.HelperPath() is null ? "landlock helper unavailable on this machine" : null;
    }

    [Fact]
    public async Task WorkspaceWrite_ConfinesBashWritesToWorkspace()
    {
        if (SkipReason() is { } reason)
        {
            return; // best-effort on machines without cc; confinement verified where supported
        }
        var (harness, agent) = Create();
        await using var _ = harness;

        var inside = await harness.Tools.Execute(Bash(agent, $"echo payload > {_root}/inside.txt && cat {_root}/inside.txt"));
        Assert.False(inside.IsError, inside.Error?.Message);
        Assert.Contains("payload", Assert.IsType<TextBlock>(inside.Content.Single()).Text);

        var outsidePath = Path.Combine(Path.GetTempPath(), $"blazorly-escape-{Guid.NewGuid():N}.txt");
        var outside = await harness.Tools.Execute(Bash(agent, $"echo bad > {outsidePath}"));
        var text = Assert.IsType<TextBlock>(outside.Content.Single()).Text;
        Assert.Contains("[exit code: 1]", text); // the write is denied by landlock
        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public async Task ReadOnly_DeniesEvenWorkspaceWrites()
    {
        if (SkipReason() is not null) return;
        var (harness, agent) = Create(SandboxPolicy.ReadOnly);
        await using var _ = harness;

        var blocked = await harness.Tools.Execute(Bash(agent, $"echo nope > {_root}/blocked.txt"));
        Assert.Contains("[exit code: 1]", Assert.IsType<TextBlock>(blocked.Content.Single()).Text);
        Assert.False(File.Exists(Path.Combine(_root, "blocked.txt")));
    }

    [Fact]
    public async Task DangerFullAccess_RunsUnconstrained()
    {
        var (harness, agent) = Create(SandboxPolicy.DangerFullAccess);
        await using var _ = harness;
        var run = await harness.Tools.Execute(Bash(agent, "echo free > /tmp/blazorly-free-check.txt && cat /tmp/blazorly-free-check.txt"));
        Assert.False(run.IsError);
        Assert.Contains("free", Assert.IsType<TextBlock>(run.Content.Single()).Text);
    }

    [Fact]
    public async Task SandboxedBash_CanStillReadOutsideWorkspace()
    {
        if (SkipReason() is not null) return;
        var (harness, agent) = Create();
        await using var _ = harness;
        var read = await harness.Tools.Execute(Bash(agent, "cat /etc/hostname"));
        Assert.False(read.IsError);
        Assert.True(read.Content.OfType<TextBlock>().Single().Text.Length > 0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class BackgroundBashTests
{
    [Fact]
    public async Task RunInBackground_ReturnsJobIdAndCollectsOutput()
    {
        await using var harness = TestHarness.Create();
        var jobs = JobsRuntime.Mount(harness.Ctx);
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "bash",
            Arguments = JsonSerializer.SerializeToElement(new { command = "echo bg-output-42", description = "background echo", run_in_background = true }),
            CallId = "call_bg_1",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.False(result.IsError);
        Assert.Contains("job_", Assert.IsType<TextBlock>(result.Content.Single()).Text);

        var jobId = jobs.List().Single().Id;
        for (var i = 0; i < 40 && jobs.Get(jobId)!.Status == "running"; i++)
        {
            await Task.Delay(100);
        }
        Assert.Equal("done", jobs.Get(jobId)!.Status);
        Assert.Equal(0, jobs.Get(jobId)!.ExitCode);
        Assert.Contains("bg-output-42", jobs.ReadOutput(jobId));
    }

    [Fact]
    public async Task JobKill_StopsLongRunningJob()
    {
        await using var harness = TestHarness.Create();
        var jobs = JobsRuntime.Mount(harness.Ctx);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 30");
        var jobId = jobs.StartProcess("bash", "long sleep", psi);
        await Task.Delay(300); // let it start
        Assert.NotNull(jobId);
        Assert.True(jobs.KillJob(jobId));
    }

    [Fact]
    public async Task JobCompletion_InjectsNoticeIntoOwnerInbox()
    {
        await using var harness = TestHarness.Create();
        var jobs = JobsRuntime.Mount(harness.Ctx);
        var agent = harness.CreateAgent();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-c \"echo done-marker\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        jobs.StartProcess("bash", "notice producer", psi, owner: agent);
        for (var i = 0; i < 40 && agent.Inbox.NextStep.Count == 0; i++)
        {
            await Task.Delay(100);
        }
        Assert.Contains(agent.Inbox.NextStep, m => m.FlattenText().Contains("finished"));
    }
}

public class SubagentServiceTests
{
    [Fact]
    public async Task Spawn_RunsChildToIdleAndReturnsSummary()
    {
        await using var harness = TestHarness.Create(options =>
            options.Purpose == "compaction" ? ReplayScript.Text("n/a")
            : options.SessionId is { } id && id.Contains("sub") ? ReplayScript.Text("child computed the answer: 41")
            : ReplayScript.Text("parent ok"));
        var subagents = Blazorly.Harness.Core.Subagents.SubagentService.Mount(harness.Ctx);
        var parent = harness.CreateAgent();

        var result = await subagents.SpawnAsync(parent, new Blazorly.Harness.Core.Subagents.SubagentRequest("compute the answer"), CancellationToken.None);
        Assert.Equal("child computed the answer: 41", result.Summary);
        Assert.Equal("completed", result.FinishKind);

        var child = subagents.GetChild(result.SessionId);
        Assert.NotNull(child);
        Assert.Equal(parent.Id, child!.Session.Header.ParentSession);
        Assert.Equal(1, child.Session.Header.DelegationDepth);
    }

    [Fact]
    public async Task Spawn_RespectsDelegationDepthCap()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("deep"));
        var subagents = Blazorly.Harness.Core.Subagents.SubagentService.Mount(harness.Ctx);
        var parent = harness.CreateAgent();

        var depth0 = await subagents.SpawnAsync(parent, new("one"), CancellationToken.None);
        var child1 = subagents.GetChild(depth0.SessionId)!;
        var depth1 = await subagents.SpawnAsync(child1, new("two"), CancellationToken.None);
        var child2 = subagents.GetChild(depth1.SessionId)!;
        var depth2 = await subagents.SpawnAsync(child2, new("three"), CancellationToken.None);
        var child3 = subagents.GetChild(depth2.SessionId)!;

        await Assert.ThrowsAsync<Kernel.HarnessException>(() =>
            subagents.SpawnAsync(child3, new("four"), CancellationToken.None));
    }
}
