using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

public class AgentLoopTests
{
    private static GenerateOptions LastOptions(IReadOnlyList<GenerateOptions> calls, int index)
        => calls[Math.Min(index, calls.Count - 1)];

    [Fact]
    public async Task RuntimeContextSnapshot_IsSilent_AndDoesNotRepeatWithinTheMinute()
    {
        // The time section re-renders every minute; chatty models used to answer each refresh
        // with "The runtime context has been refreshed…". The snapshot must carry a do-not-
        // acknowledge preamble, and must not repeat while its content is unchanged.
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        new Blazorly.Harness.Core.Context.TimeContextPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();

        agent.Followup(Message.CreateUserText("hi"));
        await agent.WhenIdleAsync();
        agent.Followup(Message.CreateUserText("again"));
        await agent.WhenIdleAsync();

        var snapshots = agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.UserMessage)
            .Select(SessionEventRead.MessageOf)
            .Where(m => m.Source.Kind == "plugin")
            .ToList();
        var texts = snapshots.Select(x => x.FlattenText()).ToList();
        var text = Assert.Single(texts); // identical content across turns — injected once, not per turn
        Assert.Contains("do not acknowledge", text);
        Assert.Contains("Current time:", text);
    }

    [Fact]
    public async Task SimpleTurn_StreamsChunksAndAppendsDurableHistory()
    {
        var calls = new List<GenerateOptions>();
        await using var harness = TestHarness.Create(options =>
        {
            calls.Add(options);
            return Scripted.Text("Hello there!");
        });
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("hi"));
        await agent.WhenIdleAsync();

        var events = agent.Session.Events;
        Assert.Contains(events, e => e.Type == SessionEventTypes.TurnStart);
        Assert.Contains(events, e => e.Type == SessionEventTypes.StepStart);
        Assert.Contains(events, e => e.Type == SessionEventTypes.AssistantChunk);
        var assistant = events.First(e => e.Type == SessionEventTypes.AssistantMessage);
        var payload = SessionEventRead.AssistantMessageOf(assistant);
        Assert.Equal("Hello there!", payload.Message.FlattenText());
        Assert.Contains(events, e => e.Type == SessionEventTypes.TurnEnd);
        Assert.IsType<TurnEndReason.Completed>(SessionEventRead.TurnEndReasonOf(events.Last(e => e.Type == SessionEventTypes.TurnEnd)));

        // model-visible ⟺ logged: the request history derives from the log alone
        var derived = agent.Session.DeriveMessages();
        Assert.Equal(2, derived.Count);
        Assert.Equal("hi", derived[0].FlattenText());
        Assert.Equal("Hello there!", derived[1].FlattenText());
        Assert.Equal("hi", LastOptions(calls, 0).Messages[0].FlattenText());
    }

    [Fact]
    public async Task ToolCallRoundTrip_AppendsCallResultPairAndContinues()
    {
        var calls = new List<GenerateOptions>();
        await using var harness = TestHarness.Create(options =>
        {
            calls.Add(options);
            return calls.Count == 1
                ? Scripted.ToolCall("bash", new { command = "echo roundtrip", description = "Echo test" })
                : Scripted.Text("done: " + (calls.Count > 1 && calls[1].Messages.Any(m => m.Content.OfType<ToolResultBlock>().Any())
                    ? calls[1].Messages.OfType<Message>().SelectMany(m => m.Content).OfType<ToolResultBlock>().First().Content.OfType<TextBlock>().First().Text.Trim()
                    : "missing"));
        });
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("run echo"));
        await agent.WhenIdleAsync();

        var events = agent.Session.Events;
        var call = events.Single(e => e.Type == SessionEventTypes.ToolCall);
        Assert.Equal("bash", SessionEventRead.ToolCallOf(call).Name);
        var result = events.Single(e => e.Type == SessionEventTypes.ToolResult);
        var message = SessionEventRead.ToolResultOf(result).Message;
        var toolBlock = Assert.IsType<ToolResultBlock>(Assert.Single(message.Content));
        var innerText = Assert.IsType<TextBlock>(Assert.Single(toolBlock.Content));
        Assert.Contains("roundtrip", innerText.Text);
        Assert.Equal(1, calls.Count(e => e.Messages.Count > 1));

        // the tool result reached the second request as a user-role tool-result message
        var secondCall = calls[1];
        var toolResult = secondCall.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.NotNull(toolResult);
    }

    [Fact]
    public async Task FollowupQueue_ProducesTwoTurns()
    {
        var turns = new List<int>();
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        var agent = harness.CreateAgent();
        agent.Session.Subscribe(e =>
        {
            if (e.Type == SessionEventTypes.TurnStart) turns.Add(SessionEventRead.TurnOf(e));
        });
        agent.Followup(Message.CreateUserText("first"));
        agent.Followup(Message.CreateUserText("second"));
        await agent.WhenIdleAsync();

        Assert.Equal([1, 2], turns);
        var userMessages = agent.Session.Events.Where(e => e.Type == SessionEventTypes.UserMessage).ToList();
        Assert.Equal(2, userMessages.Count);
    }

    [Fact]
    public async Task Steering_MidTurnEntersNextStep()
    {
        var probe = new ProbeTool { SafeClassifier = _ => true };
        var calls = new List<GenerateOptions>();
        await using var harness = TestHarness.Create(options =>
        {
            calls.Add(options);
            if (calls.Count == 1) return Scripted.ToolCall("probe", new { value = "slow", delayMs = 300 });
            var sawSteer = options.Messages.Any(m => m.FlattenText().Contains("STEER:"));
            return Scripted.Text(sawSteer ? "steered" : "not-steered");
        });
        harness.Tools.Register(probe);
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("begin"));
        // steer while the slow tool body is still running (the turn is definitely open)
        while (probe.CallLog.Count == 0) await Task.Delay(10);
        agent.Steer(Message.CreateUserText("STEER: go left"));
        await agent.WhenIdleAsync();

        var steered = agent.Session.Events.Any(e => e.Type == SessionEventTypes.UserMessage
            && SessionEventRead.MessageOf(e).FlattenText().Contains("STEER:"));
        Assert.True(steered);
        var finalText = agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.AssistantMessage)
            .Select(e => SessionEventRead.AssistantMessageOf(e).Message.FlattenText())
            .Last();
        Assert.Equal("steered", finalText);
    }

    [Fact]
    public async Task Cancel_MidStreamAbortsTurn()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("never finishes"));
        harness.ScriptedLlm.ChunkDelayMs = 5_000;
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("start"));
        await Task.Delay(150); // the stream is now in-flight and slow
        agent.Cancel(AgentCancelCause.User());
        await agent.WhenIdleAsync();

        var turnEnd = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd);
        var reason = SessionEventRead.TurnEndReasonOf(turnEnd);
        var aborted = Assert.IsType<TurnEndReason.Aborted>(reason);
        Assert.Equal(TurnEndAbortedCauses.User, aborted.Cause);
    }

    [Fact]
    public async Task RequestError_RetriesThenSucceeds()
    {
        var attempts = 0;
        await using var harness = TestHarness.Create(_ =>
        {
            attempts++;
            return attempts <= 2
                ? Scripted.Error(LlmErrorCodes.Server, "boom")
                : Scripted.Text("recovered");
        });
        var agent = harness.CreateAgent();
        agent.RetryLimit = 3;
        agent.Followup(Message.CreateUserText("retry me"));
        await agent.WhenIdleAsync();

        Assert.True(attempts >= 3);
        var last = agent.Session.Events.Where(e => e.Type == SessionEventTypes.AssistantMessage).Last();
        Assert.Equal("recovered", SessionEventRead.AssistantMessageOf(last).Message.FlattenText());
        Assert.Equal(AgentStatus.Idle, agent.Status);
    }

    [Fact]
    public async Task RequestError_NonRetriableFailsTheTurn()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Error(LlmErrorCodes.InvalidCredential, "bad key"));
        var agent = harness.CreateAgent();
        agent.RetryLimit = 3;
        agent.Followup(Message.CreateUserText("fail fast"));
        await agent.WhenIdleAsync();

        var turnEnd = agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd);
        var error = Assert.IsType<TurnEndReason.Error>(SessionEventRead.TurnEndReasonOf(turnEnd));
        Assert.Equal(LlmErrorCodes.InvalidCredential, error.Code);
    }

    [Fact]
    public async Task TurnStoppingListener_CanVetoCloseBySteering()
    {
        var steered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = TestHarness.Create(options =>
            options.Messages.Any(m => m.FlattenText().Contains("VETO")) ? Scripted.Text("vetoed handled") : Scripted.Text("first pass"));
        var agent = harness.CreateAgent();
        harness.Ctx.Events.On<TurnStoppingEvent>("agent/turn-stopping", async (e, _) =>
        {
            if (!steered.Task.IsCompleted)
            {
                e.Agent.Steer(Message.CreateUserText("VETO: one more thing"));
                steered.TrySetResult();
            }
        });
        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var steps = agent.Session.Events.Count(e => e.Type == SessionEventTypes.StepStart);
        Assert.True(steps >= 2);
        var final = agent.Session.Events.Where(e => e.Type == SessionEventTypes.AssistantMessage).Last();
        Assert.Equal("vetoed handled", SessionEventRead.AssistantMessageOf(final).Message.FlattenText());
    }

    [Fact]
    public async Task PreStepReject_ClosesTurnWithNoStep()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("never"));
        var agent = harness.CreateAgent();
        harness.Ctx.Events.OnWaterfall<PreStepEvent, List<Message>, PreStepDecision>(
            "agent/pre-step", (_, _, _, _) => Task.FromResult(PreStepDecision.Reject()));
        agent.Followup(Message.CreateUserText("blocked"));
        await agent.WhenIdleAsync();

        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.StepStart);
        var turnEnd = agent.Session.Events.Single(e => e.Type == SessionEventTypes.TurnEnd);
        Assert.IsType<TurnEndReason.Blocked>(SessionEventRead.TurnEndReasonOf(turnEnd));
    }

    [Fact]
    public async Task SystemPrompt_IncludesIdentitySectionAndToolSchemas()
    {
        GenerateOptions? seen = null;
        await using var harness = TestHarness.Create(options => { seen = options; return Scripted.Text("ok"); });
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("hi"));
        await agent.WhenIdleAsync();

        Assert.NotNull(seen);
        Assert.Contains("Blazorly Harness", seen!.System);
        Assert.Contains("agentic coding assistant", seen!.System);
        Assert.Contains(seen!.Tools!, t => t.Name == "bash");
        Assert.Contains(seen!.Tools!, t => t.Name == "read");
        Assert.Contains(seen!.Tools!, t => t.Name == "edit");
        Assert.Contains(seen!.Tools!, t => t.Name == "todo_write");
    }
}

public class ToolSchedulerTests
{
    [Fact]
    public async Task ParallelCalls_OverlapBodies_CommitInModelOrder()
    {
        var probe = new ProbeTool { SafeClassifier = _ => true };
        var committed = new List<string>();
        var calls = 0;
        await using var harness = TestHarness.Create(_ =>
        {
            calls++;
            return calls == 1
                ? Scripted.ToolCalls(
                    ("probe", new { value = "a", delayMs = 120 }),
                    ("probe", new { value = "b", delayMs = 10 }),
                    ("probe", new { value = "c", delayMs = 60 }))
                : Scripted.Text("done");
        });
        harness.Tools.Register(probe);
        harness.Ctx.Events.On<ToolPostExecute>("tools/result", (e, _) =>
        {
            if (e.Execution.Input.Name == "probe" && !e.Result.IsError)
            {
                lock (committed) committed.Add(e.Execution.Input.Arguments.GetProperty("value").GetString()!);
            }
            return Task.CompletedTask;
        });

        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("parallel"));
        await agent.WhenIdleAsync();

        // bodies overlap (b finishes first), but results commit strictly in model order a, b, c
        Assert.Equal(3, probe.CallLog.Count);
        Assert.Equal(["a", "b", "c"], committed);
        var toolResults = agent.Session.Events.Where(e => e.Type == SessionEventTypes.ToolResult).ToList();
        Assert.Equal(3, toolResults.Count);
    }

    [Fact]
    public async Task ExclusiveCall_IsABarrier()
    {
        var probe = new ProbeTool { SafeClassifier = args => args.Value != "exclusive" };
        var started = new List<string>();
        var calls = 0;
        await using var harness = TestHarness.Create(_ =>
        {
            calls++;
            return calls == 1
                ? Scripted.ToolCalls(
                    ("probe", new { value = "fast" }),
                    ("probe", new { value = "exclusive" }),
                    ("probe", new { value = "after" }))
                : Scripted.Text("done");
        });
        harness.Tools.Register(probe);
        probe.BodyStarted += v => { lock (started) started.Add(v); };

        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText("barrier"));
        await agent.WhenIdleAsync();

        // the exclusive call must not start before the parallel group ahead of it commits
        var exclusiveIndex = started.IndexOf("exclusive");
        var fastIndex = started.IndexOf("fast");
        Assert.True(fastIndex >= 0 && exclusiveIndex >= 0);
        Assert.True(exclusiveIndex > fastIndex);
        Assert.Equal(3, started.Count);
    }
}

/// <summary>Provider 400s name our own fields back at the user with the way out.</summary>
public class InvalidRequestHintTests
{
    [Fact]
    public void EffortNamedByProvider_PointsAtEffortReset()
    {
        var text = AgentDriver.WithInvalidRequestHint(
            "provider rejected request (400: reasoning_effort 'max' is not supported)",
            new AgentOptions("p", "m", 1024, "max"));
        Assert.Contains("reasoning effort 'max'", text);
        Assert.Contains("/effort default", text);
    }

    [Fact]
    public void MaxTokensNamedByProvider_PointsAtSettings()
    {
        var text = AgentDriver.WithInvalidRequestHint(
            "provider rejected request (400: max_tokens 65536 exceeds the limit)",
            new AgentOptions("p", "m", 65536));
        Assert.Contains("max_tokens 65536", text);
        Assert.Contains("Settings", text);
    }

    [Fact]
    public void UnnamedCause_ListsBothSuspectsWithValues()
    {
        var text = AgentDriver.WithInvalidRequestHint(
            "provider rejected request (400: Invalid request parameters)",
            new AgentOptions("p", "m", 65536, "max"));
        Assert.Contains("reasoning effort 'max'", text);
        Assert.Contains("max_tokens 65536", text);
        Assert.Contains("/effort default", text);
    }

    [Fact]
    public void NothingOursSent_MessagePassesThrough()
    {
        const string message = "provider rejected request (400: Invalid request parameters)";
        Assert.Equal(message, AgentDriver.WithInvalidRequestHint(message, new AgentOptions("p", "m")));
    }
}
