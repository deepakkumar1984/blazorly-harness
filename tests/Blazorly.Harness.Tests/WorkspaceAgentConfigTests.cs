using System.Globalization;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Per-workspace agent configuration: custom instructions replace the configurable body below
/// the static identity header (which always stays) for sessions in that workspace only, and the
/// tool allow-list restricts every agent published into the workspace — live chats on save, new
/// chats on creation, subagents through the same agent/created hook. Storage is the workspace
/// record in workspaces.json, never global settings.
/// </summary>
[Collection("BlazorlyHome")]
public sealed class WorkspaceAgentConfigTests : BootstrapperTestBase
{
    private async Task<HarnessBootstrapper> Boot()
    {
        // Tests that route the LLM (subagent spawn) write their own settings first.
        if (!File.Exists(Path.Combine(Home, "settings.json")))
            File.WriteAllText(Path.Combine(Home, "settings.json"),
                JsonSerializer.Serialize(new { persistence = "sqlite", enableSystemOne = false }));
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        return boot;
    }

    private Workspace Add(HarnessBootstrapper boot, string name)
    {
        var directory = Path.Combine(Home, name);
        Directory.CreateDirectory(directory);
        return boot.Workspaces.Add(name, directory);
    }

    private static string RenderedIdentity(HarnessBootstrapper boot, Agent agent)
    {
        var prompt = boot.Context.Get<SystemPromptService>(SystemPromptService.ServiceKey);
        return SystemPromptService.RenderPrompt(prompt.Assemble(agent, agent.Session.Header.Cwd));
    }

    [Fact]
    public async Task UpdateAgentConfig_PersistsOnTheWorkspaceOnly_AndNormalizes()
    {
        await using var boot = await Boot();
        var ws = Add(boot, "project");
        var other = Add(boot, "other");

        var updated = boot.Workspaces.UpdateAgentConfig(ws.Id, "  You are the docs writer.  ", ["read", "read", " bash "]);
        Assert.Equal("You are the docs writer.", updated.SystemPrompt);
        Assert.Equal(new[] { "read", "bash" }, updated.EnabledTools);

        var reloaded = new WorkspaceRegistry(Home).Get(ws.Id)!;
        Assert.Equal("You are the docs writer.", reloaded.SystemPrompt);
        Assert.Equal(new[] { "read", "bash" }, reloaded.EnabledTools);
        // The neighbour workspace keeps the defaults: nothing leaked into global state.
        Assert.Null(new WorkspaceRegistry(Home).Get(other.Id)!.SystemPrompt);
        Assert.Null(new WorkspaceRegistry(Home).Get(other.Id)!.EnabledTools);

        var cleared = boot.Workspaces.UpdateAgentConfig(ws.Id, "   ", null);
        Assert.Null(cleared.SystemPrompt);
        Assert.Null(cleared.EnabledTools);
        Assert.Throws<InvalidOperationException>(() => boot.Workspaces.UpdateAgentConfig("ws-nope", "x", null));
    }

    [Fact]
    public async Task LegacyWorkspacesFile_LoadsWithNullAgentConfig()
    {
        var project = Path.Combine(Home, "legacy");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(Home, "workspaces.json"), JsonSerializer.Serialize(new
        {
            workspaces = new[] { new { id = "ws-1", name = "Legacy", root = project, order = 0 } },
        }));
        await using var boot = await Boot();
        var ws = boot.Workspaces.Get("ws-1")!;
        Assert.Null(ws.SystemPrompt);
        Assert.Null(ws.EnabledTools);
    }

    [Fact]
    public async Task WorkspaceInstruction_ReplacesIdentityForThatWorkspaceOnly()
    {
        await using var boot = await Boot();
        var ws = Add(boot, "project");
        var other = Add(boot, "other");
        boot.Workspaces.UpdateAgentConfig(ws.Id, "You are the docs writer in {{cwd}}. Keep {{unknown}} literal.", null);

        var inside = boot.Loop.Create(new SessionMeta(Cwd: ws.Root));
        var insideText = RenderedIdentity(boot, inside);
        // Plain (non-interpolated) literal: the lenient section keeps {{unknown}} verbatim.
        Assert.Contains("You are the docs writer in " + ws.Root + ". Keep {{unknown}} literal.", insideText);
        // The override replaces the configurable body; the static identity header always stays.
        Assert.DoesNotContain(AgentLoopService.DefaultIdentityBody, insideText);
        Assert.Contains("You are Blazorly Harness, an agentic coding assistant", insideText);

        // Another workspace — and a cwd outside every workspace — keep the default body.
        var outside = boot.Loop.Create(new SessionMeta(Cwd: other.Root));
        Assert.Contains(AgentLoopService.DefaultIdentityBody, RenderedIdentity(boot, outside));
        var unfiledDir = Path.Combine(Home, "unfiled");
        Directory.CreateDirectory(unfiledDir);
        var unfiled = boot.Loop.Create(new SessionMeta(Cwd: unfiledDir));
        Assert.Contains(AgentLoopService.DefaultIdentityBody, RenderedIdentity(boot, unfiled));

        // Edits take effect per request: no restart, no re-attach.
        boot.Workspaces.UpdateAgentConfig(ws.Id, "Second revision.", null);
        Assert.Contains("Second revision.", RenderedIdentity(boot, inside));
    }

    [Fact]
    public async Task EnabledTools_RestrictLiveAndNewAgents_UntilCleared()
    {
        await using var boot = await Boot();
        var ws = Add(boot, "project");
        var other = Add(boot, "other");
        var facade = new SessionFacade(boot, new UiEventBroker());

        var session = facade.CreateSession(ws.Id);
        var agent = boot.Agents.Get(session.Id)!;
        Assert.Contains(boot.Tools.Schemas(agent.ScopeKey), s => s.Name == "bash");

        // Saving while the chat is live applies to it immediately.
        facade.UpdateWorkspaceAgentConfig(ws.Id, null, ["read"]);
        Assert.Equal("read", Assert.Single(boot.Tools.Schemas(agent.ScopeKey)).Name);
        Assert.Null(boot.Tools.Get("bash", agent.ScopeKey));

        var blocked = await boot.Tools.Execute(new ToolExecutionInput
        {
            Name = "bash",
            Arguments = JsonSerializer.SerializeToElement(new { command = "echo denied" }),
            CallId = "call_blocked",
            Signal = CancellationToken.None,
            Agent = agent,
        });
        Assert.True(blocked.IsError);
        Assert.Contains("not available", blocked.Content.OfType<TextBlock>().First().Text);

        // A new session in the workspace is restricted from creation; another workspace is not.
        var second = facade.CreateSession(ws.Id);
        Assert.Equal("read", Assert.Single(boot.Tools.Schemas(boot.Agents.Get(second.Id)!.ScopeKey)).Name);
        var elsewhere = facade.CreateSession(other.Id);
        Assert.Contains(boot.Tools.Schemas(boot.Agents.Get(elsewhere.Id)!.ScopeKey), s => s.Name == "bash");

        // Clearing the selection restores every tool.
        facade.UpdateWorkspaceAgentConfig(ws.Id, null, null);
        Assert.Contains(boot.Tools.Schemas(agent.ScopeKey), s => s.Name == "bash");
    }

    [Fact]
    public async Task EmptyToolList_LeavesTheAgentWithoutTools()
    {
        await using var boot = await Boot();
        var ws = Add(boot, "project");
        boot.Workspaces.UpdateAgentConfig(ws.Id, null, []);
        var agent = boot.Loop.Create(new SessionMeta(Cwd: ws.Root));
        Assert.Empty(boot.Tools.Schemas(agent.ScopeKey));
    }

    [Fact]
    public async Task AgentDisposal_ReleasesTheRestrictionLayer()
    {
        await using var boot = await Boot();
        var ws = Add(boot, "project");
        boot.Workspaces.UpdateAgentConfig(ws.Id, null, ["read"]);
        var agent = boot.Loop.Create(new SessionMeta(Cwd: ws.Root));
        Assert.Contains(agent.ScopeKey, boot.Tools.ScopedKeys);
        await agent.DisposeAsync();
        Assert.DoesNotContain(agent.ScopeKey, boot.Tools.ScopedKeys);
    }

    [Fact]
    public async Task Subagents_InheritWorkspaceInstructionAndToolAllowList()
    {
        // The scripted child turn asks for bash + todo_write; both must be denied by the
        // workspace allow-list, which only leaves the summary step.
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        await using var boot = await Boot();
        var ws = Add(boot, "project");
        boot.Workspaces.UpdateAgentConfig(ws.Id, "Custom workspace identity for testing.", ["read"]);

        var parent = boot.Loop.Create(new SessionMeta(Cwd: ws.Root));
        Assert.Equal("read", Assert.Single(boot.Tools.Schemas(parent.ScopeKey)).Name);

        var result = await boot.Subagents.SpawnAsync(parent, new SubagentRequest("do the thing", "thing"), CancellationToken.None);
        var child = boot.Agents.Get(result.SessionId);
        Assert.NotNull(child);
        // The child is published without agent/session-start — the agent/created hook is what reaches it.
        Assert.Equal("read", Assert.Single(boot.Tools.Schemas(child!.ScopeKey)).Name);
        Assert.Contains("Custom workspace identity for testing.", RenderedIdentity(boot, child));
    }

    [Fact]
    public async Task GlobalDefaultTools_ApplyToEveryWorkspace_UnlessOneOverrides()
    {
        await using var boot = await Boot();
        var ws = Add(boot, "project");
        var facade = new SessionFacade(boot, new UiEventBroker());
        var agent = boot.Agents.Get(facade.CreateSession(ws.Id).Id)!;
        Assert.Contains(boot.Tools.Schemas(agent.ScopeKey), s => s.Name == "bash");

        // Settings → Capabilities is the default for every workspace without its own choice,
        // and it reaches chats that are already open.
        facade.UpdateDefaultToolSelection(["read"]);
        Assert.Equal("read", Assert.Single(boot.Tools.Schemas(agent.ScopeKey)).Name);
        Assert.Contains("defaultEnabledTools", File.ReadAllText(Path.Combine(Home, "settings.json")));

        // A workspace's own selection wins over the default...
        facade.UpdateWorkspaceAgentConfig(ws.Id, null, ["read", "bash"]);
        Assert.Equal(2, boot.Tools.Schemas(agent.ScopeKey).Count);
        // ...and clearing it falls back to the global default, not to "every tool".
        facade.UpdateWorkspaceAgentConfig(ws.Id, null, null);
        Assert.Equal("read", Assert.Single(boot.Tools.Schemas(agent.ScopeKey)).Name);

        // Selecting everything stores null again: "every tool", including ones added later.
        facade.UpdateDefaultToolSelection([.. boot.Tools.Schemas().Select(s => s.Name)]);
        Assert.Null(boot.Settings.DefaultEnabledTools);
        Assert.Contains(boot.Tools.Schemas(agent.ScopeKey), s => s.Name == "edit");
    }

    [Fact]
    public async Task IdentityHeader_CarriesTodaysDate_WhateverTheBodySays()
    {
        await using var boot = await Boot();
        var ws = Add(boot, "project");
        var now = DateTime.Now;
        var today = $"Today is {now.ToString("dddd", CultureInfo.InvariantCulture)}, "
            + $"{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} in this machine's local time zone.";

        // Default workspace: the built-in body under the dated header.
        var plain = boot.Loop.Create(new SessionMeta(Cwd: ws.Root));
        Assert.Contains(today, RenderedIdentity(boot, plain));

        // A custom body replaces only what is below the header — the date line is not user-owned.
        boot.Workspaces.UpdateAgentConfig(ws.Id, "Custom body only.", null);
        var text = RenderedIdentity(boot, plain);
        Assert.Contains(today, text);
        Assert.Contains("Custom body only.", text);

        // Custom text can interpolate the same variables into its own sentences.
        boot.Workspaces.UpdateAgentConfig(ws.Id, "Freeze scope after {{date}} ({{weekday}}).", null);
        Assert.Contains(
            $"Freeze scope after {now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} "
            + $"({now.ToString("dddd", CultureInfo.InvariantCulture)}).",
            RenderedIdentity(boot, plain));
    }

    [Fact]
    public async Task DefaultInstructionBody_IsTheCharter_AndHoldsNoPlaceholders()
    {
        await using var boot = await Boot();
        var body = AgentLoopService.DefaultIdentityBody;
        Assert.Contains("Senior Software Engineering Agent", body);
        Assert.Contains("## 1. MISSION AND ACCOUNTABILITY", body);
        Assert.Contains("## 21. FINAL OPERATING DIRECTIVE", body);
        // The built-in body must stay literal: the editor prefills from it and an unchanged save
        // stores null, so nothing in it may depend on prompt variables.
        Assert.False(body.Contains("{{", StringComparison.Ordinal));
        Assert.False(body.Contains("}}", StringComparison.Ordinal));
        // It carries no product identity either — that belongs to the fixed header above it.
        Assert.False(body.Contains("Blazorly", StringComparison.Ordinal));
        // One source of truth for the editor, the placeholder hint, and the prompt itself.
        Assert.Equal(body, new SessionFacade(boot, new UiEventBroker()).DefaultSystemInstructions);
    }
}

/// <summary>Core-level seams: the identity override hook, lenient interpolation for
/// user-authored sections, and restriction lifetime in the tool runtime.</summary>
public sealed class IdentityOverrideTests
{
    [Fact]
    public async Task IdentityOverride_ReplacesDefault_AndInterpolatesLeniently()
    {
        await using var harness = TestHarness.Create();
        harness.Loop.IdentityOverride = ctx =>
            ctx.Cwd == "/special" ? "Custom identity in {{cwd}} on {{provider}}; {{bogus}} stays." : null;

        var special = harness.CreateAgent("/special");
        var text = SystemPromptService.RenderPrompt(harness.Prompt.Assemble(special, "/special"));
        Assert.Contains("Custom identity in /special on scripted; {{bogus}} stays.", text);
        Assert.DoesNotContain(AgentLoopService.DefaultIdentityBody, text);
        // The static header survives every override.
        Assert.Contains("agentic coding assistant powered by scripted (test)", text);

        var ordinary = harness.CreateAgent("/ordinary");
        var defaultText = SystemPromptService.RenderPrompt(harness.Prompt.Assemble(ordinary, "/ordinary"));
        Assert.Contains("agentic coding assistant powered by scripted (test)", defaultText);
        Assert.Contains(AgentLoopService.DefaultIdentityBody, defaultText);
    }

    [Fact]
    public async Task StrictSections_StillFailOnUnknownVariables()
    {
        await using var harness = TestHarness.Create();
        using var section = harness.Prompt.RegisterSection("strict-test", 50, _ => "boom {{nope}}");
        var ex = Assert.Throws<HarnessException>(() => harness.Prompt.Assemble(null, null));
        Assert.Equal("PROMPT_VARIABLE", ex.Code);
    }

    [Fact]
    public async Task Restrict_UndoReclaimsTheScopeLayer()
    {
        await using var harness = TestHarness.Create();
        var key = new object();
        var restriction = harness.Tools.Restrict(key, allow: new HashSet<string> { "read" });
        Assert.Contains(key, harness.Tools.ScopedKeys);
        restriction.Dispose();
        Assert.DoesNotContain(key, harness.Tools.ScopedKeys);
    }

    [Fact]
    public async Task Restrict_MutationsAreSafeAgainstConcurrentSchemaReads()
    {
        await using var harness = TestHarness.Create();
        var agent = harness.CreateAgent();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) _ = harness.Tools.Schemas(agent.ScopeKey);
        });
        for (var i = 0; i < 500; i++)
        {
            using var restriction = harness.Tools.Restrict(agent.ScopeKey, allow: new HashSet<string> { "read" });
        }
        stop.Cancel();
        await reader; // a torn restrictions list would surface here
    }
}
