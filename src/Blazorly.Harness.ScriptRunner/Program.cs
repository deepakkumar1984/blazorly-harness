using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

// Blazorly.Harness.ScriptRunner: the confined half of run_code.
//
// The parent (RunCodeTool) spawns one of these per script under landlock-exec, so the
// script's filesystem writes are confined to the session workspace even though the script
// language keeps full .NET API access (System.IO included). Protocol is newline-delimited
// JSON: the parent sends one request line on stdin, the runner answers with frames on stdout.
//
// Parent -> runner (stdin, single line):
//   {"code": "<C# async-method body>"}
// Runner -> parent (stdout, one frame per line):
//   {"type":"tool_call","callId":1,"name":"bash","arguments":{...}}   (needs a tool executed)
// Parent -> runner (stdin, reply line):
//   {"type":"tool_result","callId":1,"result":{...}}
// Runner -> parent (stdout, terminal frame):
//   {"type":"done","console":"...","result":"<raw JSON text of the return value>"}
//   {"type":"error","code":"RUN_CODE_FAILED","message":"..."}
// Console output of the script is captured (never the pipe). Runner stderr is diagnostics
// only. Exit code is 0 on protocol completion (done or error frame); non-zero means the
// runner itself crashed and the parent must fail the call.

static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    // The protocol pipe. Captured before the script's console is redirected: frames must
    // never go into the capture buffer (and replies never come from it).
    private static TextWriter PipeOut = Console.Out;
    private static TextReader PipeIn = Console.In;

    static async Task<int> Main()
    {
        PipeOut = Console.Out;
        PipeIn = Console.In;
        try
        {
            var requestLine = await PipeIn.ReadLineAsync().ConfigureAwait(false);
            if (requestLine is null) return 1;
            string code;
            try
            {
                code = JsonDocument.Parse(requestLine).RootElement.GetProperty("code").GetString() ?? "";
            }
            catch (Exception ex)
            {
                WriteFrame(new { type = "error", code = "RUN_CODE_FAILED", message = $"bad runner request: {ex.Message}" });
                return 0;
            }

            var script = CSharpScript.Create<object?>(code, RunnerOptions.Options, typeof(RunnerGlobals));
            try
            {
                script.Compile();
            }
            catch (CompilationErrorException ex)
            {
                WriteFrame(new { type = "error", code = "RUN_CODE_FAILED", message = ex.Diagnostics.FirstOrDefault()?.GetMessage() ?? ex.Message });
                return 0;
            }

            var console = new StringWriter();
            Console.SetOut(console);
            Console.SetError(console);
            object? returnValue;
            try
            {
                var state = await script.RunAsync(new RunnerGlobals(new RunnerToolProxy())).ConfigureAwait(false);
                if (state.Exception is not null) throw state.Exception;
                returnValue = state.ReturnValue;
            }
            catch (Exception ex)
            {
                WriteFrame(new { type = "error", code = "RUN_CODE_FAILED", message = ex.Message });
                return 0;
            }

            string resultJson;
            try
            {
                resultJson = JsonSerializer.Serialize(returnValue, Json);
            }
            catch (Exception ex)
            {
                WriteFrame(new { type = "error", code = "RUN_CODE_FAILED", message = $"the returned value is not serializable: {ex.Message}" });
                return 0;
            }
            WriteFrame(new { type = "done", console = console.ToString(), result = resultJson });
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[script-runner] fatal: {ex}");
            return 1;
        }
    }

    internal static void WriteFrame(object frame)
    {
        PipeOut.WriteLine(JsonSerializer.Serialize(frame, Json));
        PipeOut.Flush();
    }

    internal static string? ReadReply()
    {
        try { return PipeIn.ReadLine(); }
        catch { return null; }
    }
}

/// <summary>Script references; mirrors the parent tool minus the server-side globals type.</summary>
static class RunnerOptions
{
    public static readonly ScriptOptions Options = ScriptOptions.Default
        .AddReferences(
            typeof(object).Assembly,
            typeof(Console).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Task).Assembly,
            typeof(System.IO.File).Assembly,
            typeof(JsonSerializer).Assembly,
            typeof(List<>).Assembly,
            typeof(RunnerGlobals).Assembly)
        .AddImports(
            "System",
            "System.IO",
            "System.Linq",
            "System.Text.Json",
            "System.Threading.Tasks",
            "System.Collections.Generic");
}

/// <summary>Globals visible to confined scripts: tool calls round-trip to the parent process.</summary>
public sealed class RunnerGlobals(RunnerToolProxy tools)
{
    public RunnerToolProxy Tools { get; } = tools;
}

public sealed class RunnerToolProxy
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };
    private int _nextId;

    public async Task<JsonElement> CallAsync(string name, object arguments)
    {
        var callId = Interlocked.Increment(ref _nextId);
        var argsElement = arguments is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(arguments, Json);
        Program.WriteFrame(new { type = "tool_call", callId, name, arguments = argsElement });
        var replyLine = await Task.Run(Program.ReadReply).ConfigureAwait(false);
        if (replyLine is null) throw new InvalidOperationException("script runner lost the parent pipe");
        using var reply = JsonDocument.Parse(replyLine);
        var root = reply.RootElement;
        if (root.GetProperty("type").GetString() != "tool_result"
            || root.GetProperty("callId").GetInt32() != callId)
            throw new InvalidOperationException("script runner protocol violation");
        return root.GetProperty("result").Clone();
    }
}
