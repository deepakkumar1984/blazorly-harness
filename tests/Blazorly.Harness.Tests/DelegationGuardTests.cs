using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;

namespace Blazorly.Harness.Tests;

/// <summary>
/// P0 delegation guardrails, written after session-1a184622635f24d1fc0 fanned out into
/// 32 sessions and never finished: child ids must never collide with the durable store,
/// delegation is one level deep with delegation tools hidden from children, each child
/// has a hard tool budget, and stopping a session stops its whole tree.
/// </summary>
public class DelegationGuardTests
{
    private static ToolExecutionInput Input(Agent agent, string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = $"call_{name}_{Guid.NewGuid():N}",
        Signal = CancellationToken.None,
        Agent = agent,
    };

    private static TestHarness ChildHarness()
        => TestHarness.Create(options =>
            options.SessionId is { Length: > 0 } id && id.Contains("sub")
                ? Scripted.Text("child reply")
                : Scripted.Text("lead reply"));

    [Fact]
    public async Task Spawn_IdsAreUniqueAcrossServiceInstances()
    {
        await using var harness = ChildHarness();
        var lead = harness.CreateAgent();

        // A fresh service per spawn simulates a harness restart: the old sequential
        // counter restarted at 1 and collided with ids already in the durable store.
        var first = await new SubagentService(harness.Ctx).SpawnAsync(lead,
            new SubagentRequest(Prompt: "one"), CancellationToken.None);
        var second = await new SubagentService(harness.Ctx).SpawnAsync(lead,
            new SubagentRequest(Prompt: "two"), CancellationToken.None);

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.StartsWith("session-sub-", first.SessionId);
        Assert.StartsWith("session-sub-", second.SessionId);
    }

    [Fact]
    public async Task Spawn_EmptyProviderOverride_InheritsParentRoute()
    {
        // Regression (session-1a184622635f24d1fc0, turn 9): the model emitted
        // "provider": "" / "model": "" for optional subagent_start fields, the ""
        // won over the parent's route, and the child died with NO_ADAPTER on every
        // delivery while the lead kept re-sending to it.
        await using var harness = ChildHarness();
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: "work", Provider: "", Model: "  ", Continuable: true), CancellationToken.None);

        Assert.Equal("child reply", result.Summary);
        var child = subagents.GetChild(result.SessionId)!;
        var descriptor = child.Session.Events
            .Where(e => e.Type == SessionEventTypes.SubagentDescriptor)
            .Select(SessionEventRead.SubagentDescriptorOf)
            .Single();
        Assert.Equal(lead.Options.Provider, descriptor.Provider);
        Assert.Equal(lead.Options.Model, descriptor.Model);
    }

    [Fact]
    public async Task Spawn_BrokenChild_SummarySurfacesTheFailure()
    {
        await using var harness = ChildHarness();
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var result = await subagents.SpawnAsync(lead, new SubagentRequest(
            Prompt: "work", Provider: "no-such-provider"), CancellationToken.None);

        Assert.Equal("error", result.FinishKind);
        Assert.Contains("no adapter registered for provider", result.Summary);
    }

    [Fact]
    public async Task Child_CannotDelegateFurther()
    {
        await using var harness = ChildHarness();
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var spawned = await subagents.SpawnAsync(lead, new SubagentRequest(Prompt: "work"), CancellationToken.None);
        var child = subagents.GetChild(spawned.SessionId)!;
        Assert.Equal(1, child.Session.Header.DelegationDepth);

        var ex = await Assert.ThrowsAsync<HarnessException>(() =>
            subagents.SpawnAsync(child, new SubagentRequest(Prompt: "delegate again"), CancellationToken.None));
        Assert.Equal("DELEGATION_DEPTH", ex.Code);
    }

    [Fact]
    public async Task Child_SchemasHideEveryDelegationTool()
    {
        await using var harness = ChildHarness();
        var subagents = SubagentService.Mount(harness.Ctx);
        new SubagentToolsPlugin().Apply(harness.Ctx);
        new TeamPlugin().Apply(harness.Ctx);
        new WorkflowPlugin().Apply(harness.Ctx);
        var lead = harness.CreateAgent();

        var spawned = await subagents.SpawnAsync(lead, new SubagentRequest(Prompt: "work"), CancellationToken.None);
        var child = subagents.GetChild(spawned.SessionId)!;

        var childTools = harness.Tools.Schemas(child.ScopeKey).Select(s => s.Name).ToHashSet();
        Assert.Empty(childTools.Intersect(SubagentService.DelegationToolNames));
        Assert.Contains("bash", childTools); // ordinary work tools stay available

        var leadTools = harness.Tools.Schemas(lead.ScopeKey).Select(s => s.Name).ToHashSet();
        Assert.Contains("subagent_start", leadTools);
        Assert.Contains("review", leadTools);
    }

    [Fact]
    public async Task Child_ToolBudgetStopsRunawayChildButNotTheLead()
    {
        await using var harness = ChildHarness();
        var subagents = SubagentService.Mount(harness.Ctx);
        subagents.MaxChildToolCalls = 3;
        harness.Tools.Register(new ProbeTool("probe"));
        var lead = harness.CreateAgent();

        var spawned = await subagents.SpawnAsync(lead, new SubagentRequest(Prompt: "work"), CancellationToken.None);
        var child = subagents.GetChild(spawned.SessionId)!;

        for (var i = 0; i < 3; i++)
            Assert.False((await harness.Tools.Execute(Input(child, "probe", new { value = "x" }))).IsError);
        var over = await harness.Tools.Execute(Input(child, "probe", new { value = "x" }));
        Assert.True(over.IsError);
        Assert.Contains("tool budget", string.Join(" ", over.Content.OfType<TextBlock>().Select(b => b.Text)));

        // the lead's budget is unlimited
        Assert.False((await harness.Tools.Execute(Input(lead, "probe", new { value = "x" }))).IsError);
    }

    [Fact]
    public async Task CancelTree_StopsLiveDescendants()
    {
        await using var harness = ChildHarness();
        harness.ScriptedLlm.ChunkDelayMs = 30_000; // the child's turn cannot settle on its own
        var subagents = SubagentService.Mount(harness.Ctx);
        var lead = harness.CreateAgent();

        var started = subagents.SpawnBackgroundAsync(lead, new SubagentRequest(Prompt: "slow work"));
        var child = subagents.GetChild(started.SessionId)!;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (child.Status != AgentStatus.Running && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(AgentStatus.Running, child.Status);

        var cancelled = subagents.CancelTree(lead.Id);

        Assert.Equal(2, cancelled); // lead + child
        await child.WhenIdleAsync();
        Assert.Equal(AgentStatus.Idle, child.Status);
    }
}
