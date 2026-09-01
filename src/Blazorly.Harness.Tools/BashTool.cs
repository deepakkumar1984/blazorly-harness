using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blazorly.Harness.Core.Jobs;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

/// <summary>
/// The bash tool: one fresh `bash -c` per call, confined by Landlock under the session sandbox
/// preset (workspace-write/read-only; danger-full-access runs unconstrained, fail-closed when
/// the confinement helper is unavailable). Optional workdir + timeout; run_in_background
/// registers a job collected via job_* tools.
/// </summary>
public sealed class BashTool : ToolDefinition<BashTool.Args, BashTool.BashOutput>
{
    public sealed record Args(
        string Command,
        string Description,
        int? TimeoutMs = null,
        string? Workdir = null,
        [property: JsonPropertyName("run_in_background")] bool? RunInBackground = false);

    public sealed record BashOutput(
        string Kind, // "foreground" | "background"
        int? ExitCode,
        string? Signal,
        bool TimedOut,
        bool Aborted,
        string Stdout,
        string Stderr,
        int TruncatedAt,
        string? JobId);

    public override string Name => "bash";

    public override string Description =>
        "Execute a bash command (bash -c) and return its stdout/stderr. Each call runs in a fresh shell: "
        + "no state (cwd, variables, functions) persists between calls — pass workdir instead of using cd. "
        + "Non-zero exits are reported as [exit code: N]. Long output is truncated to its tail. "
        + "File mutations are confined to the session workspace by the sandbox; a blocked write reports "
        + "[sandbox: ...] — a policy denial, not a bug. Set run_in_background: true for long-running commands: "
        + "returns a job id immediately; collect with job_output, stop with job_kill.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["command"] = JsonSchema.String("The bash command to execute."),
            ["description"] = JsonSchema.String("Clear, concise description of what this command does in active voice, 5-10 words (shown in the UI)."),
            ["timeoutMs"] = JsonSchema.Number("Timeout in milliseconds; the command is killed on expiry."),
            ["workdir"] = JsonSchema.String("Working directory for this command. Defaults to the session workspace."),
            ["run_in_background"] = JsonSchema.Boolean("Run in the background and return a job id immediately (collect with job_output, stop with job_kill). No timeout applies."),
        },
        required: ["command", "description"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["kind"] = JsonSchema.String(values: [JsonSerializer.SerializeToElement("foreground"), JsonSerializer.SerializeToElement("background")]),
            ["exitCode"] = JsonSchema.Integer(),
            ["signal"] = JsonSchema.String(),
            ["timedOut"] = JsonSchema.Boolean(),
            ["aborted"] = JsonSchema.Boolean(),
            ["stdout"] = JsonSchema.String(),
            ["stderr"] = JsonSchema.String(),
            ["truncatedAt"] = JsonSchema.Integer(),
            ["jobId"] = JsonSchema.String(),
        },
        required: ["kind", "timedOut", "aborted", "stdout", "stderr", "truncatedAt"]);

    private const int MaxOutputChars = 30_000;

    protected override async Task<BashOutput> ExecuteTyped(Args args, ToolRunContext exec)
    {
        var startInfo = BuildStartInfo(args, exec, foreground: true);
        if (args.RunInBackground == true)
        {
            var jobs = BackgroundJobs(exec);
            if (jobs is null)
            {
                throw new ToolException("NO_JOBS", "no jobs runtime is mounted for background execution");
            }
            var startInfoBackground = BuildStartInfo(args, exec, foreground: false);
            var jobId = jobs.StartProcess("bash", args.Description, startInfoBackground, owner: exec.Agent);
            return new BashOutput("background", null, null, false, false, "", "", -1, jobId);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var cwd = startInfo.WorkingDirectory;
        var timeout = args.TimeoutMs is > 0 ? TimeSpan.FromMilliseconds(args.TimeoutMs.Value) : TimeSpan.FromMinutes(10);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(exec.Signal);
        var stderrTask = process.StandardError.ReadToEndAsync(exec.Signal);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(exec.Signal);
        timeoutCts.CancelAfter(timeout);
        int? exitCode = null;
        string? signal = null;
        var timedOut = false;
        var aborted = false;
        try
        {
            process.EnableRaisingEvents = true;
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !exec.Signal.IsCancellationRequested)
        {
            timedOut = true;
            KillTree(process);
            signal = "SIGKILL";
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            KillTree(process);
            signal = "SIGKILL";
        }

        string stdout, stderr;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stdout = stderr = "";
        }

        var (truncatedOut, truncatedAt) = Truncate(stdout);
        return new BashOutput("foreground", exitCode, signal, timedOut, aborted, truncatedOut, Truncate(stderr).Text, truncatedAt, null);
    }

    /// <summary>Landlock-wraps argv under the session sandbox preset; fails closed for mutating modes.</summary>
    internal static ProcessStartInfo BuildStartInfo(Args args, ToolRunContext exec, bool foreground)
    {
        var cwd = ResolveWorkdir(args, exec);
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        startInfo.Environment["BLAZORLY_SESSION"] = exec.Agent?.Id ?? "";

        var sandbox = exec.Agent?.Ctx.TryGet<SandboxPolicy>("sandboxPolicy");
        var mode = exec.Session.LatestSandboxMode() ?? sandbox?.DefaultMode ?? SandboxPolicy.WorkspaceWrite;
        var command = args.Command;
        if (mode is null || mode == SandboxPolicy.WorkspaceWrite)
        {
            var helper = LandlockSandbox.HelperPath();
            if (helper is null)
            {
                // Fail closed: a mutating shell without confinement is exactly what the mode forbids.
                throw new ToolException("SANDBOX_UNAVAILABLE",
                    "[sandbox: bash confinement unavailable (landlock helper could not be built on this machine); " +
                    "switch the session to danger-full-access to run without confinement]");
            }
            startInfo.FileName = helper;
            startInfo.ArgumentList.Add(SandboxPolicy.WorkspaceWrite);
            startInfo.ArgumentList.Add(cwd);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("/bin/bash");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }
        else if (mode == SandboxPolicy.ReadOnly)
        {
            var helper = LandlockSandbox.HelperPath();
            if (helper is null)
            {
                throw new ToolException("SANDBOX_UNAVAILABLE",
                    "[sandbox: bash confinement unavailable; switch to danger-full-access to run without confinement]");
            }
            startInfo.FileName = helper;
            startInfo.ArgumentList.Add(SandboxPolicy.ReadOnly);
            startInfo.ArgumentList.Add(cwd);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("/bin/bash");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }
        else // danger-full-access
        {
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }
        return startInfo;
    }

    private static JobsRuntime? BackgroundJobs(ToolRunContext exec)
        => (exec.Agent?.Ctx ?? throw new ToolException("NO_AGENT", "background execution requires an owning agent"))
            .TryGet<JobsRuntime>("jobs");

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort teardown
        }
    }

    private static string ResolveWorkdir(Args args, ToolRunContext exec)
    {
        if (!string.IsNullOrWhiteSpace(args.Workdir))
        {
            return Path.GetFullPath(args.Workdir, exec.Session.Header.Cwd ?? Directory.GetCurrentDirectory());
        }
        return exec.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
    }

    internal static (string Text, int TruncatedAt) Truncate(string text)
    {
        if (text.Length <= MaxOutputChars) return (text, -1);
        return (text[^MaxOutputChars..], text.Length - MaxOutputChars);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, BashOutput output)
    {
        if (output.Kind == "background")
        {
            return [new TextBlock($"Started background job {output.JobId}: {args.Description}. Read its output with job_output; stop it with job_kill.")];
        }
        var builder = new StringBuilder();
        if (output.Stdout.Length > 0)
        {
            builder.Append(output.Stdout);
            if (!output.Stdout.EndsWith('\n')) builder.Append('\n');
        }
        if (output.Stderr.Length > 0)
        {
            builder.Append(output.Stderr);
            if (!output.Stderr.EndsWith('\n')) builder.Append('\n');
        }
        if (output.TimedOut)
        {
            builder.Append("[timed out]");
        }
        else if (output.Aborted)
        {
            builder.Append("[aborted]");
        }
        else if (output.ExitCode is not 0)
        {
            builder.Append($"[exit code: {output.ExitCode?.ToString() ?? "unknown"}]");
        }
        if (output.TruncatedAt > 0)
        {
            builder.Append($"[output truncated: first {output.TruncatedAt} characters dropped]");
        }
        var text = builder.ToString().TrimEnd();
        return [new TextBlock(text.Length > 0 ? text : "(no output)")];
    }

    protected override ToolCallView? PresentCallTyped(Args args) => new()
    {
        Card = "terminal",
        Kind = "execute",
        Title = args.Command,
        Description = args.Description,
    };

    protected override ToolResultView? PresentResultTyped(Args args, ToolExecutionResult result)
    {
        var text = result.Content.OfType<TextBlock>().FirstOrDefault()?.Text;
        return new ToolResultView { Card = "terminal", Title = args.Description, Text = text };
    }
}
