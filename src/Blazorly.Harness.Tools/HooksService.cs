using System.Diagnostics;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public static class HookPoints
{
    public const string PreStep = "pre-step";
    public const string PostStep = "post-step";
    public const string TurnEnd = "turn-end";
}

public static class HookRunDecision
{
    public const string Allow = "allow";
    public const string Block = "block";
}

/// <summary>A hook entry: point selects when it runs, matcher (null = all) narrows it to a tool name, command runs under /bin/bash -c.</summary>
public sealed record HookConfig(string Point, string? Matcher, string Command);

/// <summary>hook/invoked event payload, appended before the command runs.</summary>
public sealed record HookInvokedPayload(string Point, string? Matcher, string HandlerId);

/// <summary>hook/result event payload, appended after the command runs.</summary>
public sealed record HookResultPayload(string Point, string HandlerId, string Decision, int? ExitCode, long DurationMs, string? Reason = null);

/// <summary>The outcome of one hook run.</summary>
public sealed record HookRun(string Decision, string? Reason, int? ExitCode, long DurationMs);

/// <summary>
/// Loads a hooks.json file ([{"point","matcher","command"}]) and runs the commands as shell
/// hooks. Timeouts, spawn failures, and unparseable output are non-blocking: the decision
/// defaults to allow and the failure is recorded in the hook/result event.
/// </summary>
public sealed class HooksService
{
    public const string ServiceKey = "hooks";
    public const int TimeoutMs = 5_000;

    public HooksService(string? path = null)
    {
        Path = path ?? DefaultPath;
        Hooks = Load(Path);
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly", "hooks.json");

    public string Path { get; }
    public IReadOnlyList<HookConfig> Hooks { get; }

    public IReadOnlyList<HookConfig> HooksAt(string point) => Hooks.Where(h => h.Point == point).ToList();

    /// <summary>
    /// Runs one hook: /bin/bash -c command with BLAZORLY_HOOK_INPUT carrying the trigger
    /// messages as JSON ([{role,text}]), capped at a 5s wall clock.
    /// </summary>
    public async Task<HookRun> RunAsync(HookConfig hook, string inputJson, string? cwd, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Directory.GetCurrentDirectory(),
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(hook.Command);
        startInfo.Environment["BLAZORLY_HOOK_INPUT"] = inputJson;

        var stopwatch = Stopwatch.StartNew();
        int? exitCode = null;
        string stdout = "";
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));
            using var process = Process.Start(startInfo)
                ?? throw new Kernel.HarnessException("HOOK_START", $"failed to spawn the hook process for '{hook.Command}'");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process); // over budget: non-blocking, recorded with a null exit code
            }
            try { stdout = await stdoutTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* discard partial output from timed-out hooks */ }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HookRun(HookRunDecision.Allow, $"hook failed to run: {ex.Message}", null, stopwatch.ElapsedMilliseconds);
        }

        var (decision, reason) = ParseDecision(stdout);
        return new HookRun(decision, reason, exitCode, stopwatch.ElapsedMilliseconds);
    }

    private static (string Decision, string? Reason) ParseDecision(string stdout)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return (HookRunDecision.Allow, null);
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("decision", out var decisionElement)
                && decisionElement.ValueKind == JsonValueKind.String)
            {
                if (decisionElement.GetString() == HookRunDecision.Block)
                {
                    var reason = root.TryGetProperty("reason", out var reasonElement)
                        && reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : null;
                    return (HookRunDecision.Block, reason);
                }
                return (decisionElement.GetString()!, null);
            }
        }
        catch (JsonException)
        {
            // unparseable hook output is non-blocking
        }
        return (HookRunDecision.Allow, null);
    }

    private static List<HookConfig> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new Kernel.HarnessException("HOOKS_CONFIG", $"{path}: the hooks file must be a JSON array of hook objects");
            var hooks = new List<HookConfig>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    throw new Kernel.HarnessException("HOOKS_CONFIG", $"{path}: every hook must be an object");
                var point = RequireString(entry, "point", path);
                if (point is not (HookPoints.PreStep or HookPoints.PostStep or HookPoints.TurnEnd))
                    throw new Kernel.HarnessException("HOOKS_CONFIG",
                        $"{path}: unknown hook point '{point}' (expected pre-step, post-step, or turn-end)");
                string? matcher = null;
                if (entry.TryGetProperty("matcher", out var matcherElement)
                    && matcherElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    if (matcherElement.ValueKind != JsonValueKind.String)
                        throw new Kernel.HarnessException("HOOKS_CONFIG", $"{path}: hook matcher must be a string or null");
                    matcher = matcherElement.GetString();
                }
                var command = RequireString(entry, "command", path);
                if (command.Length == 0)
                    throw new Kernel.HarnessException("HOOKS_CONFIG", $"{path}: hook command must be non-empty");
                hooks.Add(new HookConfig(point, matcher, command));
            }
            return hooks;
        }
        catch (JsonException ex)
        {
            throw new Kernel.HarnessException("HOOKS_CONFIG", $"{path}: invalid JSON — {ex.Message}");
        }
    }

    private static string RequireString(JsonElement entry, string property, string path)
        => entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new Kernel.HarnessException("HOOKS_CONFIG", $"{path}: hook entry is missing string property '{property}'");

    private static void TryKill(Process process)
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
}

/// <summary>
/// Runs shell hooks from ~/.blazorly/hooks.json at agent-loop boundaries. pre-step hooks gate
/// each step through the agent/pre-step waterfall: a hook printing
/// {"decision":"block","reason":"..."} rejects the step without running the model and the turn
/// ends blocked. post-step hooks run on the agent/post-step waterfall after each settled step;
/// a block ends the turn blocked (no further steps). turn-end hooks run on agent/turn-stopping;
/// a blocking decision there is recorded on the session but cannot stop an already-stopping turn.
/// </summary>
public sealed class HooksPlugin(string? path = null) : HarnessPlugin
{
    public override string Name => "hooks";
    public override string[] Inject { get; } = ["tools"];

    public HooksService Service { get; } = new(path);

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        ctx.Provide(HooksService.ServiceKey, Service);

        var preStep = Service.HooksAt(HookPoints.PreStep);
        if (preStep.Count > 0)
        {
            ctx.OnWaterfall<PreStepEvent, List<Message>, PreStepDecision>("agent/pre-step",
                async (payload, value, next, ct) =>
                {
                    foreach (var hook in Matching(preStep, payload.Messages))
                    {
                        var run = await InvokeAsync(payload.Agent, hook, payload.Messages, ct).ConfigureAwait(false);
                        if (run.Decision == HookRunDecision.Block)
                            return PreStepDecision.Reject(); // short-circuit: next() is never called
                    }
                    return await next(value).ConfigureAwait(false);
                });
        }

        var turnEnd = Service.HooksAt(HookPoints.TurnEnd);
        if (turnEnd.Count > 0)
        {
            ctx.On<TurnStoppingEvent>("agent/turn-stopping", async (payload, ct) =>
            {
                var messages = payload.Agent.Session.DeriveMessages();
                foreach (var hook in Matching(turnEnd, messages))
                {
                    await InvokeAsync(payload.Agent, hook, messages, ct).ConfigureAwait(false);
                }
            });
        }

        var postStep = Service.HooksAt(HookPoints.PostStep);
        if (postStep.Count > 0)
        {
            ctx.OnWaterfall<PostStepEvent, PostStepDecision, PostStepDecision>("agent/post-step",
                async (payload, value, next, ct) =>
                {
                    var messages = payload.Agent.Session.DeriveMessages();
                    foreach (var hook in Matching(postStep, messages))
                    {
                        var run = await InvokeAsync(payload.Agent, hook, messages, ct).ConfigureAwait(false);
                        if (run.Decision == HookRunDecision.Block)
                            return PostStepDecision.Stop(); // short-circuit: next() is never called
                    }
                    return await next(value).ConfigureAwait(false);
                });
        }

        return Task.CompletedTask;
    }

    private async Task<HookRun> InvokeAsync(Agent agent, HookConfig hook, IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var input = JsonSerializer.Serialize(
            messages.Select(m => new { role = m.Role, text = m.FlattenText() }), SessionJson.Options);
        agent.Session.Append(SessionEventTypes.HookInvoked,
            new HookInvokedPayload(hook.Point, hook.Matcher, hook.Command));
        var run = await Service.RunAsync(hook, input, agent.Session.Header.Cwd, ct).ConfigureAwait(false);
        agent.Session.Append(SessionEventTypes.HookResult,
            new HookResultPayload(hook.Point, hook.Command, run.Decision, run.ExitCode, run.DurationMs, run.Reason));
        return run;
    }

    /// <summary>A null matcher matches everything; a tool name matches messages that mention that tool.</summary>
    internal static IReadOnlyList<HookConfig> Matching(IReadOnlyList<HookConfig> hooks, IReadOnlyList<Message> messages)
        => hooks.Where(hook => hook.Matcher is null || messages.Any(m =>
            m.Content.OfType<ToolCallBlock>().Any(call => call.Name == hook.Matcher)
            || m.FlattenText().Contains(hook.Matcher, StringComparison.Ordinal))).ToList();
}
