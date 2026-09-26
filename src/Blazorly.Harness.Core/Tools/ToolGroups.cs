namespace Blazorly.Harness.Core.Tools;

/// <summary>One row of a human-facing tool list: a capability family in plain language.</summary>
public sealed record ToolGroup(string Id, string Label, string Hint, int Order);

/// <summary>
/// Groups the tool set into capability families for people, not models: the workspace tool picker
/// shows these instead of 50 flat checkboxes. Tools that only work together land in one group, so
/// "background jobs" is switched off as a unit rather than by reasoning about <c>job_list</c> vs
/// <c>job_output</c> vs <c>job_kill</c>.
/// </summary>
/// <remarks>
/// Keyed by tool name (explicit entries plus family prefixes) on purpose: the families are product
/// vocabulary that spans plugins, and MCP tools already carry their server in the name
/// (<c>mcp__server__tool</c>), which becomes a group of its own. <see cref="Extensions"/> catches
/// anything unclaimed — a registered tool landing there is a gap in this table, which
/// ToolGroupsTests fails on.
/// </remarks>
public static class ToolGroups
{
    public static readonly ToolGroup Files = new("files", "Files & code",
        "Read, write and edit files in the folder, search them, and inspect code.", 10);

    public static readonly ToolGroup Commands = new("commands", "Run commands",
        "Run shell commands and code here or in a remote sandbox, including interactive terminals.", 20);

    public static readonly ToolGroup Jobs = new("jobs", "Background jobs",
        "Let long-running work continue in the background, then read its output or stop it.", 30);

    public static readonly ToolGroup Planning = new("planning", "Plans, tasks & goals",
        "Keep a task list, hold a long-running goal, and present a plan for your approval.", 40);

    public static readonly ToolGroup Reminders = new("reminders", "Reminders",
        "Schedule a follow-up turn: after a delay, at a time, or on a repeat.", 50);

    public static readonly ToolGroup Helpers = new("helpers", "Helpers & teammates",
        "Spawn child agents and teammates, message them, and share a task list.", 60);

    public static readonly ToolGroup Workflows = new("workflows", "Multi-step routines",
        "Bigger patterns: an open-ended loop, a parallel swarm, a fixed pipeline, an independent review.", 70);

    public static readonly ToolGroup Web = new("web", "Web",
        "Search the web and read pages.", 80);

    public static readonly ToolGroup History = new("history", "Past sessions",
        "Search earlier chats and inspect their event logs.", 90);

    public static readonly ToolGroup Skills = new("skills", "Skills",
        "Load a saved skill's instructions before starting the task it covers.", 100);

    public static readonly ToolGroup Questions = new("questions", "Ask you questions",
        "Pause and ask you for a decision — it waits 15 minutes for an answer.", 110);

    /// <summary>Catch-all: MCP servers and plugins whose tools claim no family.</summary>
    public static readonly ToolGroup Extensions = new("extensions", "Add-ons",
        "Tools added by MCP servers and plugins.", 900);

    /// <summary>The built-in families, in picker order (MCP groups sort just before Add-ons).</summary>
    public static IReadOnlyList<ToolGroup> Catalog { get; } =
        [Files, Commands, Jobs, Planning, Reminders, Helpers, Workflows, Web, History, Skills, Questions, Extensions];

    private const string McpPrefix = "mcp__";

    private static readonly Dictionary<string, ToolGroup> ByName = new(StringComparer.Ordinal)
    {
        ["read"] = Files,
        ["read_image"] = Files,
        ["write"] = Files,
        ["edit"] = Files,
        ["glob"] = Files,
        ["grep"] = Files,
        ["spill_read"] = Files,
        ["lsp"] = Files,
        ["bash"] = Commands,
        ["run_code"] = Commands,
        ["run_remote"] = Commands,
        ["todo_write"] = Planning,
        ["create_goal"] = Planning,
        ["get_goal"] = Planning,
        ["update_goal"] = Planning,
        ["exit_plan_mode"] = Planning,
        ["spawn_teammate"] = Helpers,
        ["send_message"] = Helpers,
        ["list_agents"] = Helpers,
        ["wait_agent"] = Helpers,
        ["interrupt_agent"] = Helpers,
        ["report"] = Helpers,
        ["ralph"] = Workflows,
        ["swarm"] = Workflows,
        ["workflow"] = Workflows,
        ["review"] = Workflows,
        ["skill"] = Skills,
        ["ask_user_question"] = Questions,
    };

    /// <summary>The family a tool belongs to; unknown names fall into <see cref="Extensions"/>.</summary>
    public static ToolGroup Of(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return Extensions;
        if (ByName.TryGetValue(toolName, out var named)) return named;
        if (toolName.StartsWith("terminal_", StringComparison.Ordinal)) return Commands;
        if (toolName.StartsWith("job_", StringComparison.Ordinal)) return Jobs;
        if (toolName.StartsWith("schedule_", StringComparison.Ordinal)) return Reminders;
        if (toolName.StartsWith("session_", StringComparison.Ordinal)) return History;
        if (toolName.StartsWith("subagent_", StringComparison.Ordinal)) return Helpers;
        if (toolName.StartsWith("team_task_", StringComparison.Ordinal)) return Helpers;
        if (toolName.StartsWith("web_", StringComparison.Ordinal)) return Web;
        if (toolName.StartsWith(McpPrefix, StringComparison.Ordinal)) return McpGroup(toolName);
        return Extensions;
    }

    /// <summary>One group per MCP server, so an add-on can be turned off as a unit.</summary>
    private static ToolGroup McpGroup(string toolName)
    {
        var parts = toolName.Split("__", StringSplitOptions.RemoveEmptyEntries);
        var server = parts.Length > 1 ? parts[1] : "server";
        return new ToolGroup($"mcp:{server}", $"Add-on: {server}", $"Tools from the {server} MCP server.", 850);
    }
}
