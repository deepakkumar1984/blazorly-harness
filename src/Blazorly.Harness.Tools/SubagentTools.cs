using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record SubagentStartArgs(
    string Prompt,
    string? Description = null,
    string? Provider = null,
    string? Model = null,
    string? Persona = null,
    string? Mode = null,
    bool Fork = false,
    [property: System.Text.Json.Serialization.JsonPropertyName("output_schema")] JsonElement? OutputSchema = null);

public sealed record SubagentStartOutput(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
    string Status,
    string? Summary = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("finish_kind")] string? FinishKind = null,
    JsonElement? Structured = null,
    string? Diagnostic = null);

public sealed record SubagentSendArgs(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
    string Prompt);

public sealed record SubagentSendOutput(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
    string Summary,
    [property: System.Text.Json.Serialization.JsonPropertyName("finish_kind")] string FinishKind);

public sealed record SubagentListEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
    string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("last_summary")] string? LastSummary,
    int Depth);

public sealed record SubagentListOutput(IReadOnlyList<SubagentListEntry> Children);

public sealed record SubagentInterruptArgs(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId);

public sealed record SubagentInterruptOutput(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
    bool Interrupted);

/// <summary>
/// The generic delegation surface (dsh tool-subagent + tool-subagent-control parity):
/// start children in the foreground (await the result) or background (continuable, runs
/// autonomously), send follow-up messages — cold-resuming settled children — list lineage
/// with activity, and interrupt the current turn without destroying the child.
/// </summary>
public sealed class SubagentStartTool(SubagentService subagents) : ToolDefinition<SubagentStartArgs, SubagentStartOutput>
{
    public override string Name => "subagent_start";

    public override string Description =>
        "Delegate a task to a child agent with its own session. Mode 'foreground' waits for the child to finish and "
        + "returns its summary; mode 'background' starts a continuable child that runs autonomously — read it later "
        + "with subagent_list and steer it with subagent_send. Fork=true seeds the child with this conversation's log "
        + "so it continues from here. OutputSchema (JSON schema) asks the child to end with schema-validated JSON.";

    public override int? TimeoutMs => 600000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["prompt"] = JsonSchema.String("The child's task. Self-contained: the child does not see this conversation unless fork=true."),
            ["description"] = JsonSchema.String("Optional one-line description shown in the trajectory."),
            ["provider"] = JsonSchema.String("Optional provider override for the child."),
            ["model"] = JsonSchema.String("Optional model override for the child."),
            ["persona"] = JsonSchema.String("Optional persona/system framing for the child."),
            ["mode"] = JsonSchema.String(description: "'foreground' (default) or 'background'."),
            ["fork"] = JsonSchema.Boolean(description: "Seed the child with this session's log prefix instead of a fresh context."),
            ["output_schema"] = new JsonSchema.Schema
            {
                Type = "object",
                Description = "Optional JSON schema; the child's final answer is validated against it.",
            },
        },
        required: ["prompt"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(description: "'completed' | 'running' | the turn outcome."),
            ["summary"] = JsonSchema.String(),
            ["finish_kind"] = JsonSchema.String(),
            ["structured"] = new JsonSchema.Schema { Description = "The child's final answer as JSON when output_schema validated." },
            ["diagnostic"] = JsonSchema.String(description: "Present when structured output failed; the raw text still returned in summary."),
        },
        required: ["session_id", "status"]);

    protected override async Task<SubagentStartOutput> ExecuteTyped(SubagentStartArgs args, ToolRunContext exec)
    {
        var parent = exec.Agent ?? throw new ToolException("NO_AGENT", "this tool requires an owning agent");
        if (string.IsNullOrWhiteSpace(args.Prompt)) throw new ToolException("INVALID_ARGS", "prompt must be non-empty");
        var schema = args.OutputSchema is { ValueKind: JsonValueKind.Object } raw ? JsonSchema.Raw(raw.Clone()) : null;
        var request = new SubagentRequest(
            Prompt: args.Prompt.Trim(),
            Description: args.Description,
            Provider: args.Provider,
            Model: args.Model,
            Persona: args.Persona,
            Continuable: string.Equals(args.Mode, "background", StringComparison.OrdinalIgnoreCase),
            Fork: args.Fork,
            OutputSchema: schema);

        if (string.Equals(args.Mode, "background", StringComparison.OrdinalIgnoreCase))
        {
            var started = subagents.SpawnBackgroundAsync(parent, request);
            return new SubagentStartOutput(started.SessionId, started.FinishKind);
        }

        var result = await subagents.SpawnAsync(parent, request, exec.Signal).ConfigureAwait(false);
        return new SubagentStartOutput(result.SessionId, result.FinishKind, result.Summary, result.FinishKind, result.Structured, result.Diagnostic);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SubagentStartArgs args, SubagentStartOutput value)
    {
        var text = value.Diagnostic is { Length: > 0 }
            ? $"[structured output rejected: {value.Diagnostic}]\n{value.Summary}"
            : value.Summary is { Length: > 0 } ? value.Summary : $"subagent {value.SessionId} is {value.Status}";
        return [new TextBlock(text)];
    }

    protected override ToolCallView? PresentCallTyped(SubagentStartArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Subagent",
        Description = args.Description ?? (args.Prompt.Length > 80 ? args.Prompt[..80] : args.Prompt),
    };
}

public sealed class SubagentSendTool(SubagentService subagents) : ToolDefinition<SubagentSendArgs, SubagentSendOutput>
{
    public override string Name => "subagent_send";

    public override string Description =>
        "Send a follow-up instruction to one of your child agents (see subagent_list). Settled continuable children "
        + "are cold-resumed from their persisted session; one-shot children cannot be continued. Waits for the "
        + "child's next turn and returns its summary.";

    public override int? TimeoutMs => 600000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("The child session id."),
            ["prompt"] = JsonSchema.String("The follow-up instruction."),
        },
        required: ["session_id", "prompt"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String(),
            ["summary"] = JsonSchema.String(),
            ["finish_kind"] = JsonSchema.String(),
        },
        required: ["session_id", "summary"]);

    protected override async Task<SubagentSendOutput> ExecuteTyped(SubagentSendArgs args, ToolRunContext exec)
    {
        var parent = exec.Agent ?? throw new ToolException("NO_AGENT", "this tool requires an owning agent");
        if (string.IsNullOrWhiteSpace(args.Prompt)) throw new ToolException("INVALID_ARGS", "prompt must be non-empty");
        var result = await subagents.ContinueAsync(parent, args.SessionId, args.Prompt.Trim(), exec.Signal).ConfigureAwait(false);
        return new SubagentSendOutput(result.SessionId, result.Summary, result.FinishKind);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SubagentSendArgs args, SubagentSendOutput value)
        => [new TextBlock(value.Summary)];
}

public sealed class SubagentListTool(SubagentService subagents) : ToolDefinition<SubagentListArgs, SubagentListOutput>
{
    public override string Name => "subagent_list";

    public override string Description =>
        "List your child agents with activity status. 'live' children are in memory (running or idle); 'settled' "
        + "children exist only as persisted sessions and are resumed by the next subagent_send. Use this to read a "
        + "background child's latest summary.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["children"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["session_id"] = JsonSchema.String(),
                    ["status"] = JsonSchema.String(description: "'live' | 'settled'."),
                    ["last_summary"] = JsonSchema.String(),
                    ["depth"] = JsonSchema.Integer(),
                },
                Required = ["session_id", "status", "depth"],
                AdditionalProperties = false,
            }),
        },
        required: ["children"]);

    protected override async Task<SubagentListOutput> ExecuteTyped(SubagentListArgs args, ToolRunContext exec)
    {
        var parent = exec.Agent ?? throw new ToolException("NO_AGENT", "this tool requires an owning agent");
        var headers = await subagents.ChildrenOfAsync(parent.Id, exec.Signal).ConfigureAwait(false);
        var entries = new List<SubagentListEntry>(headers.Count);
        foreach (var header in headers)
        {
            var live = subagents.GetChild(header.Id);
            string status = "settled";
            string? lastSummary = null;
            if (live is not null)
            {
                status = "live";
                lastSummary = LastSummaryOf(live);
            }
            entries.Add(new SubagentListEntry(header.Id, status, lastSummary, header.DelegationDepth));
        }
        return new SubagentListOutput(entries);
    }

    private static string? LastSummaryOf(Blazorly.Harness.Core.Agent.Agent child)
    {
        for (var i = child.Session.Events.Count - 1; i >= 0; i--)
        {
            var e = child.Session.Events[i];
            if (e.Type != SessionEventTypes.AssistantMessage) continue;
            var payload = SessionEventRead.AssistantMessageOf(e);
            var text = string.Join("\n", payload.Message.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
            if (text.Length > 0) return text;
        }
        return null;
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SubagentListArgs args, SubagentListOutput value)
        => [new TextBlock(value.Children.Count == 0
            ? "no child agents"
            : string.Join("\n", value.Children.Select(c => $"{c.SessionId} [{c.Status}] depth {c.Depth}")))];
}

public sealed class SubagentListArgs;

public sealed class SubagentInterruptTool(SubagentService subagents) : ToolDefinition<SubagentInterruptArgs, SubagentInterruptOutput>
{
    public override string Name => "subagent_interrupt";

    public override string Description =>
        "Interrupt a live child agent's current turn without destroying it (user-cause cancel). The child stays "
        + "available for subagent_send. Settled children cannot be interrupted.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("The child session id."),
        },
        required: ["session_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String(),
            ["interrupted"] = JsonSchema.Boolean(),
        },
        required: ["session_id", "interrupted"]);

    protected override Task<SubagentInterruptOutput> ExecuteTyped(SubagentInterruptArgs args, ToolRunContext exec)
    {
        var child = subagents.GetChild(args.SessionId)
            ?? throw new ToolException("SUBAGENT_NOT_FOUND", $"no live subagent '{args.SessionId}'");
        child.Cancel(AgentCancelCause.User());
        return Task.FromResult(new SubagentInterruptOutput(args.SessionId, true));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SubagentInterruptArgs args, SubagentInterruptOutput value)
        => [new TextBlock($"interrupted {args.SessionId}")];
}

/// <summary>Mounts the generic delegation tools.</summary>
public sealed class SubagentToolsPlugin : HarnessPlugin
{
    public override string Name => "subagent-tools";
    public override string[] Inject { get; } = [ToolRuntime.ServiceKey, SubagentService.ServiceKey];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var subagents = ctx.TryGet<SubagentService>(SubagentService.ServiceKey) ?? SubagentService.Mount(ctx);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);
        ctx.Effect(tools.Register(new SubagentStartTool(subagents)).Dispose);
        ctx.Effect(tools.Register(new SubagentSendTool(subagents)).Dispose);
        ctx.Effect(tools.Register(new SubagentListTool(subagents)).Dispose);
        ctx.Effect(tools.Register(new SubagentInterruptTool(subagents)).Dispose);
        return Task.CompletedTask;
    }
}
