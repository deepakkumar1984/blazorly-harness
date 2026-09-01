using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.RemoteSandbox;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record RunRemoteArgs(string Command, bool Kill = false);

/// <summary>
/// run_remote: executes a shell command inside the session's remote E2B sandbox
/// (dsh packages/e2b parity). One sandbox per agent, created lazily on first use and kept
/// until killed explicitly or expired server-side. Registered only when E2B is configured.
/// </summary>
public sealed class RemoteSandboxTool(E2bSandboxClient client) : ToolDefinition<RunRemoteArgs, string>
{
    private readonly ConcurrentDictionary<string, string> _sandboxByAgent = new(StringComparer.Ordinal);

    public override string Name => "run_remote";

    public override string Description =>
        "Run a shell command inside this session's remote cloud sandbox (isolated from this machine). "
        + "The sandbox is created on first use and reused afterwards. Kill=true disposes it. "
        + "Use for untrusted or environment-heavy work that must not touch the local workspace.";

    public override int? TimeoutMs => 180000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["command"] = JsonSchema.String("The shell command to run inside the sandbox (bash -lc)."),
            ["kill"] = JsonSchema.Boolean("Set true to terminate this session's sandbox instead of running a command."),
        },
        required: ["command"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.String();

    protected override async Task<string> ExecuteTyped(RunRemoteArgs args, ToolRunContext exec)
    {
        if (exec.Agent is null) throw new ToolException("NO_AGENT", "this tool requires an owning agent");
        var agentKey = exec.Agent.Id;

        if (args.Kill)
        {
            if (!_sandboxByAgent.TryRemove(agentKey, out var existing))
                throw new ToolException("NO_SANDBOX", "this session has no remote sandbox");
            await client.KillAsync(existing, exec.Signal).ConfigureAwait(false);
            return $"sandbox {existing} terminated";
        }

        if (string.IsNullOrWhiteSpace(args.Command)) throw new ToolException("INVALID_ARGS", "command must be non-empty");
        var sandboxId = _sandboxByAgent.GetOrAdd(agentKey, _ => client.CreateAsync(exec.Signal).ConfigureAwait(false).GetAwaiter().GetResult());
        var result = await client.ExecAsync(sandboxId, args.Command, exec.Signal).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.Append("<sandbox id=\"").Append(sandboxId).Append("\" exit=\"").Append(result.ExitCode).Append("\">");
        if (result.Stdout.Length > 0) builder.Append("\n<stdout>\n").Append(result.Stdout.TrimEnd()).Append("\n</stdout>");
        if (result.Stderr.Length > 0) builder.Append("\n<stderr>\n").Append(result.Stderr.TrimEnd()).Append("\n</stderr>");
        builder.Append("\n</sandbox>");
        return builder.ToString();
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(RunRemoteArgs args, string value) => [new TextBlock(value)];
}
