using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Blazorly.Harness.Tools;

/// <summary>
/// The host exposed to run_code scripts: every call goes through the full guarded pipeline
/// (pre-execute, guards, timeout, post-execute) with the outer execution's agent forwarded.
/// Used by the in-process danger-full-access path; confined runs forward from the child instead.
/// </summary>
public sealed class CodeModeToolHost(ToolRuntime tools, ToolRunContext exec)
{
    public Task<JsonElement> CallAsync(string name, object arguments)
        => RunCodeTool.ExecuteForwardedAsync(tools, name,
            JsonSerializer.SerializeToElement(arguments, SessionJson.Options), exec);
}

/// <summary>Script globals for run_code: `Tools.CallAsync(name, args)` runs any registered tool.</summary>
public sealed class ScriptGlobals(CodeModeToolHost tools)
{
    public CodeModeToolHost Tools { get; } = tools;
}

public sealed record RunCodeArgs(string Code, string Description);

public sealed record RunCodeOutput(string Console, System.Text.Json.JsonElement Result);

/// <summary>
/// run_code — Code Mode: the code is the body of an async C# method (top-level await and
/// return work). Console output is captured; the returned value is serialized alongside it.
///
/// Execution is Landlock-confined like bash: workspace-write/read-only sessions run the script
/// in a helper process (`Blazorly.Harness.ScriptRunner`) under landlock-exec, so direct
/// filesystem writes cannot escape the session workspace even though the script language keeps
/// full .NET API access. Tool calls round-trip to this process over JSON lines and go through
/// the normal guarded pipeline. danger-full-access runs in-process (explicit opt-out).
/// Fails closed when the helper cannot be built, exactly like bash.
/// </summary>
public sealed class RunCodeTool(ToolRuntime tools) : ToolDefinition<RunCodeArgs, RunCodeOutput>
{
    private const int MaxRenderChars = 20_000;

    // References anchored on the types the import list exposes; the Tools assembly carries the globals.
    private static readonly ScriptOptions Options = ScriptOptions.Default
        .AddReferences(
            typeof(object).Assembly,
            typeof(Console).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Task).Assembly,
            typeof(System.IO.File).Assembly,
            typeof(JsonSerializer).Assembly,
            typeof(List<>).Assembly,
            typeof(ScriptGlobals).Assembly)
        .AddImports(
            "System",
            "System.IO",
            "System.Linq",
            "System.Text.Json",
            "System.Threading.Tasks",
            "System.Collections.Generic");

    public override string Name => "run_code";

    public override string Description =>
        "Execute a C# snippet: the code is the body of an async method, so top-level await and return work. "
        + "Call tools with Tools.CallAsync(name, arguments); System, System.IO, System.Linq, System.Text.Json, "
        + "System.Threading.Tasks and System.Collections.Generic are imported. The script runs confined to the "
        + "session workspace (Landlock, like bash). Console output and the returned "
        + "value are both reported; return or print only what matters.";

    public override int? TimeoutMs => 180_000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["code"] = JsonSchema.String("The body of an async C# method. Top-level await and return work; call tools with Tools.CallAsync(name, args)."),
            ["description"] = JsonSchema.String("Clear, concise description of what this code does, 5-10 words (shown in the UI)."),
        },
        required: ["code", "description"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["console"] = JsonSchema.String("Text written to Console.Out/Console.Error during the run."),
            ["result"] = new JsonSchema.Schema { Description = "The script's return value serialized to JSON; null when it returned nothing." },
        },
        required: ["console", "result"]);

    protected override async Task<RunCodeOutput> ExecuteTyped(RunCodeArgs args, ToolRunContext exec)
    {
        // Null-agent executions (tests, one-shot hosts) fall back to the process directory
        // and the default mode, exactly like BashTool's workdir fallback.
        var mode = exec.Agent?.Session.LatestSandboxMode()
            ?? exec.Agent?.Ctx.TryGet<SandboxPolicy>("sandboxPolicy")?.DefaultMode
            ?? SandboxPolicy.WorkspaceWrite;
        if (mode == SandboxPolicy.DangerFullAccess)
            return await ExecuteInProcessAsync(args, exec).ConfigureAwait(false);
        return await ExecuteConfinedAsync(args, exec, mode).ConfigureAwait(false);
    }

    /// <summary>Forwarded tool call shared by both paths: same 30s cap, same agent forwarding.</summary>
    internal static async Task<JsonElement> ExecuteForwardedAsync(
        ToolRuntime tools, string name, JsonElement arguments, ToolRunContext exec)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(exec.Signal);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await tools.Execute(new ToolExecutionInput
        {
            Name = name,
            Arguments = arguments,
            CallId = Ids.NewCallId(),
            Signal = cts.Token,
            Agent = exec.Agent,
            DeferContextAsync = exec.DeferContextAsync,
            ConcludeTurn = exec.ConcludeTurn,
        }).ConfigureAwait(false);
        return result.Value
            ?? JsonSerializer.SerializeToElement(new { isError = true, error = result.Error?.Message ?? "tool failed" }, SessionJson.Options);
    }

    /// <summary>
    /// The active console capture for the current async flow. Console.Out/Error are
    /// process-global, so the swapped writer routes each write through this: writes from
    /// the script's flow (the AsyncLocal flows across its awaits) are captured, writes
    /// from unrelated concurrent code (other sessions, tests) fall through to the real
    /// console instead of leaking into the capture.
    /// </summary>
    private static readonly AsyncLocal<StringWriter?> ConsoleCapture = new();

    private sealed class ScopedConsoleWriter(TextWriter fallback) : TextWriter
    {
        private TextWriter Target => ConsoleCapture.Value ?? fallback;

        public override System.Text.Encoding Encoding => Target.Encoding;
        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);
        public override void Write(ReadOnlySpan<char> buffer) => Target.Write(buffer);
        public override void WriteLine(string? value) => Target.WriteLine(value);
    }

    /// <summary>danger-full-access: today's in-process execution, unchanged.</summary>
    private async Task<RunCodeOutput> ExecuteInProcessAsync(RunCodeArgs args, ToolRunContext exec)
    {
        var script = CSharpScript.Create<object?>(args.Code, Options, typeof(ScriptGlobals));
        try
        {
            script.Compile();
        }
        catch (CompilationErrorException ex)
        {
            throw new ToolException("RUN_CODE_FAILED", ex.Diagnostics.FirstOrDefault()?.GetMessage() ?? ex.Message);
        }

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var console = new StringWriter();
        Console.SetOut(new ScopedConsoleWriter(originalOut));
        Console.SetError(new ScopedConsoleWriter(originalError));
        ConsoleCapture.Value = console;
        try
        {
            ScriptState<object?> state;
            try
            {
                state = await script.RunAsync(new ScriptGlobals(new CodeModeToolHost(tools, exec)), exec.Signal).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // the pipeline maps outer timeout/abort cancellations to their own codes
            }
            catch (Exception ex)
            {
                throw new ToolException("RUN_CODE_FAILED", ex.Message);
            }
            if (state.Exception is not null)
            {
                throw new ToolException("RUN_CODE_FAILED", state.Exception.Message);
            }
            return new RunCodeOutput(console.ToString(), Serialize(state.ReturnValue));
        }
        finally
        {
            ConsoleCapture.Value = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// workspace-write/read-only: one landlock-exec child running ScriptRunner.dll. The protocol
    /// is JSON lines (see ScriptRunner/Program.cs); tool calls are forwarded through the pipeline.
    /// </summary>
    private async Task<RunCodeOutput> ExecuteConfinedAsync(RunCodeArgs args, ToolRunContext exec, string mode)
    {
        var helper = LandlockSandbox.HelperPath();
        if (helper is null)
        {
            throw new ToolException("SANDBOX_UNAVAILABLE",
                "[sandbox: run_code confinement unavailable (landlock helper could not be built on this machine); " +
                "switch the session to danger-full-access to run without confinement]");
        }
        var runner = LocateRunner();
        if (runner is null)
        {
            throw new ToolException("RUN_CODE_UNAVAILABLE",
                "[sandbox: the confined script runner was not found beside the app binaries]");
        }
        var cwd = exec.Agent?.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(cwd);
        startInfo.ArgumentList.Add("--");
        // Prefer the runner's native apphost (no muxer needed); fall back to `dotnet <dll>`.
        if (runner.IsApphost)
        {
            startInfo.ArgumentList.Add(runner.Path);
        }
        else
        {
            startInfo.ArgumentList.Add("dotnet");
            startInfo.ArgumentList.Add(runner.Path);
        }
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new ToolException("RUN_CODE_UNAVAILABLE", "[sandbox: could not start the confined script runner]");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ToolException("RUN_CODE_UNAVAILABLE", $"[sandbox: could not start the confined script runner: {ex.Message}]");
        }
        using var _ = process;
        try
        {
            process.StandardInput.AutoFlush = true;
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new { code = args.Code }, SessionJson.Options)).ConfigureAwait(false);
            var stderrTask = process.StandardError.ReadToEndAsync();
            while (true)
            {
                var line = await ReadRunnerLineAsync(process, exec.Signal).ConfigureAwait(false);
                if (line is null) break; // EOF: child exited; handled below
                using var frame = JsonDocument.Parse(line);
                var root = frame.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "tool_call")
                {
                    var callId = root.GetProperty("callId").GetInt32();
                    var name = root.GetProperty("name").GetString() ?? "";
                    var result = await ExecuteForwardedAsync(tools, name, root.GetProperty("arguments").Clone(), exec).ConfigureAwait(false);
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
                        new { type = "tool_result", callId, result }, SessionJson.Options)).ConfigureAwait(false);
                    continue;
                }
                if (type == "done")
                {
                    var console = root.TryGetProperty("console", out var c) ? c.GetString() ?? "" : "";
                    var raw = root.TryGetProperty("result", out var r) ? r.GetString() ?? "null" : "null";
                    JsonElement result;
                    try { result = JsonDocument.Parse(raw).RootElement.Clone(); }
                    catch (JsonException ex) { throw new ToolException("RUN_CODE_FAILED", $"script runner returned bad JSON: {ex.Message}"); }
                    return new RunCodeOutput(console, result);
                }
                if (type == "error")
                {
                    var code = root.TryGetProperty("code", out var ec) ? ec.GetString() ?? "RUN_CODE_FAILED" : "RUN_CODE_FAILED";
                    throw new ToolException(code, root.TryGetProperty("message", out var m) ? m.GetString() ?? "script failed" : "script failed");
                }
                throw new ToolException("RUN_CODE_FAILED", "script runner protocol violation");
            }
            await process.WaitForExitAsync(exec.Signal).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var tail = stderr.Trim();
            if (tail.Length > 2000) tail = "…" + tail[^2000..];
            throw new ToolException("RUN_CODE_FAILED",
                string.IsNullOrEmpty(tail)
                    ? $"the confined script runner exited ({process.ExitCode}) without a result"
                    : $"the confined script runner exited ({process.ExitCode}): {tail}");
        }
        catch (OperationCanceledException)
        {
            KillRunner(process);
            throw; // the pipeline maps outer timeout/abort cancellations to their own codes
        }
        catch (ToolException)
        {
            KillRunner(process);
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            KillRunner(process);
            throw new ToolException("RUN_CODE_FAILED", $"confined script run failed: {ex.Message}");
        }
    }

    private static async Task<string?> ReadRunnerLineAsync(System.Diagnostics.Process process, CancellationToken signal)
    {
        try
        {
            return await process.StandardOutput.ReadLineAsync(signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static void KillRunner(System.Diagnostics.Process process)
    {
        try
        {
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort teardown
        }
    }

    private sealed record RunnerLaunch(string Path, bool IsApphost);

    private static RunnerLaunch? LocateRunner()
    {
        var dirs = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(RunCodeTool).Assembly.Location) ?? "",
        };
        foreach (var dir in dirs)
        {
            var apphost = Path.Combine(dir, "Blazorly.Harness.ScriptRunner");
            if (File.Exists(apphost)) return new RunnerLaunch(apphost, true);
            var dll = Path.Combine(dir, "Blazorly.Harness.ScriptRunner.dll");
            if (File.Exists(dll)) return new RunnerLaunch(dll, false);
        }
        return null;
    }

    private static JsonElement Serialize(object? value)
    {
        try
        {
            return JsonSerializer.SerializeToElement(value, SessionJson.Options);
        }
        catch (Exception ex)
        {
            throw new ToolException("RUN_CODE_FAILED", $"the returned value is not serializable: {ex.Message}");
        }
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(RunCodeArgs args, RunCodeOutput output)
    {
        var builder = new StringBuilder();
        if (output.Console.Length > 0)
        {
            builder.Append(output.Console);
            if (!output.Console.EndsWith('\n')) builder.Append('\n');
        }
        if (output.Result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            builder.AppendLine(output.Result.GetRawText());
        }
        var text = builder.ToString().TrimEnd();
        if (text.Length > MaxRenderChars) text = "…\n" + text[^MaxRenderChars..];
        return [new TextBlock(text.Length > 0 ? text : "(no output)")];
    }

    protected override ToolCallView? PresentCallTyped(RunCodeArgs args) => new()
    {
        Card = "terminal",
        Kind = "execute",
        Title = FirstLine(args.Code),
        Description = args.Description,
    };

    private static string FirstLine(string code)
    {
        var line = code.Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }
}

/// <summary>Mounts run_code (Code Mode) plus its prompt guidance.</summary>
public sealed class CodeModePlugin : HarnessPlugin
{
    public override string Name => "code-mode";
    public override string[] Inject { get; } = [ToolRuntime.ServiceKey];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);
        ctx.Effect(tools.Register(new RunCodeTool(tools)).Dispose);

        var prompt = ctx.Get<SystemPromptService>(SystemPromptService.ServiceKey);
        var section = prompt.RegisterSection("tool:run-code", 107, _ =>
            "Code Mode (run_code): the code is the body of an async C# method — top-level await and return work, "
            + "and System, System.IO, System.Linq, System.Text.Json, System.Threading.Tasks and System.Collections.Generic are imported. "
            + "Scripts run Landlock-confined to the session workspace (like bash); danger-full-access runs in-process. "
            + "Call tools with `await Tools.CallAsync(\"bash\", new { command = \"ls\", description = \"List files\" });` "
            + "— arguments are anonymous objects matching each tool's schema. Console output and the returned value are "
            + "both reported; return or print only what matters.");
        ctx.Effect(section.Dispose);
        return Task.CompletedTask;
    }
}
