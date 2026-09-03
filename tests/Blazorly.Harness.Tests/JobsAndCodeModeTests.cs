using System.Diagnostics;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Jobs;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

public class JobsAndCodeModeTests
{
    private static TestHarness CreateHarness()
    {
        var harness = TestHarness.Create();
        JobsRuntime.Mount(harness.Ctx);
        new JobsPlugin().Apply(harness.Ctx);
        new CodeModePlugin().Apply(harness.Ctx);
        return harness;
    }

    private static Task<ToolExecutionResult> Run(TestHarness harness, string name, object args, Agent? agent = null)
        => harness.Tools.Execute(new ToolExecutionInput
        {
            Name = name,
            Arguments = JsonSerializer.SerializeToElement(args),
            CallId = Ids.NewCallId(),
            Signal = CancellationToken.None,
            Agent = agent,
        });

    private static ProcessStartInfo BashPsi(string command)
    {
        var psi = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        return psi;
    }

    private static async Task WaitForStatus(JobsRuntime jobs, string id, string status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (jobs.Get(id)?.Status != status)
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"job {id} did not reach status '{status}'");
            await Task.Delay(25);
        }
    }

    // ---- job lifecycle ----

    [Fact]
    public async Task JobLifecycle_ListsOutputsAndKills()
    {
        await using var harness = CreateHarness();
        var jobs = harness.Ctx.Get<JobsRuntime>(JobsRuntime.ServiceKey);
        var echoId = jobs.StartProcess("bash", "print hi", BashPsi("echo hi"));
        var sleepId = jobs.StartProcess("bash", "sleep it off", BashPsi("sleep 30"));

        // The sleep job is guaranteed mid-flight; the echo job may settle at any point.
        var running = await Run(harness, "job_list", new { });
        Assert.False(running.IsError);
        var listed = Assert.IsType<TextBlock>(running.Content.Single()).Text;
        Assert.Contains($"{sleepId} [running] bash — sleep it off", listed);
        Assert.Contains($"{echoId} [", listed);

        await WaitForStatus(jobs, echoId, "done");
        var settled = await Run(harness, "job_list", new { });
        Assert.Contains($"{echoId} [done] bash — print hi", Assert.IsType<TextBlock>(settled.Content.Single()).Text);

        var output = await Run(harness, "job_output", new { job_id = echoId });
        Assert.False(output.IsError);
        Assert.Contains("hi", Assert.IsType<TextBlock>(output.Content.Single()).Text);
        Assert.NotNull(output.Value);
        Assert.Equal(echoId, output.Value.Value.GetProperty("jobId").GetString());
        Assert.Equal("done", output.Value.Value.GetProperty("status").GetString());
        Assert.Equal("hi\n", output.Value.Value.GetProperty("output").GetString());

        var kill = await Run(harness, "job_kill", new { job_id = sleepId });
        Assert.False(kill.IsError);
        Assert.Equal(sleepId, kill.Value!.Value.GetProperty("jobId").GetString());
        Assert.True(kill.Value.Value.GetProperty("killed").GetBoolean());
        Assert.Contains("killed", Assert.IsType<TextBlock>(kill.Content.Single()).Text, StringComparison.OrdinalIgnoreCase);

        await WaitForStatus(jobs, sleepId, "done");
        Assert.NotEqual(0, jobs.Get(sleepId)!.ExitCode); // a killed process does not exit 0
    }

    [Fact]
    public async Task JobOutput_UnknownJob_FailsClosed()
    {
        await using var harness = CreateHarness();
        var result = await Run(harness, "job_output", new { job_id = "job_nope" });
        Assert.True(result.IsError);
        Assert.Equal("UNKNOWN_JOB", result.Error!.Info!.Code);
    }

    [Fact]
    public async Task JobsPlugin_MountsJobsRuntime_WhenAbsent()
    {
        await using var harness = TestHarness.Create();
        new JobsPlugin().Apply(harness.Ctx);
        Assert.NotNull(harness.Ctx.TryGet<JobsRuntime>(JobsRuntime.ServiceKey));
        var listed = await Run(harness, "job_list", new { });
        Assert.False(listed.IsError);
        Assert.Contains("No background jobs.", Assert.IsType<TextBlock>(listed.Content.Single()).Text);
    }

    // ---- run_code ----

    [Fact]
    public async Task RunCode_CallsToolsThroughThePipeline_AndReturnsTheResult()
    {
        await using var harness = CreateHarness();
        harness.Tools.Register(new CodeProbeTool());
        var agent = harness.CreateAgent();
        var result = await Run(harness, "run_code", new
        {
            code = "var r = await Tools.CallAsync(\"probe\", new { value = \"x\" });\nreturn r.ToString();",
            description = "Call the probe tool via script",
        }, agent);
        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(result.Content.Single()).Text;
        Assert.Contains("echo:x", text);
        Assert.NotNull(result.Value);
        Assert.Equal("", result.Value.Value.GetProperty("console").GetString());
        Assert.Contains("echo:x", result.Value.Value.GetProperty("result").GetRawText());
    }

    [Fact]
    public async Task RunCode_CapturesConsoleAndReturnValue()
    {
        await using var harness = CreateHarness();
        var result = await Run(harness, "run_code", new
        {
            code = "Console.WriteLine(\"hello from script\");\nreturn 40 + 2;",
            description = "Print a line and return a number",
        });
        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(result.Content.Single()).Text;
        Assert.Contains("hello from script", text);
        Assert.Contains("42", text);
        Assert.NotNull(result.Value);
        Assert.Equal("hello from script\n", result.Value.Value.GetProperty("console").GetString());
        Assert.Equal(42, result.Value.Value.GetProperty("result").GetInt32());
    }

    [Fact]
    public async Task RunCode_RuntimeFailure_BecomesRunCodeFailedError()
    {
        await using var harness = CreateHarness();
        var result = await Run(harness, "run_code", new
        {
            code = "throw new InvalidOperationException(\"boom from script\");",
            description = "A script that throws on purpose",
        });
        Assert.True(result.IsError);
        Assert.Equal("RUN_CODE_FAILED", result.Error!.Info!.Code);
        Assert.Contains("boom from script", result.Error.Message);
    }

    [Fact]
    public async Task RunCode_CompileFailure_BecomesRunCodeFailedError()
    {
        await using var harness = CreateHarness();
        var result = await Run(harness, "run_code", new
        {
            code = "var x = ;",
            description = "A script that does not compile",
        });
        Assert.True(result.IsError);
        Assert.Equal("RUN_CODE_FAILED", result.Error!.Info!.Code);
    }

    [Fact]
    public async Task RunCode_ConfinedToWorkspace_WritesOutsideFail()
    {
        await using var harness = CreateHarness();
        var workspace = Path.Combine(Path.GetTempPath(), "blazorly-code-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        var outside = Path.Combine(Path.GetTempPath(), "blazorly-code-out-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        try
        {
            var agent = harness.CreateAgent(workspace);
            agent.Session.Append(SessionEventTypes.SandboxMode,
                new SessionPayloads.SandboxModePayload(SandboxPolicy.WorkspaceWrite));

            var inside = await Run(harness, "run_code", new
            {
                code = "File.WriteAllText(\"inside.txt\", \"in\");\nreturn \"wrote inside\";",
                description = "Write a file in the workspace",
            }, agent);
            Assert.False(inside.IsError);
            Assert.True(File.Exists(Path.Combine(workspace, "inside.txt")));

            var escaped = outside.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var result = await Run(harness, "run_code", new
            {
                code = $"File.WriteAllText(\"{escaped}\", \"OUT\");\nreturn \"escaped\";",
                description = "Try to write outside the workspace",
            }, agent);
            Assert.True(result.IsError);
            Assert.Equal("RUN_CODE_FAILED", result.Error!.Info!.Code);
            Assert.False(File.Exists(outside));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
            try { File.Delete(outside); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RunCode_ReadOnly_AllowsReadsBlocksWrites()
    {
        await using var harness = CreateHarness();
        var workspace = Path.Combine(Path.GetTempPath(), "blazorly-code-ro-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "data.txt"), "readable");
        try
        {
            var agent = harness.CreateAgent(workspace);
            agent.Session.Append(SessionEventTypes.SandboxMode,
                new SessionPayloads.SandboxModePayload(SandboxPolicy.ReadOnly));

            var read = await Run(harness, "run_code", new
            {
                code = "return File.ReadAllText(\"data.txt\");",
                description = "Read a workspace file",
            }, agent);
            Assert.False(read.IsError);
            Assert.Contains("readable", read.Value!.Value.GetProperty("result").GetString());

            var write = await Run(harness, "run_code", new
            {
                code = "File.WriteAllText(\"blocked.txt\", \"x\");\nreturn \"wrote\";",
                description = "Try to write in read-only mode",
            }, agent);
            Assert.True(write.IsError);
            Assert.False(File.Exists(Path.Combine(workspace, "blocked.txt")));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Small probe tool: echoes its value through the guarded pipeline.</summary>
    private sealed class CodeProbeTool : ToolDefinition<CodeProbeTool.Args, string>
    {
        public sealed record Args(string Value);

        public override string Name => "probe";
        public override string Description => "test probe for run_code";

        public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema.Schema>
            {
                ["value"] = JsonSchema.String(),
            },
            required: ["value"]);

        public override JsonSchema.Schema Output { get; } = JsonSchema.String();

        protected override Task<string> ExecuteTyped(Args args, ToolRunContext exec) => Task.FromResult($"echo:{args.Value}");

        protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, string value) => [new TextBlock(value)];

        protected override bool IsConcurrencySafeTyped(Args args) => true;
    }
}
