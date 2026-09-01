using System.Text.Json;
using Blazorly.Harness.Cli;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Compaction;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>Boots the real composition in an isolated BLAZORLY_HOME (env restored per test).</summary>
public abstract class BootstrapperTestBase : IDisposable
{
    protected readonly string Home = Path.Combine(Path.GetTempPath(), "blazorly-cli-" + Guid.NewGuid().ToString("N")[..8]);

    protected BootstrapperTestBase()
    {
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("BLAZORLY_HOME", Home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BLAZORLY_HOME", null);
        try { Directory.Delete(Home, recursive: true); } catch (IOException) { }
    }
}

public class HeadlessRunnerTests : BootstrapperTestBase
{
    private string Workspace() => Path.Combine(Path.GetTempPath(), "blazorly-cli-ws-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Run_CompletesWithExitZero_PersistsSession_StreamsJsonEnvelope()
    {
        var workspace = Workspace();
        var output = new StringWriter();
        var result = await HeadlessRunner.RunAsync(new HeadlessOptions
        {
            Job = "run the demo task",
            WorkspacePath = workspace,
            Json = true,
            Out = output,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("completed", result.Finish);
        Assert.NotNull(result.SessionId);
        var envelope = JsonDocument.Parse(output.ToString()).RootElement;
        Assert.Equal(result.SessionId, envelope.GetProperty("sessionId").GetString());
        Assert.Contains("demo run completed", envelope.GetProperty("response").GetString());
        Assert.True(Directory.Exists(Path.Combine(Home, "sessions")));
        Assert.True(Directory.Exists(Path.Combine(Home, "spills"))); // the full composition booted
    }

    [Fact]
    public async Task Resume_ContinuesTheSameSession()
    {
        var workspace = Workspace();
        var first = await HeadlessRunner.RunAsync(new HeadlessOptions
        {
            Job = "run the demo task",
            WorkspacePath = workspace,
            Quiet = true,
        });
        Assert.Equal(0, first.ExitCode);

        var second = await HeadlessRunner.RunAsync(new HeadlessOptions
        {
            Job = "and once more",
            WorkspacePath = workspace,
            ResumeSessionId = first.SessionId,
            Quiet = true,
        });
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Contains("demo run completed", second.Response);
    }

    [Fact]
    public async Task Timeout_AbortsTheRun_ExitThree()
    {
        var result = await HeadlessRunner.RunAsync(new HeadlessOptions
        {
            Job = "run the demo task",
            WorkspacePath = Workspace(),
            ChunkDelayMs = 400, // pace the scripted stream so the run outlives the timeout
            TimeoutSeconds = 1,
            Quiet = true,
        });
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("aborted", result.Finish);
    }

    [Fact]
    public async Task ProviderFailure_TurnEndsInError_ExitTwo()
    {
        var result = await HeadlessRunner.RunAsync(new HeadlessOptions
        {
            Job = "anything",
            WorkspacePath = Workspace(),
            Provider = "no-such-provider",
            Model = "no-such-model",
            Quiet = true,
        });
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("error", result.Finish);
    }

    [Fact]
    public async Task SessionsCommand_ListsPersistedSessions()
    {
        var workspace = Workspace();
        var run = await HeadlessRunner.RunAsync(new HeadlessOptions
        {
            Job = "run the demo task",
            WorkspacePath = workspace,
            Quiet = true,
        });
        Assert.Equal(0, run.ExitCode);

        var output = new StringWriter();
        var exit = await HeadlessRunner.ListSessionsAsync(new HeadlessOptions { WorkspacePath = workspace, Out = output });
        Assert.Equal(0, exit);
        Assert.Contains(run.SessionId!, output.ToString());
    }
}

public class CompactionPrunerTests
{
    private static Agent AgentWithBigToolResult(TestHarness harness, string marker, int bulkChars)
    {
        var agent = harness.CreateAgent();
        agent.Session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        agent.Session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        var call = agent.Session.Append(SessionEventTypes.ToolCall,
            new SessionPayloads.ToolCall(1, 1, "call_1", "bash", "{}"));
        var message = Llm.Message.CreateToolResult("call_1",
            [new TextBlock($"{marker}-" + new string('x', bulkChars))]);
        agent.Session.Append(SessionEventTypes.ToolResult,
            new SessionPayloads.ToolResult(1, 1, message),
            new Session.AppendOptions(SourceEventSeqs: [call.Seq], SurfaceOp: new SurfaceOp.Append()));
        agent.Session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(1, 1));
        agent.Session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
        return agent;
    }

    private static void Fill(Agent agent, int messages, int chars)
    {
        for (var i = 0; i < messages; i++)
        {
            agent.Session.Append(SessionEventTypes.UserMessage, Message.CreateUserText(new string('y', chars) + $" #{i}"),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
        }
    }

    [Fact]
    public async Task Prune_ReplacesOldOversizedToolResultInPlace()
    {
        await using var harness = TestHarness.Create();
        var compaction = CompactionService.Mount(harness.Ctx, new CompactionOptions { PrunerChars = 500 });
        var agent = AgentWithBigToolResult(harness, "BULK-MARKER", 5_000);
        Fill(agent, 5, 100);

        var prunedTokens = await compaction.PruneAsync(agent);

        Assert.True(prunedTokens > 1_000);
        // The replacement node is a user message carrying a rewritten tool-result block.
        var replaced = agent.Session.Events.Last(e => e.Type == SessionEventTypes.UserMessage && e.SurfaceOp is SurfaceOp.Replace);
        var replacedBlock = Assert.IsType<ToolResultBlock>(SessionEventRead.MessageOf(replaced).Content.Single());
        Assert.Equal("call_1", replacedBlock.ToolCallId);
        Assert.Contains("[tool output pruned: 5012 chars from 'bash'", replacedBlock.Content.OfType<TextBlock>().Single().Text);
        Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.CompactionPrune
            && e.Data.GetProperty("prunedChars").GetInt64() == 5_012);
        // the newest quarter stays verbatim
        Assert.Contains("y", string.Join("\n", agent.Session.DeriveMessages().Select(m => m.FlattenText())));
    }

    [Fact]
    public async Task PruningAlone_CanSatisfyTheTrigger_WithoutAnyModelCall()
    {
        var llmCalls = 0;
        await using var harness = TestHarness.Create(options =>
        {
            llmCalls++;
            return ReplayScript.Text("should not be needed");
        });
        // trigger ~2211 tokens: header (~1364) + bulk (~1250) + fill crosses it; pruning the bulk clears it.
        var compaction = CompactionService.Mount(harness.Ctx, new CompactionOptions
        {
            ContextWindowTokens = 8_192,
            Threshold = 0.27,
            KeepRatio = 0.05,
            PrunerChars = 500,
        });
        var agent = AgentWithBigToolResult(harness, "BULK-MARKER", 5_000);
        Fill(agent, 4, 100);
        Assert.True(compaction.ShouldCompact(agent));

        await compaction.PruneAsync(agent);

        Assert.False(compaction.ShouldCompact(agent)); // pure-prune path: no summary needed
        Assert.Equal(0, llmCalls);                      // the model was never called
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.CompactionSummary);
    }

    [Fact]
    public async Task Compact_SummarizesThePrunedInput_NotTheBulk()
    {
        string? summaryInput = null;
        await using var harness = TestHarness.Create(options =>
        {
            if (options.Purpose == "compaction")
            {
                summaryInput = string.Join("\n", options.Messages.Select(m => m.FlattenText()));
                return ReplayScript.Text("SUMMARY: pruned and summarized.");
            }
            return ReplayScript.Text("ok");
        });
        var compaction = CompactionService.Mount(harness.Ctx, new CompactionOptions
        {
            ContextWindowTokens = 8_192,
            Threshold = 0.27,
            KeepRatio = 0.05,
            PrunerChars = 500,
        });
        var agent = AgentWithBigToolResult(harness, "BULK-MARKER", 5_000);
        Fill(agent, 8, 300); // large enough that pruning alone cannot clear the trigger

        Assert.True(compaction.ShouldCompact(agent));
        await compaction.CompactAsync(agent);

        Assert.NotNull(summaryInput);
        Assert.DoesNotContain("BULK-MARKER", summaryInput); // the summary never read the bulk
        Assert.Contains(agent.Session.Events, e => e.Type == SessionEventTypes.CompactionSummary);
        var derivedText = string.Join("\n", agent.Session.DeriveMessages().Select(m => m.FlattenText()));
        Assert.Contains("SUMMARY: pruned and summarized.", derivedText);
    }

    [Fact]
    public async Task Window_ResolvesFromTheModelCatalog_OverTheGlobalOption()
    {
        await using var harness = TestHarness.Create();
        var compaction = CompactionService.Mount(harness.Ctx, new CompactionOptions { ContextWindowTokens = 100_000, Threshold = 0.5 });
        harness.Llm.RegisterAdapter(new CatalogAdapter("tiny"));

        // Agent selection defaults override per-call options, so switch the default route.
        harness.Loop.DefaultSelection = new LlmCallConfig { Provider = "tiny", Model = "tiny-model" };
        var tinyAgent = harness.CreateAgent();
        Assert.Equal(1_000, compaction.ResolveWindow(tinyAgent));
        Assert.Equal(500, compaction.TriggerFor(tinyAgent));

        harness.Loop.DefaultSelection = new LlmCallConfig { Provider = "replay", Model = "demo" };
        var defaultAgent = harness.CreateAgent(); // replay/demo has no catalog window
        Assert.Equal(100_000, compaction.ResolveWindow(defaultAgent));
    }

    private sealed class CatalogAdapter(string provider) : LlmAdapter
    {
        public override string Provider { get; } = provider;
        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken ct = default)
            => throw new LlmException(LlmErrorCodes.NoAdapter, "catalog stub never streams");
        public override IReadOnlyList<LlmModelInfo> ListModels() =>
            [new LlmModelInfo(Provider, "tiny-model", "Tiny", ContextWindowTokens: 1_000)];
    }
}

public class CompactCommandTests : BootstrapperTestBase
{
    [Fact]
    public async Task CompactCommand_GuardsThenCompacts()
    {
        var bootstrapper = new HarnessBootstrapper();
        await bootstrapper.StartAsync(CancellationToken.None);
        try
        {
            var facade = new SessionFacade(bootstrapper, new UiEventBroker());
            var session = facade.CreateSession();

            // Guard: an empty session has nothing to compact.
            var empty = facade.TryCommand(session.Id, "/compact");
            Assert.NotNull(empty);
            Assert.False(empty!.Ok);
            Assert.Contains("nothing to compact", empty.Text);

            facade.Prompt(session.Id, "run the demo task", "queue");
            for (var i = 0; i < 100 && session.Events.All(e => e.Type != SessionEventTypes.TurnEnd); i++)
            {
                await Task.Delay(100);
            }

            var outcome = facade.TryCommand(session.Id, "/compact");
            Assert.NotNull(outcome);
            Assert.True(outcome!.Ok, outcome.Text);
            Assert.Contains("compaction started", outcome.Text);

            for (var i = 0; i < 100 && session.Events.All(e => e.Type != SessionEventTypes.CompactionEnd); i++)
            {
                await Task.Delay(100);
            }
            Assert.Contains(session.Events, e => e.Type == SessionEventTypes.CompactionEnd);
        }
        finally
        {
            await bootstrapper.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompactCommand_IsRejectedWhileRunning()
    {
        var bootstrapper = new HarnessBootstrapper();
        await bootstrapper.StartAsync(CancellationToken.None);
        try
        {
            var facade = new SessionFacade(bootstrapper, new UiEventBroker());
            var session = facade.CreateSession();
            facade.Prompt(session.Id, "run the demo task", "queue");
            for (var i = 0; i < 100 && session.Events.All(e => e.Type != SessionEventTypes.StepStart); i++)
            {
                await Task.Delay(100);
            }

            var outcome = facade.TryCommand(session.Id, "/compact");
            Assert.NotNull(outcome);
            Assert.False(outcome!.Ok);
            Assert.Contains("busy", outcome.Text);

            for (var i = 0; i < 100 && session.Events.All(e => e.Type != SessionEventTypes.TurnEnd); i++)
            {
                await Task.Delay(100);
            }
        }
        finally
        {
            await bootstrapper.DisposeAsync();
        }
    }
}
