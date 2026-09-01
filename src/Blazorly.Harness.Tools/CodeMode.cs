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
/// </summary>
public sealed class CodeModeToolHost(ToolRuntime tools, ToolRunContext exec)
{
    public async Task<JsonElement> CallAsync(string name, object arguments)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(exec.Signal);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await tools.Execute(new ToolExecutionInput
        {
            Name = name,
            Arguments = JsonSerializer.SerializeToElement(arguments, SessionJson.Options),
            CallId = Ids.NewCallId(),
            Signal = cts.Token,
            Agent = exec.Agent,
            DeferContextAsync = exec.DeferContextAsync,
            ConcludeTurn = exec.ConcludeTurn,
        }).ConfigureAwait(false);
        return result.Value
            ?? JsonSerializer.SerializeToElement(new { isError = true, error = result.Error?.Message ?? "tool failed" }, SessionJson.Options);
    }
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
        + "System.Threading.Tasks and System.Collections.Generic are imported. Console output and the returned "
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
        var script = CSharpScript.Create<object?>(args.Code, Options, typeof(ScriptGlobals));
        try
        {
            script.Compile();
        }
        catch (CompilationErrorException ex)
        {
            throw new ToolException("RUN_CODE_FAILED", ex.Diagnostics.FirstOrDefault()?.GetMessage() ?? ex.Message);
        }

        // Single-user harness: capturing the global console around the run is acceptable.
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var console = new StringWriter();
        Console.SetOut(console);
        Console.SetError(console);
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
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
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
            + "Call tools with `await Tools.CallAsync(\"bash\", new { command = \"ls\", description = \"List files\" });` "
            + "— arguments are anonymous objects matching each tool's schema. Console output and the returned value are "
            + "both reported; return or print only what matters.");
        ctx.Effect(section.Dispose);
        return Task.CompletedTask;
    }
}
