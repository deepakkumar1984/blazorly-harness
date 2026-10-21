using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Persistence;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Continuable subagents and cold resume (dsh packages/subagent continuation parity): the
/// log-only descriptor, the NOT_RESUMABLE contract, lineage authorization, in-memory and
/// cross-store resume, and team delivery resilience across a lost registry.
/// </summary>
public class SubagentResumeTests
{
    private static string TempRoot(string prefix) => Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);

    private static TestHarness Harness(string? persistenceRoot = null)
        => TestHarness.Create(
            options =>
            {
                var text = options.Messages.SelectMany(m => m.Content).OfType<TextBlock>().LastOrDefault()?.Text ?? "";
                if (text.Contains("FOLLOWUP")) return Scripted.Text("FOLLOWUP-OK");
                if (text.Contains("Task:")) return Scripted.Text("TASK-DONE");
                return Scripted.Text("PARENT-OK");
            },
            persistence: persistenceRoot is null ? null : new JsonlSessionPersistence(persistenceRoot));

    [Fact]
    public async Task ContinuableSpawn_AppendsLogOnlyDescriptor()
    {
        await using var harness = Harness();
        var subagents = SubagentService.Mount(harness.Ctx);
        var parent = harness.CreateAgent();

        var result = await subagents.SpawnAsync(parent, new SubagentRequest("do it", Continuable: true), CancellationToken.None);

        var child = harness.Sessions.Get(result.SessionId)!;
        var descriptor = child.Events.Single(e => e.Type == SessionEventTypes.SubagentDescriptor);
        Assert.True(descriptor.Ignorable == true, "the descriptor is a plugin event readers may skip");
        Assert.Null(descriptor.SurfaceOp);
        var payload = SessionEventRead.SubagentDescriptorOf(descriptor);
        Assert.Equal(SessionPayloads.SubagentModeContinuable, payload.Mode);
        Assert.Equal("scripted", payload.Provider);
        // log-only: exactly the task message and the reply project into model history
        Assert.Equal(2, child.DeriveMessages().Count);
    }

    [Fact]
    public async Task OneShotChild_RefusesColdResume()
    {
        await using var harness = Harness();
        var subagents = SubagentService.Mount(harness.Ctx);
        var parent = harness.CreateAgent();
        var result = await subagents.SpawnAsync(parent, new SubagentRequest("do it"), CancellationToken.None);

        var fresh = new SubagentService(harness.Ctx); // registry lost, as after a restart
        var ex = await Assert.ThrowsAsync<HarnessException>(
            () => fresh.ContinueAsync(parent, result.SessionId, "FOLLOWUP", CancellationToken.None));
        Assert.Equal("SUBAGENT_NOT_RESUMABLE", ex.Code);
        Assert.Null(fresh.GetChild(result.SessionId));
    }

    [Fact]
    public async Task ColdResume_AuthorizesOnlyTheDirectParent_ThenContinues()
    {
        await using var harness = Harness();
        var subagents = SubagentService.Mount(harness.Ctx);
        var parent = harness.CreateAgent();
        var result = await subagents.SpawnAsync(parent, new SubagentRequest("do it", Continuable: true), CancellationToken.None);

        var fresh = new SubagentService(harness.Ctx);
        var other = harness.CreateAgent();
        var foreign = await Assert.ThrowsAsync<HarnessException>(
            () => fresh.ContinueAsync(other, result.SessionId, "FOLLOWUP", CancellationToken.None));
        Assert.Equal("SUBAGENT_NOT_RESUMABLE", foreign.Code);
        Assert.Null(fresh.GetChild(result.SessionId));

        var continued = await fresh.ContinueAsync(parent, result.SessionId, "FOLLOWUP", CancellationToken.None);
        Assert.Equal("FOLLOWUP-OK", continued.Summary);
        Assert.NotNull(fresh.GetChild(result.SessionId));
        var child = fresh.GetChild(result.SessionId)!;
        Assert.Equal(4, child.Session.DeriveMessages().Count); // two full turns
    }

    [Fact]
    public async Task ColdResume_WorksAcrossStores_FromPersistedLog()
    {
        var root = TempRoot("blazorly-subresume-");
        try
        {
            string parentId;
            string childId;
            await using (var harnessA = Harness(root))
            {
                var subagents = SubagentService.Mount(harnessA.Ctx);
                var parent = harnessA.CreateAgent();
                var result = await subagents.SpawnAsync(parent, new SubagentRequest("do it", Continuable: true), CancellationToken.None);
                parentId = parent.Id;
                childId = result.SessionId;
                await harnessA.Sessions.Persistence!.FlushAllAsync();
            }

            await using var harnessB = Harness(root);
            var probe = new SubagentService(harnessB.Ctx);
            var persisted = await probe.ChildrenOfAsync(parentId);
            Assert.Contains(persisted, h => h.Id == childId);

            var subagentsB = new SubagentService(harnessB.Ctx);
            Assert.Empty(subagentsB.ChildrenOf(parentId)); // live registry is gone

            var resumedParent = await harnessB.Loop.ResumeAsync(parentId);
            var continued = await subagentsB.ContinueAsync(resumedParent, childId, "FOLLOWUP", CancellationToken.None);
            Assert.Equal("FOLLOWUP-OK", continued.Summary);
            Assert.Equal(parentId, subagentsB.GetChild(childId)!.Session.Header.ParentSession);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TeamDelivery_ColdResumesTeammate_AndReinstallsReportTool()
    {
        var root = TempRoot("blazorly-teamresume-");
        try
        {
            await using var harness = Harness(root);
            var lead = harness.CreateAgent();
            var team = new TeamService(harness.Ctx, SubagentService.Mount(harness.Ctx));
            var teammate = await team.SpawnTeammateAsync(lead, "worker", CancellationToken.None);
            Assert.Contains(harness.Sessions.Get(teammate.SessionId)!.Events,
                e => e.Type == SessionEventTypes.SubagentDescriptor);

            // fresh service instances: the live registry is lost, as after a restart
            var subagents = new SubagentService(harness.Ctx);
            var freshTeam = new TeamService(harness.Ctx, subagents);
            var send = await freshTeam.SendAsync(lead, teammate.SessionId, "FOLLOWUP", CancellationToken.None);
            Assert.Contains("FOLLOWUP-OK", send.Reply);

            var resumed = subagents.GetChild(teammate.SessionId);
            Assert.NotNull(resumed);
            var schemas = harness.Tools.Schemas(resumed!.ScopeKey);
            Assert.Contains(schemas, s => s.Name == "report");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task LiveDelivery_RemainsFifoPerTurn()
    {
        await using var harness = Harness();
        var subagents = SubagentService.Mount(harness.Ctx);
        var parent = harness.CreateAgent();
        var first = await subagents.SpawnAsync(parent, new SubagentRequest("do it", Continuable: true), CancellationToken.None);

        var second = await subagents.ContinueAsync(parent, first.SessionId, "FOLLOWUP one", CancellationToken.None);
        var third = await subagents.ContinueAsync(parent, first.SessionId, "FOLLOWUP two", CancellationToken.None);

        Assert.Equal("FOLLOWUP-OK", second.Summary);
        Assert.Equal("FOLLOWUP-OK", third.Summary);
        var child = subagents.GetChild(first.SessionId)!;
        var userTurns = child.Session.Events.Count(e => e.Type == SessionEventTypes.UserMessage);
        var turnEnds = child.Session.Events.Count(e => e.Type == SessionEventTypes.TurnEnd);
        Assert.Equal(3, userTurns);
        Assert.Equal(3, turnEnds);
    }
}
