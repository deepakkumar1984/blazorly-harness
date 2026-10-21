using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

/// <summary>
/// The workspace tool picker shows capability families instead of 50 flat checkboxes: tools that
/// only work together share a group (so "background jobs" switches off as a unit), every registered
/// tool lands in a real family, and MCP tools group per server.
/// </summary>
[Collection("BlazorlyHome")]
public sealed class ToolGroupTests : BootstrapperTestBase
{
    private static string Group(string tool) => ToolGroups.Of(tool).Id;

    [Fact]
    public void DependentTools_ShareOneGroup()
    {
        Assert.Single(new[] { "job_list", "job_output", "job_kill" }.Select(Group).Distinct());
        Assert.Single(new[] { "create_goal", "get_goal", "update_goal" }.Select(Group).Distinct());
        Assert.Single(new[] { "schedule_create", "schedule_list", "schedule_delete" }.Select(Group).Distinct());
        Assert.Single(new[] { "subagent_start", "subagent_list", "subagent_send", "subagent_interrupt", "spawn_teammate", "send_message", "team_task_create" }.Select(Group).Distinct());
        Assert.Single(new[] { "bash", "run_code", "terminal_open", "terminal_send", "terminal_read", "terminal_signal", "terminal_close", "terminal_list" }.Select(Group).Distinct());
        Assert.Single(new[] { "read", "write", "edit", "grep", "glob", "spill_read" }.Select(Group).Distinct());
        Assert.Single(new[] { "ralph", "swarm", "workflow", "review" }.Select(Group).Distinct());
        Assert.Single(new[] { "session_search", "session_trace", "session_event_read", "session_event_search", "session_event_trace" }.Select(Group).Distinct());
        // Distinct families stay distinct — grouping everything together would be no grouping at all.
        Assert.NotEqual(Group("read"), Group("bash"));
        Assert.NotEqual(Group("job_list"), Group("schedule_list"));
        Assert.NotEqual(Group("web_search"), Group("session_search"));
    }

    [Fact]
    public void McpTools_GroupPerServer_AndUnknownToolsFallBackToAddOns()
    {
        Assert.Equal("mcp:github", Group("mcp__github__list_pull_requests"));
        Assert.Equal("Add-on: github", ToolGroups.Of("mcp__github__list_pull_requests").Label);
        Assert.NotEqual(Group("mcp__github__list_pull_requests"), Group("mcp__slack__post"));
        Assert.Equal(ToolGroups.Extensions.Id, Group("some_tool_added_later"));
        Assert.Equal(ToolGroups.Extensions.Id, Group(""));
    }

    [Fact]
    public void Catalog_IsOrderedAndUnique()
    {
        Assert.Equal(ToolGroups.Catalog.Count, ToolGroups.Catalog.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([.. ToolGroups.Catalog.Select(g => g.Order).Order()], [.. ToolGroups.Catalog.Select(g => g.Order)]);
        foreach (var group in ToolGroups.Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(group.Label));
            Assert.False(string.IsNullOrWhiteSpace(group.Hint));
        }
    }

    /// <summary>The registry is the source of truth: a new tool that nobody claimed shows up here
    /// as an "Add-ons" row in every workspace picker, which is the gap this test fails on.</summary>
    [Fact]
    public async Task EveryRegisteredTool_HasAFamily_AndThePickerCarriesIt()
    {
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var registered = boot.Tools.Schemas().Select(s => s.Name).ToList();
            // Anything unclaimed would show up as a loose "Add-ons" row in every workspace picker.
            Assert.Empty(registered.Where(name => Group(name) == ToolGroups.Extensions.Id).ToList());

            var picker = new SessionFacade(boot, new UiEventBroker()).AvailableTools();
            Assert.Equal(registered.Count, picker.Count);
            Assert.Contains(picker, o => o.Name == "job_output" && o.Group.Id == "jobs");
            Assert.Contains(picker, o => o.Name == "read" && o.Group.Id == "files");
            Assert.Contains(picker, o => o.Name == "ask_user_question" && o.Group.Id == "questions");
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }
}
