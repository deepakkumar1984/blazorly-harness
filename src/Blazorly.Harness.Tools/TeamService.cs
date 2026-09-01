using System.Text.Json;
using System.Text.Json.Serialization;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

// ---- durable team state: records folded from the lead session's log ----

public sealed record TeamMember(string SessionId, string Label, string Status /*active|idle*/);

public sealed record TeamTask(string Id, string Title, string Status /*open|in_progress|done*/, string? Assignee);

public sealed record TeamMessage(string Id, string From, string To, string Body);

public static class TeamMemberStatus
{
    public const string Active = "active";
    public const string Idle = "idle";
}

public static class TeamTaskStatus
{
    public const string Open = "open";
    public const string InProgress = "in_progress";
    public const string Done = "done";

    public static readonly IReadOnlyList<string> All = [Open, InProgress, Done];
}

/// <summary>Shared schema fragments for team records as they serialize into tool outputs.</summary>
internal static class TeamSchemas
{
    public static JsonSchema.Schema Task { get; } = new()
    {
        Type = "object",
        Properties = new Dictionary<string, JsonSchema.Schema>
        {
            ["id"] = JsonSchema.String(),
            ["title"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(values: [.. TeamTaskStatus.All.Select(s => JsonSerializer.SerializeToElement(s))]),
            ["assignee"] = JsonSchema.String(),
        },
        Required = ["id", "title", "status"],
        AdditionalProperties = false,
    };

    public static JsonSchema.Schema TaskArray { get; } = JsonSchema.Array(Task);
}

internal static class DelegationGuards
{
    public static Agent RequireAgent(ToolRunContext exec)
        => exec.Agent ?? throw new ToolException("NO_AGENT", "this tool requires an owning agent");
}

/// <summary>
/// ctx.agentTeams — durable team state lives in the LEAD session's log. Spawns teammates through
/// the subagent seam, each with a scoped report tool routing summaries back into the lead's inbox.
/// Not concurrency-safe: one lead drives one team at a time.
/// </summary>
public sealed class TeamService
{
    public const string ServiceKey = "agentTeams";

    /// <summary>Serialized camelCase into team events: the team id plus exactly one state record.</summary>
    private sealed record TeamEventPayload(string TeamId, TeamMember? Member = null, TeamTask? Task = null, TeamMessage? Message = null);

    private readonly HarnessContext _ctx;
    private readonly SubagentService _subagents;
    private int _taskCounter;
    private int _messageCounter;

    public TeamService(HarnessContext ctx, SubagentService subagents)
    {
        _ctx = ctx;
        _subagents = subagents;
        // Re-installs the scoped report channel when a teammate is cold-resumed (the
        // resumed agent has a fresh scope). Roster-guarded: only rostered children of
        // live lead sessions get the tool; the one-time roster append stays in Setup.
        _subagents.RegisterContinuableSetup(child =>
        {
            var leadId = child.Session.Header.ParentSession;
            if (leadId is null) return;
            var lead = _ctx.Get<SessionStore>(SessionStore.ServiceKey).Get(leadId);
            if (lead is null || !Roster(lead).Any(m => m.SessionId == child.Id)) return;
            var tools = child.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);
            child.Ctx.Effect(tools.RegisterScoped(child.ScopeKey, new ReportTool(this, leadId, child.Id)).Dispose);
        });
    }

    public static TeamService Mount(HarnessContext ctx)
    {
        var existing = ctx.TryGet<TeamService>(ServiceKey);
        if (existing is not null) return existing;
        var service = new TeamService(ctx, ctx.TryGet<SubagentService>(SubagentService.ServiceKey) ?? SubagentService.Mount(ctx));
        ctx.Provide(ServiceKey, service);
        return service;
    }

    // ---- durable appends (the lead session log is the source of truth) ----

    public SessionEvent AppendMember(Session session, string teamId, TeamMember member)
        => session.Append(SessionEventTypes.TeamMember, new TeamEventPayload(teamId, Member: member));

    public SessionEvent AppendTask(Session session, string teamId, TeamTask task)
        => session.Append(SessionEventTypes.TeamTask, new TeamEventPayload(teamId, Task: task));

    public SessionEvent AppendQueued(Session session, string teamId, TeamMessage message)
        => session.Append(SessionEventTypes.TeamMessageQueued, new TeamEventPayload(teamId, Message: message));

    public SessionEvent AppendDelivered(Session session, string teamId, TeamMessage message)
        => session.Append(SessionEventTypes.TeamMessageDelivered, new TeamEventPayload(teamId, Message: message));

    // ---- folds (latest per id wins; custom event types, never model-visible) ----

    public static IReadOnlyList<TeamMember> Roster(Session session)
    {
        var byId = new Dictionary<string, TeamMember>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var e in session.Events)
        {
            if (e.Type != SessionEventTypes.TeamMember) continue;
            var member = SessionJson.FromElement<TeamEventPayload>(e.Data).Member;
            if (member is null) continue;
            if (byId.TryAdd(member.SessionId, member)) order.Add(member.SessionId);
            else byId[member.SessionId] = member;
        }
        return [.. order.Select(id => byId[id])];
    }

    public static IReadOnlyList<TeamTask> Tasks(Session session)
    {
        var byId = new Dictionary<string, TeamTask>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var e in session.Events)
        {
            if (e.Type != SessionEventTypes.TeamTask) continue;
            var task = SessionJson.FromElement<TeamEventPayload>(e.Data).Task;
            if (task is null) continue;
            if (byId.TryAdd(task.Id, task)) order.Add(task.Id);
            else byId[task.Id] = task;
        }
        return [.. order.Select(id => byId[id])];
    }

    public static IReadOnlyList<TeamMessage> Mailbox(Session session)
    {
        var byId = new Dictionary<string, TeamMessage>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var e in session.Events)
        {
            if (e.Type is not (SessionEventTypes.TeamMessageQueued or SessionEventTypes.TeamMessageDelivered)) continue;
            var message = SessionJson.FromElement<TeamEventPayload>(e.Data).Message;
            if (message is null) continue;
            if (byId.TryAdd(message.Id, message)) order.Add(message.Id);
            else byId[message.Id] = message;
        }
        return [.. order.Select(id => byId[id])];
    }

    public static string? LabelOf(Session session, string memberSessionId)
        => Roster(session).FirstOrDefault(m => m.SessionId == memberSessionId)?.Label;

    // ---- live team operations ----

    public Agent? LiveAgent(string sessionId)
        => _ctx.Get<AgentRuntime>(AgentRuntime.ServiceKey).Get(sessionId);

    public string NewMessageId() => $"team-msg-{Interlocked.Increment(ref _messageCounter)}";

    public sealed record TeammateSpawnResult(string SessionId, string Label, string Status, string Summary);

    /// <summary>Spawns a teammate: roster entry in the lead's log, own session, and a scoped report tool.</summary>
    public async Task<TeammateSpawnResult> SpawnTeammateAsync(Agent lead, string label, CancellationToken ct = default)
    {
        label = label.Trim();
        if (label.Length == 0) throw new ToolException("INVALID_ARGS", "teammate label must be non-empty");
        var teamId = lead.Session.Id;
        var result = await _subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: $"Join the team as teammate '{label}'. No work is assigned yet — the lead will send instructions "
                + "with send_message. Reply with one short line confirming you are ready.",
            Description: $"teammate '{label}'",
            Persona: $"You are teammate '{label}' on a team coordinated by a lead. Do your assigned work; "
                + "when you have a report for the lead, call the report tool with your summary.",
            Continuable: true,
            Setup: child =>
            {
                AppendMember(lead.Session, teamId, new TeamMember(child.Id, label, TeamMemberStatus.Active));
                var tools = child.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);
                child.Ctx.Effect(tools.RegisterScoped(child.ScopeKey, new ReportTool(this, teamId, child.Id)).Dispose);
            }), ct).ConfigureAwait(false);

        AppendMember(lead.Session, teamId, new TeamMember(result.SessionId, label, TeamMemberStatus.Idle));
        return new TeammateSpawnResult(result.SessionId, label, TeamMemberStatus.Idle, result.Summary);
    }

    public sealed record TeamSendResult(TeamMessage Message, string Reply);

    /// <summary>Delivers an instruction to a teammate: queued event, child continuation (cold-resuming a settled teammate from its persisted session), delivered event.</summary>
    public async Task<TeamSendResult> SendAsync(Agent lead, string toSessionId, string body, CancellationToken ct = default)
    {
        var teamId = lead.Session.Id;
        var message = new TeamMessage(NewMessageId(), teamId, toSessionId, body);
        AppendQueued(lead.Session, teamId, message);
        MarkMember(lead.Session, toSessionId, TeamMemberStatus.Active);
        var result = await _subagents.ContinueAsync(lead, toSessionId, body, ct).ConfigureAwait(false);
        AppendDelivered(lead.Session, teamId, message);
        MarkMember(lead.Session, toSessionId, TeamMemberStatus.Idle);
        return new TeamSendResult(message, result.Summary);
    }

    /// <summary>Waits for a live teammate to drain to idle.</summary>
    public async Task AwaitIdleAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var child = _subagents.GetChild(sessionId)
            ?? throw new HarnessException("SUBAGENT_NOT_FOUND", $"no live subagent '{sessionId}'");
        await child.WhenIdleAsync().ConfigureAwait(false);
    }

    public TeamTask CreateTask(Session leadSession, string title, string? assignee)
    {
        title = title.Trim();
        if (title.Length == 0) throw new ToolException("INVALID_ARGS", "task title must be non-empty");
        var task = new TeamTask($"task-{Interlocked.Increment(ref _taskCounter)}", title, TeamTaskStatus.Open, assignee);
        AppendTask(leadSession, leadSession.Id, task);
        return task;
    }

    public TeamTask UpdateTask(Session leadSession, string taskId, string? status, string? assignee)
    {
        if (status is not null && !TeamTaskStatus.All.Contains(status))
            throw new ToolException("INVALID_ARGS", $"task status must be one of: {string.Join(", ", TeamTaskStatus.All)}");
        var current = Tasks(leadSession).FirstOrDefault(t => t.Id == taskId)
            ?? throw new ToolException("TASK_NOT_FOUND", $"no team task '{taskId}'");
        var updated = current with { Status = status ?? current.Status, Assignee = assignee ?? current.Assignee };
        AppendTask(leadSession, leadSession.Id, updated);
        return updated;
    }

    private void MarkMember(Session leadSession, string memberSessionId, string status)
    {
        var label = LabelOf(leadSession, memberSessionId) ?? memberSessionId;
        AppendMember(leadSession, leadSession.Id, new TeamMember(memberSessionId, label, status));
    }
}

public sealed record SpawnTeammateArgs(string Label);

public sealed record SpawnTeammateOutput(string SessionId, string Label, string Status, string Summary);

/// <summary>spawn_teammate: adds a teammate with its own session and a scoped report tool.</summary>
public sealed class SpawnTeammateTool(TeamService service) : ToolDefinition<SpawnTeammateArgs, SpawnTeammateOutput>
{
    public override string Name => "spawn_teammate";

    public override string Description =>
        "Spawn a teammate agent with its own session that reports back to you through its report tool. "
        + "The teammate joins idle; send it work with send_message.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["label"] = JsonSchema.String("Short name for the teammate; it appears in the teammate's reports."),
        },
        required: ["label"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["label"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(),
            ["summary"] = JsonSchema.String(),
        },
        required: ["sessionId", "label", "status", "summary"]);

    protected override async Task<SpawnTeammateOutput> ExecuteTyped(SpawnTeammateArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        var spawn = await service.SpawnTeammateAsync(lead, args.Label, exec.Signal).ConfigureAwait(false);
        return new SpawnTeammateOutput(spawn.SessionId, spawn.Label, spawn.Status, spawn.Summary);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SpawnTeammateArgs args, SpawnTeammateOutput output)
        => [new TextBlock($"Teammate '{output.Label}' joined as {output.SessionId} (status: {output.Status}). Opening reply: {output.Summary}")];

    protected override ToolCallView? PresentCallTyped(SpawnTeammateArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = $"Spawn teammate '{args.Label}'",
    };
}

public sealed record ListAgentsArgs;

public sealed record TeamAgentView(string SessionId, string Label, string Status, bool Live);

public sealed record ListAgentsOutput(IReadOnlyList<TeamAgentView> Agents);

/// <summary>list_agents: the durable roster overlaid with each child's live status.</summary>
public sealed class ListAgentsTool(SubagentService subagents) : ToolDefinition<ListAgentsArgs, ListAgentsOutput>
{
    public override string Name => "list_agents";

    public override string Description =>
        "List the team roster: each teammate's label, session id, and live status (active while working, idle otherwise).";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["agents"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["sessionId"] = JsonSchema.String(),
                    ["label"] = JsonSchema.String(),
                    ["status"] = JsonSchema.String(),
                    ["live"] = JsonSchema.Boolean(),
                },
                Required = ["sessionId", "label", "status", "live"],
                AdditionalProperties = false,
            }),
        },
        required: ["agents"]);

    protected override bool IsConcurrencySafeTyped(ListAgentsArgs args) => true;

    protected override Task<ListAgentsOutput> ExecuteTyped(ListAgentsArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        var agents = TeamService.Roster(lead.Session).Select(member =>
        {
            var child = subagents.GetChild(member.SessionId);
            return new TeamAgentView(member.SessionId, member.Label, child?.Status ?? member.Status, child is not null);
        }).ToList();
        return Task.FromResult(new ListAgentsOutput(agents));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(ListAgentsArgs args, ListAgentsOutput output)
    {
        if (output.Agents.Count == 0) return [new TextBlock("The team roster is empty.")];
        var lines = output.Agents.Select(a => $"- {a.Label} ({a.SessionId}): {a.Status}{(a.Live ? "" : " [not live]")}");
        return [new TextBlock("Team roster:\n" + string.Join("\n", lines))];
    }
}

public sealed record SendMessageArgs([property: JsonPropertyName("to_session_id")] string ToSessionId, string Body);

public sealed record SendMessageOutput(string MessageId, string To, string Reply);

/// <summary>send_message: delivers an instruction to a teammate and drains its reply.</summary>
public sealed class SendMessageTool(TeamService service) : ToolDefinition<SendMessageArgs, SendMessageOutput>
{
    public override string Name => "send_message";

    public override string Description =>
        "Send an instruction or question to a teammate by session id. The teammate processes it to completion; "
        + "its final message is returned as the reply.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["to_session_id"] = JsonSchema.String("Session id of the teammate, from spawn_teammate or list_agents."),
            ["body"] = JsonSchema.String("The instruction to deliver."),
        },
        required: ["to_session_id", "body"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["messageId"] = JsonSchema.String(),
            ["to"] = JsonSchema.String(),
            ["reply"] = JsonSchema.String(),
        },
        required: ["messageId", "to", "reply"]);

    protected override async Task<SendMessageOutput> ExecuteTyped(SendMessageArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        var send = await service.SendAsync(lead, args.ToSessionId, args.Body, exec.Signal).ConfigureAwait(false);
        return new SendMessageOutput(send.Message.Id, send.Message.To, send.Reply);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SendMessageArgs args, SendMessageOutput output)
        => [new TextBlock($"Message {output.MessageId} delivered to {output.To}. Teammate reply: {output.Reply}")];

    protected override ToolCallView? PresentCallTyped(SendMessageArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = $"Message → {args.ToSessionId}",
        Description = args.Body.Length > 80 ? args.Body[..80] : args.Body,
    };
}

public sealed record InterruptAgentArgs(string SessionId);

public sealed record InterruptAgentOutput(string SessionId, string Status);

/// <summary>interrupt_agent: cancels a teammate's in-flight work.</summary>
public sealed class InterruptAgentTool(SubagentService subagents) : ToolDefinition<InterruptAgentArgs, InterruptAgentOutput>
{
    public override string Name => "interrupt_agent";

    public override string Description =>
        "Interrupt a teammate by session id: cancels its in-flight work and clears its pending queue. "
        + "Use when a teammate is off course; follow up with send_message to redirect it.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("Session id of the teammate to interrupt."),
        },
        required: ["session_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(),
        },
        required: ["sessionId", "status"]);

    protected override Task<InterruptAgentOutput> ExecuteTyped(InterruptAgentArgs args, ToolRunContext exec)
    {
        DelegationGuards.RequireAgent(exec);
        var child = subagents.GetChild(args.SessionId)
            ?? throw new ToolException("SUBAGENT_NOT_FOUND", $"no live subagent '{args.SessionId}'");
        child.Cancel(AgentCancelCause.User());
        return Task.FromResult(new InterruptAgentOutput(args.SessionId, child.Status));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(InterruptAgentArgs args, InterruptAgentOutput output)
        => [new TextBlock($"Teammate {output.SessionId} interrupted; status: {output.Status}.")];
}

public sealed record TeamTaskCreateArgs(string Title, string? Assignee = null);

/// <summary>team_task_create: appends a durable open task to the lead's log.</summary>
public sealed class TeamTaskCreateTool(TeamService service) : ToolDefinition<TeamTaskCreateArgs, TeamTask>
{
    public override string Name => "team_task_create";

    public override string Description =>
        "Create a shared team task (status open). Track work you delegate across teammates; "
        + "update it with team_task_update as it progresses.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["title"] = JsonSchema.String("What needs to be done, one line."),
            ["assignee"] = JsonSchema.String("Optional teammate session id responsible for the task."),
        },
        required: ["title"]);

    public override JsonSchema.Schema Output { get; } = TeamSchemas.Task;

    protected override Task<TeamTask> ExecuteTyped(TeamTaskCreateArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        var assignee = string.IsNullOrWhiteSpace(args.Assignee) ? null : args.Assignee.Trim();
        return Task.FromResult(service.CreateTask(lead.Session, args.Title, assignee));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TeamTaskCreateArgs args, TeamTask output)
        => [new TextBlock($"Task {output.Id} created: {output.Title} (open).")];

    protected override ToolCallView? PresentCallTyped(TeamTaskCreateArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = $"Team task: {args.Title}",
    };
}

public sealed record TeamTaskListArgs;

public sealed record TeamTaskListOutput(IReadOnlyList<TeamTask> Tasks);

/// <summary>team_task_list: the folded task list from the lead's log.</summary>
public sealed class TeamTaskListTool : ToolDefinition<TeamTaskListArgs, TeamTaskListOutput>
{
    public override string Name => "team_task_list";

    public override string Description => "List the shared team tasks with their status and assignees.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["tasks"] = TeamSchemas.TaskArray,
        },
        required: ["tasks"]);

    protected override bool IsConcurrencySafeTyped(TeamTaskListArgs args) => true;

    protected override Task<TeamTaskListOutput> ExecuteTyped(TeamTaskListArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        return Task.FromResult(new TeamTaskListOutput(TeamService.Tasks(lead.Session)));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TeamTaskListArgs args, TeamTaskListOutput output)
    {
        if (output.Tasks.Count == 0) return [new TextBlock("No team tasks.")];
        var lines = output.Tasks.Select(t => $"- {t.Id} [{t.Status}]{(t.Assignee is null ? "" : $" → {t.Assignee}")}: {t.Title}");
        return [new TextBlock("Team tasks:\n" + string.Join("\n", lines))];
    }
}

public sealed record TeamTaskUpdateArgs(
    [property: JsonPropertyName("task_id")] string TaskId,
    string? Status = null,
    string? Assignee = null);

/// <summary>team_task_update: appends the latest task snapshot (latest per id wins in the fold).</summary>
public sealed class TeamTaskUpdateTool(TeamService service) : ToolDefinition<TeamTaskUpdateArgs, TeamTask>
{
    public override string Name => "team_task_update";

    public override string Description =>
        "Update a team task's status (open, in_progress, done) and/or assignee. Omitted fields keep their current values.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["task_id"] = JsonSchema.String("Task id from team_task_create."),
            ["status"] = JsonSchema.String("New status.", values: [.. TeamTaskStatus.All.Select(s => JsonSerializer.SerializeToElement(s))]),
            ["assignee"] = JsonSchema.String("Teammate session id now responsible for the task."),
        },
        required: ["task_id"]);

    public override JsonSchema.Schema Output { get; } = TeamSchemas.Task;

    protected override Task<TeamTask> ExecuteTyped(TeamTaskUpdateArgs args, ToolRunContext exec)
    {
        var lead = DelegationGuards.RequireAgent(exec);
        var assignee = args.Assignee is null ? null : (string.IsNullOrWhiteSpace(args.Assignee) ? null : args.Assignee.Trim());
        return Task.FromResult(service.UpdateTask(lead.Session, args.TaskId, args.Status, assignee));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TeamTaskUpdateArgs args, TeamTask output)
        => [new TextBlock($"Task {output.Id} updated: {output.Title} ({output.Status}).")];
}

public sealed record WaitAgentArgs(string SessionId);

public sealed record WaitAgentOutput(string SessionId, string Status);

/// <summary>wait_agent: blocks until a teammate drains to idle.</summary>
public sealed class WaitAgentTool(TeamService service) : ToolDefinition<WaitAgentArgs, WaitAgentOutput>
{
    public override string Name => "wait_agent";

    public override string Description =>
        "Wait until a teammate finishes its current work and is idle. Call this before relying on a teammate's results.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("Session id of the teammate to wait for."),
        },
        required: ["session_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(),
        },
        required: ["sessionId", "status"]);

    protected override async Task<WaitAgentOutput> ExecuteTyped(WaitAgentArgs args, ToolRunContext exec)
    {
        DelegationGuards.RequireAgent(exec);
        await service.AwaitIdleAsync(args.SessionId, exec.Signal).ConfigureAwait(false);
        return new WaitAgentOutput(args.SessionId, TeamMemberStatus.Idle);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(WaitAgentArgs args, WaitAgentOutput output)
        => [new TextBlock($"Teammate {output.SessionId} is {output.Status}.")];
}

public sealed record ReportArgs(string Summary);

public sealed record ReportOutput(string MessageId, string To, string Status);

/// <summary>
/// Scoped per-teammate tool: appends queued/delivered team events to the LEAD session and
/// injects the report into the lead's inbox as "[teammate label] summary".
/// </summary>
public sealed class ReportTool(TeamService service, string leadSessionId, string childSessionId)
    : ToolDefinition<ReportArgs, ReportOutput>
{
    public override string Name => "report";

    public override string Description =>
        "Deliver a report to the team lead: your summary of completed work, findings, or blockers. "
        + "This is how you hand results back to the lead.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["summary"] = JsonSchema.String("Your report to the lead: outcomes, findings, and next steps."),
        },
        required: ["summary"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["messageId"] = JsonSchema.String(),
            ["to"] = JsonSchema.String(),
            ["status"] = JsonSchema.String(),
        },
        required: ["messageId", "to", "status"]);

    protected override Task<ReportOutput> ExecuteTyped(ReportArgs args, ToolRunContext exec)
    {
        var summary = args.Summary.Trim();
        if (summary.Length == 0) throw new ToolException("INVALID_ARGS", "report summary must be non-empty");
        if (exec.Agent is not { } reporter || reporter.Id != childSessionId)
            throw new ToolException("NO_AGENT", "report may only be called by the teammate it belongs to");
        var lead = service.LiveAgent(leadSessionId)
            ?? throw new ToolException("LEAD_NOT_LIVE", $"the lead session '{leadSessionId}' is not live");

        var label = TeamService.LabelOf(lead.Session, childSessionId) ?? childSessionId;
        var message = new TeamMessage(service.NewMessageId(), childSessionId, leadSessionId, summary);
        service.AppendQueued(lead.Session, leadSessionId, message);
        lead.Inject(Message.CreateUserText($"[teammate {label}] {summary}"));
        service.AppendDelivered(lead.Session, leadSessionId, message);
        return Task.FromResult(new ReportOutput(message.Id, leadSessionId, "delivered"));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(ReportArgs args, ReportOutput output)
        => [new TextBlock($"Report delivered to the lead (message {output.MessageId}).")];

    protected override ToolCallView? PresentCallTyped(ReportArgs args) => new()
    {
        Card = "generic",
        Kind = "other",
        Title = "Report to lead",
        Description = args.Summary.Length > 80 ? args.Summary[..80] : args.Summary,
    };
}

/// <summary>Mounts agent teams: durable roster/tasks/mailbox in the lead's log plus the team tools.</summary>
public sealed class TeamPlugin : HarnessPlugin
{
    public override string Name => "agent-teams";
    public override string[] Inject { get; } = [ToolRuntime.ServiceKey, SubagentService.ServiceKey];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var subagents = ctx.TryGet<SubagentService>(SubagentService.ServiceKey) ?? SubagentService.Mount(ctx);
        var service = TeamService.Mount(ctx);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey);

        ctx.Effect(tools.Register(new SpawnTeammateTool(service)).Dispose);
        ctx.Effect(tools.Register(new ListAgentsTool(subagents)).Dispose);
        ctx.Effect(tools.Register(new SendMessageTool(service)).Dispose);
        ctx.Effect(tools.Register(new InterruptAgentTool(subagents)).Dispose);
        ctx.Effect(tools.Register(new TeamTaskCreateTool(service)).Dispose);
        ctx.Effect(tools.Register(new TeamTaskListTool()).Dispose);
        ctx.Effect(tools.Register(new TeamTaskUpdateTool(service)).Dispose);
        ctx.Effect(tools.Register(new WaitAgentTool(service)).Dispose);

        var prompt = ctx.Get<SystemPromptService>(SystemPromptService.ServiceKey);
        var section = prompt.RegisterSection("agent-teams", 109, _ =>
            "Agent teams: you coordinate teammates through tools. spawn_teammate adds a teammate with its own "
            + "session and a report tool; teammate reports arrive in your inbox as \"[teammate <label>] <summary>\". "
            + "Delegate with send_message, review the roster with list_agents (live active/idle status), stop a "
            + "teammate with interrupt_agent, and wait_agent before depending on its output. Track shared work with "
            + "team_task_create, team_task_update (open → in_progress → done), and team_task_list; assignees are "
            + "teammate session ids. Team state (roster, tasks, mailbox) is durable in this session's log. "
            + "Team tools are not concurrency-safe: issue them one at a time.");
        ctx.Effect(section.Dispose);
        return Task.CompletedTask;
    }
}
