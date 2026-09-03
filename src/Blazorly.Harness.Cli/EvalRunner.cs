using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Blazorly.Harness.Cli;

/// <summary>One shell check: exit 0 passes. Runs with the task workspace as cwd.</summary>
public sealed record EvalCheck(string Name, string Run, int TimeoutSeconds = 30);

/// <summary>Workspace setup: files written before the run, then shell commands.</summary>
public sealed record EvalSetup(
    IReadOnlyDictionary<string, string>? Files,
    IReadOnlyList<string>? Run);

/// <summary>Interruption injected mid-turn: cancel (user-style stop, in-process) or kill
/// (SIGKILL the child process once its first tool call is durable, then killAfterMs later;
/// optionally resume the session). Exactly one mode.</summary>
public sealed record EvalInterrupt(
    int? CancelAfterMs,
    int? KillAfterMs,
    string? ResumePrompt);

/// <summary>One eval task: a prompt plus verifiable shell checks over an isolated workspace.</summary>
public sealed record EvalTask(
    string Id,
    string Description,
    string Prompt,
    string? Provider,
    string? Model,
    int TimeoutSeconds,
    EvalSetup? Setup,
    IReadOnlyList<EvalCheck> Checks,
    string? ExpectFinish = null,
    EvalInterrupt? Interrupt = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Finishes a run may be expected to end with; null means "completed" (exit 0).</summary>
    public static readonly IReadOnlySet<string> KnownFinishes = new HashSet<string>(StringComparer.Ordinal)
    {
        "completed", "max-tokens", "aborted", "interrupted", "error", "blocked",
    };

    /// <summary>The exit code the headless contract maps this finish to.</summary>
    public static int ExitOfFinish(string? finish) => finish switch
    {
        null or "completed" or "max-tokens" => 0,
        "error" or "blocked" => 2,
        "aborted" or "interrupted" => 3,
        _ => throw new ArgumentException($"unknown finish '{finish}'"),
    };

    /// <summary>Loads and validates &lt;dir&gt;/task.json.</summary>
    public static EvalTask Load(string dir)
    {
        var path = Path.Combine(dir, "task.json");
        if (!File.Exists(path))
            throw new EvalLoadException(dir, "missing task.json");
        EvalTaskFile file;
        try
        {
            file = JsonSerializer.Deserialize<EvalTaskFile>(File.ReadAllText(path), Json)
                ?? throw new EvalLoadException(dir, "task.json is empty");
        }
        catch (JsonException ex)
        {
            throw new EvalLoadException(dir, $"invalid task.json: {ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(file.Description))
            throw new EvalLoadException(dir, "description is required");
        if (string.IsNullOrWhiteSpace(file.Prompt))
            throw new EvalLoadException(dir, "prompt is required");
        if (file.Checks is not { Count: > 0 })
            throw new EvalLoadException(dir, "at least one check is required");
        var checks = new List<EvalCheck>();
        foreach (var check in file.Checks)
        {
            if (string.IsNullOrWhiteSpace(check.Name) || string.IsNullOrWhiteSpace(check.Run))
                throw new EvalLoadException(dir, "every check needs a name and a run command");
            checks.Add(new EvalCheck(check.Name, check.Run, check.TimeoutSeconds is > 0 ? check.TimeoutSeconds.Value : 30));
        }
        if (file.ExpectFinish is { } finish && !KnownFinishes.Contains(finish))
            throw new EvalLoadException(dir,
                $"expectFinish must be one of: {string.Join(", ", KnownFinishes)} (got '{finish}')");
        EvalInterrupt? interrupt = null;
        if (file.Interrupt is { } i)
        {
            var cancel = i.CancelAfterMs is > 0 ? i.CancelAfterMs : null;
            var kill = i.KillAfterMs is > 0 ? i.KillAfterMs : null;
            if (cancel is null && kill is null)
                throw new EvalLoadException(dir, "interrupt needs cancelAfterMs or killAfterMs (> 0)");
            if (cancel is not null && kill is not null)
                throw new EvalLoadException(dir, "interrupt supports one mode: cancelAfterMs or killAfterMs, not both");
            if (i.ResumePrompt is not null && kill is null)
                throw new EvalLoadException(dir, "resumePrompt requires killAfterMs (there is nothing to resume after a cancel)");
            interrupt = new EvalInterrupt(cancel, kill, i.ResumePrompt);
        }
        return new EvalTask(
            Path.GetFileName(Path.GetFullPath(dir)),
            file.Description,
            file.Prompt,
            file.Provider,
            file.Model,
            file.TimeoutSeconds is > 0 ? file.TimeoutSeconds.Value : 300,
            file.Setup,
            checks,
            file.ExpectFinish,
            interrupt);
    }

    private sealed record EvalTaskFile(
        string? Description,
        string? Prompt,
        string? Provider,
        string? Model,
        int? TimeoutSeconds,
        EvalSetup? Setup,
        List<EvalCheckFile>? Checks,
        string? ExpectFinish = null,
        EvalInterruptFile? Interrupt = null);

    private sealed record EvalCheckFile(string? Name, string? Run, int? TimeoutSeconds);

    private sealed record EvalInterruptFile(int? CancelAfterMs, int? KillAfterMs, string? ResumePrompt);
}

public sealed class EvalLoadException(string taskDir, string message)
    : Exception($"eval task '{taskDir}': {message}");

public sealed record EvalOptions
{
    public required string TasksDir { get; init; }
    public required string OutDir { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public int DefaultTimeoutSeconds { get; init; } = 300;
    public TextWriter Out { get; init; } = Console.Out;
}

public sealed record EvalCheckResult(string Name, bool Pass, int ExitCode, string Output);

public sealed record EvalTaskResult(
    string Id,
    bool Pass,
    string Finish,
    int ExitCode,
    string? SessionId,
    string Response,
    long DurationMs,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<EvalCheckResult> Checks,
    string? Error);

public sealed record EvalSummary(
    int Total,
    int Passed,
    int Failed,
    long DurationMs,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<EvalTaskResult> Tasks);

/// <summary>
/// Task benchmark runner: each task gets an isolated workspace and a fresh harness home
/// (seeded with the ambient provider keys, so eval sessions never pollute the user's home),
/// runs headless, then scores shell checks. Writes per-task JSON plus results.json/summary.md.
/// Exit contract: caller maps all-pass to 0, anything else to 1.
/// </summary>
public static class EvalRunner
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<EvalSummary> RunAsync(EvalOptions options, CancellationToken ct = default)
    {
        var tasksDir = Path.GetFullPath(options.TasksDir);
        if (!Directory.Exists(tasksDir))
            throw new EvalLoadException(tasksDir, "tasks directory does not exist");
        var dirs = Directory.GetDirectories(tasksDir).OrderBy(Path.GetFileName).ToList();
        if (dirs.Count == 0)
            throw new EvalLoadException(tasksDir, "no tasks found (each task is a directory with task.json)");

        Directory.CreateDirectory(options.OutDir);
        options = options with { OutDir = Path.GetFullPath(options.OutDir) };
        var previousHome = Environment.GetEnvironmentVariable("BLAZORLY_HOME");
        var home = SetupHome(options);
        Environment.SetEnvironmentVariable("BLAZORLY_HOME", home);
        try
        {
            var results = new List<EvalTaskResult>();
            var totalSw = Stopwatch.StartNew();
            foreach (var dir in dirs)
            {
                EvalTask task;
                try
                {
                    task = EvalTask.Load(dir);
                    if (options.DefaultTimeoutSeconds > 0)
                        task = task with { TimeoutSeconds = options.DefaultTimeoutSeconds };
                }
                catch (Exception ex)
                {
                    results.Add(Failure(Path.GetFileName(dir) ?? dir, ex.Message));
                    await options.Out.WriteLineAsync($"FAIL {Path.GetFileName(dir)} (task failed to load: {ex.Message})").ConfigureAwait(false);
                    continue;
                }
                var result = await RunTaskAsync(task, options, ct).ConfigureAwait(false);
                results.Add(result);
                await options.Out.WriteLineAsync(
                    $"{(result.Pass ? "PASS" : "FAIL")} {result.Id} ({result.DurationMs}ms, {result.Finish})").ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(options.OutDir, $"{result.Id}.json"),
                    JsonSerializer.Serialize(result, Json), ct).ConfigureAwait(false);
            }
            totalSw.Stop();
            var summary = new EvalSummary(
                results.Count,
                results.Count(r => r.Pass),
                results.Count(r => !r.Pass),
                totalSw.ElapsedMilliseconds,
                results.Sum(r => r.InputTokens),
                results.Sum(r => r.OutputTokens),
                results);
            await File.WriteAllTextAsync(Path.Combine(options.OutDir, "results.json"),
                JsonSerializer.Serialize(summary, Json), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(options.OutDir, "summary.md"),
                RenderMarkdown(summary), ct).ConfigureAwait(false);
            await options.Out.WriteLineAsync(
                $"eval: {summary.Passed}/{summary.Total} passed in {summary.DurationMs}ms "
                + $"(in {summary.InputTokens} / out {summary.OutputTokens} tokens)").ConfigureAwait(false);
            return summary;
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLAZORLY_HOME", previousHome);
        }
    }

    private static EvalTaskResult Failure(string id, string error) => new(
        id, false, "error", 1, null, "", 0, 0, 0, [], error);

    private static async Task<EvalTaskResult> RunTaskAsync(EvalTask task, EvalOptions options, CancellationToken ct)
    {
        var workspace = Path.Combine(options.OutDir, "workspaces", task.Id);
        try
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            Directory.CreateDirectory(workspace);

            if (task.Setup?.Files is { } files)
            {
                foreach (var (path, content) in files)
                {
                    var full = Path.GetFullPath(path, workspace);
                    if (!full.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        return Failure(task.Id, $"setup file escapes the workspace: {path}");
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllTextAsync(full, content, ct).ConfigureAwait(false);
                }
            }
            if (task.Setup?.Run is { } setupCmds)
            {
                foreach (var cmd in setupCmds)
                {
                    var (exit, output) = await ShellAsync(cmd, workspace, 120, ct).ConfigureAwait(false);
                    if (exit != 0)
                        return Failure(task.Id, $"setup command failed ({exit}): {cmd}\n{output}");
                }
            }

            var sw = Stopwatch.StartNew();
            var run = task.Interrupt?.KillAfterMs is { } killMs
                ? await RunKillResumeAsync(task, options, workspace, killMs, ct).ConfigureAwait(false)
                : await HeadlessRunner.RunAsync(new HeadlessOptions
                {
                    Job = task.Prompt,
                    WorkspacePath = workspace,
                    Provider = task.Provider ?? options.Provider,
                    Model = task.Model ?? options.Model,
                    TimeoutSeconds = task.TimeoutSeconds,
                    CancelAfterMs = task.Interrupt?.CancelAfterMs,
                    Quiet = true,
                }).ConfigureAwait(false);
            sw.Stop();

            if (run.Error is not null)
                return Failure(task.Id, run.Error) with
                {
                    Finish = run.Finish,
                    ExitCode = run.ExitCode,
                    SessionId = run.SessionId,
                    DurationMs = sw.ElapsedMilliseconds,
                };

            var expectedExit = EvalTask.ExitOfFinish(task.ExpectFinish);
            var checks = new List<EvalCheckResult>();
            var checkEnv = CheckEnvironment(options, task.Id, run.SessionId);
            foreach (var check in task.Checks)
            {
                var (exit, output) = await ShellAsync(check.Run, workspace, check.TimeoutSeconds, ct, checkEnv).ConfigureAwait(false);
                checks.Add(new EvalCheckResult(check.Name, exit == 0, exit, output));
            }
            var pass = run.ExitCode == expectedExit && checks.All(c => c.Pass);
            return new EvalTaskResult(
                task.Id, pass, run.Finish, run.ExitCode, run.SessionId,
                Truncate(run.Response, 2000), sw.ElapsedMilliseconds,
                run.Usage?.Input ?? 0, run.Usage?.Output ?? 0,
                checks, pass ? null
                    : run.ExitCode != expectedExit ? $"turn ended {run.Finish} (exit {run.ExitCode}), expected {task.ExpectFinish ?? "completed"} (exit {expectedExit})"
                    : "checks failed");
        }
        catch (Exception ex)
        {
            return Failure(task.Id, ex.Message);
        }
    }

    /// <summary>Restart-kill scenario: the headless run executes in a child process so it can
    /// be SIGKILLed mid-turn. The kill is anchored to the first durable tool/call in the
    /// session log (not wall-clock since spawn — child boot time varies), then lands
    /// killAfterMs later, inside the tool's execution window. The session log then must
    /// reload (repairing the interrupted tail) and, when a resumePrompt is set, continue
    /// to completion in a second child.</summary>
    private static async Task<HeadlessResult> RunKillResumeAsync(
        EvalTask task, EvalOptions options, string workspace, int killAfterMs, CancellationToken ct)
    {
        var killSw = Stopwatch.StartNew();
        var first = SpawnCli(task, workspace, resume: null);
        using var anchorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        anchorCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, task.TimeoutSeconds)));
        var toolCallSeen = WaitForFirstToolCallAsync(options, task.Id, anchorCts.Token);
        var winner = await Task.WhenAny(first.WaitForExitTask, toolCallSeen).ConfigureAwait(false);
        if (winner == first.WaitForExitTask && first.Process.ExitCode is 0 or 2 or 3)
        {
            // The turn finished before the kill anchor fired; surface its real outcome.
            var early = await EnvelopeOfAsync(first).ConfigureAwait(false);
            if (early is not null) return early;
        }
        // Anchor fired (or the child hung past it): kill mid-tool.
        try { await Task.Delay(killAfterMs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        try { first.Process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        await first.WaitForExitTask.ConfigureAwait(false);
        killSw.Stop();

        var session = FindLatestSession(options, task.Id);
        if (session is null)
            return new HeadlessResult { ExitCode = 1, Finish = "error", Error = "kill left no session log to resume" };

        if (task.Interrupt?.ResumePrompt is null)
        {
            // Kill-only: the score is the durable interrupted state; checks assert log integrity.
            return new HeadlessResult
            {
                ExitCode = 3,
                Finish = "interrupted",
                SessionId = session.Value.SessionId,
                Response = $"killed after {killSw.ElapsedMilliseconds}ms mid-turn",
            };
        }

        var resume = SpawnCli(task, workspace, resume: session.Value.SessionId);
        var resumeDone = await Task.WhenAny(
            resume.WaitForExitTask,
            Task.Delay(TimeSpan.FromSeconds(Math.Max(1, task.TimeoutSeconds)), ct)).ConfigureAwait(false);
        if (resumeDone != resume.WaitForExitTask)
        {
            try { resume.Process.Kill(entireProcessTree: true); } catch { }
            return new HeadlessResult { ExitCode = 1, Finish = "error", SessionId = session.Value.SessionId,
                Error = $"resume run exceeded the task timeout ({task.TimeoutSeconds}s)" };
        }
        var envelope = await EnvelopeOfAsync(resume).ConfigureAwait(false);
        var finish = envelope?.Finish ?? (resume.Process.ExitCode == 0 ? "completed" : "error");
        return new HeadlessResult
        {
            ExitCode = resume.Process.ExitCode,
            SessionId = session.Value.SessionId,
            Response = envelope?.Response ?? "",
            Finish = finish,
            Usage = null,
            Error = resume.Process.ExitCode != 0 ? $"resume run exited {resume.Process.ExitCode}" : null,
        };
    }

    /// <summary>Completes when the task's newest session log records its first tool/call —
    /// the deterministic mid-turn anchor. Scoped to the task's own project directory so a
    /// sibling task's log (or a reattached old session) can never satisfy the anchor.
    /// Bounded by the caller's token.</summary>
    private static async Task WaitForFirstToolCallAsync(EvalOptions options, string taskId, CancellationToken ct)
    {
        var projectDir = Path.Combine(options.OutDir, "home", "sessions", Uri.EscapeDataString(taskId));
        while (!ct.IsCancellationRequested)
        {
            if (Directory.Exists(projectDir))
            {
                foreach (var log in Directory.EnumerateFiles(projectDir, "session.jsonl", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (File.ReadAllText(log).Contains("\"tool/call\"", StringComparison.Ordinal)) return;
                    }
                    catch (IOException) { /* mid-append read; try the next tick */ }
                }
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private sealed record CliSpawn(Process Process, Task WaitForExitTask, Task<string> Stdout);

    private static CliSpawn SpawnCli(EvalTask task, string workspace, string? resume)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspace,
        };
        start.ArgumentList.Add(typeof(EvalRunner).Assembly.Location);
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(resume is null ? task.Prompt : task.Interrupt?.ResumePrompt ?? "Continue.");
        start.ArgumentList.Add("--workspace");
        start.ArgumentList.Add(workspace);
        if (!string.IsNullOrWhiteSpace(task.Provider))
        {
            start.ArgumentList.Add("--provider");
            start.ArgumentList.Add(task.Provider);
        }
        if (!string.IsNullOrWhiteSpace(task.Model))
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(task.Model);
        }
        if (resume is not null)
        {
            start.ArgumentList.Add("--resume");
            start.ArgumentList.Add(resume);
        }
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--quiet");
        // BLAZORLY_HOME is already the eval home for this process; children inherit it.
        var process = Process.Start(start) ?? throw new InvalidOperationException("failed to spawn the headless cli");
        return new CliSpawn(process, process.WaitForExitAsync(), process.StandardOutput.ReadToEndAsync());
    }

    /// <summary>Builds a result from the child's --json envelope (the last JSON object line).</summary>
    private static async Task<HeadlessResult?> EnvelopeOfAsync(CliSpawn spawn)
    {
        try
        {
            var stdout = await spawn.Stdout.ConfigureAwait(false);
            foreach (var line in stdout.Split('\n').Reverse())
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith('{')) continue;
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (!root.TryGetProperty("finish", out var finish)) continue;
                return new HeadlessResult
                {
                    ExitCode = spawn.Process.ExitCode,
                    SessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null,
                    Response = root.TryGetProperty("response", out var response) ? response.GetString() ?? "" : "",
                    Finish = finish.GetString() ?? "completed",
                };
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Newest session log under the task's own project key (the workspace basename;
    /// layout &lt;home&gt;/sessions/&lt;projectKey&gt;/&lt;sessionId&gt;/session.jsonl). Never falls back to
    /// another project's sessions — a kill/resume must operate on this task's log.</summary>
    private static (string SessionId, string Log)? FindLatestSession(EvalOptions options, string taskId)
    {
        var projectDir = Path.Combine(options.OutDir, "home", "sessions", Uri.EscapeDataString(taskId));
        if (!Directory.Exists(projectDir)) return null;
        var candidates = Directory.EnumerateFiles(projectDir, "session.jsonl", SearchOption.AllDirectories)
            .Select(log =>
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(log)!);
                return (SessionId: Uri.UnescapeDataString(dir.Name), Log: log, Modified: File.GetLastWriteTimeUtc(log));
            })
            .OrderByDescending(c => c.Modified)
            .ToList();
        return candidates.Count == 0 ? null : (candidates[0].SessionId, candidates[0].Log);
    }

    /// <summary>Env vars handed to every check command so assertions can reach the session
    /// log without guessing paths: BLAZORLY_SESSION_ID and BLAZORLY_SESSION_LOG.</summary>
    private static Dictionary<string, string>? CheckEnvironment(EvalOptions options, string taskId, string? sessionId)
    {
        if (sessionId is null) return null;
        var env = new Dictionary<string, string> { ["BLAZORLY_SESSION_ID"] = sessionId };
        var log = FindSessionLog(options, taskId, sessionId);
        if (log is not null) env["BLAZORLY_SESSION_LOG"] = log;
        return env;
    }

    private static string? FindSessionLog(EvalOptions options, string taskId, string sessionId)
    {
        var sessionsRoot = Path.Combine(options.OutDir, "home", "sessions");
        if (!Directory.Exists(sessionsRoot)) return null;
        var escaped = Uri.EscapeDataString(sessionId);
        return Directory.EnumerateFiles(sessionsRoot, "session.jsonl", SearchOption.AllDirectories)
            .FirstOrDefault(log =>
                string.Equals(Path.GetFileName(Path.GetDirectoryName(log)), escaped, StringComparison.Ordinal));
    }

    /// <summary>Fresh home seeded from the ambient settings (keys + provider/model), so eval
    /// uses the user's routes without polluting their sessions, spills, or telemetry.</summary>
    internal static string SetupHome(EvalOptions options)
    {
        var home = Path.Combine(options.OutDir, "home");
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        Directory.CreateDirectory(home);
        var ambient = Environment.GetEnvironmentVariable("BLAZORLY_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly");
        var ambientSettings = Path.Combine(ambient, "settings.json");
        if (File.Exists(ambientSettings))
        {
            File.Copy(ambientSettings, Path.Combine(home, "settings.json"));
        }
        else
        {
            File.WriteAllText(Path.Combine(home, "settings.json"), JsonSerializer.Serialize(new
            {
                provider = options.Provider ?? "deepseek",
                model = options.Model ?? "deepseek-v4-flash",
            }));
        }
        return home;
    }

    public static async Task<(int ExitCode, string Output)> ShellAsync(
        string command, string cwd, int timeoutSeconds, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = cwd,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(command);
        if (env is not null)
        {
            foreach (var (key, value) in env)
                process.StartInfo.Environment[key] = value;
        }
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        try
        {
            if (!process.Start()) return (1, "failed to start shell");
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (124, Truncate(output.ToString(), 4000) + "\n(timed out)");
        }
        return (process.ExitCode, Truncate(output.ToString(), 4000));
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[^max..];

    private static string RenderMarkdown(EvalSummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Eval results");
        builder.AppendLine();
        builder.AppendLine($"**{summary.Passed}/{summary.Total} passed** in {summary.DurationMs}ms "
            + $"(in {summary.InputTokens} / out {summary.OutputTokens} tokens).");
        builder.AppendLine();
        builder.AppendLine("| Task | Result | Finish | Time | Checks |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var task in summary.Tasks)
        {
            var checks = task.Checks.Count == 0
                ? "—"
                : string.Join(", ", task.Checks.Select(c => $"{(c.Pass ? "✓" : "✗")} {c.Name}"));
            builder.AppendLine($"| {task.Id} | {(task.Pass ? "PASS" : "FAIL")} | {task.Finish} "
                + $"| {task.DurationMs}ms | {checks} |");
        }
        return builder.ToString();
    }
}
