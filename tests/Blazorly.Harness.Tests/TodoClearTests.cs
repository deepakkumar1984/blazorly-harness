using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Task-list clearing: the panel's Clear button appends a log-only reset, and the agent can
/// clear via an empty todo_write. History stays in the log; only the latest-wins fold resets.
/// </summary>
public class TodoClearTests
{
    [Fact]
    public void TodoClear_ResetsLatestTodosUntilNextSnapshot()
    {
        var session = new Session(new SessionHeader { Id = "session-todos", CreatedAt = 1, Cwd = "/tmp" });
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.TodoWrite, new SessionPayloads.TodoWrite(
            [new TodoItem("first", TodoItem.Pending), new TodoItem("second", TodoItem.Completed)]));
        Assert.Equal(2, session.LatestTodos()!.Count);

        // The Clear button appends outside any turn/step: no validation may reject it.
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
        session.Append(SessionEventTypes.TodoClear, new { cleared = true });
        Assert.Empty(session.LatestTodos()!);

        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(2));
        session.Append(SessionEventTypes.TodoWrite, new SessionPayloads.TodoWrite(
            [new TodoItem("fresh", TodoItem.InProgress)]));
        Assert.Equal("fresh", Assert.Single(session.LatestTodos()!).Content);
    }

    [Fact]
    public async Task TodoWriteTool_AcceptsEmptyListToClear()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("unused"));
        var agent = harness.CreateAgent();
        agent.Session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        agent.Session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(1, 1));
        agent.Session.Append(SessionEventTypes.TodoWrite, new SessionPayloads.TodoWrite(
            [new TodoItem("stale", TodoItem.Completed)]));

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "todo_write",
            Arguments = JsonSerializer.SerializeToElement(new { todos = Array.Empty<object>() }),
            CallId = "call_todo_clear",
            Signal = CancellationToken.None,
            Agent = agent,
        });

        Assert.False(result.IsError);
        Assert.Empty(agent.Session.LatestTodos()!);
        Assert.Contains("Cleared", result.Content.OfType<TextBlock>().Single().Text);

        agent.Session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(1, 1));
        agent.Session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
    }

    [Fact]
    public void ConversationFold_ClearHidesPanel()
    {
        var harness = TestHarness.Create();
        var session = new Session(new SessionHeader { Id = "session-todo-fold", CreatedAt = 1, Cwd = "/tmp" });
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(1));
        session.Append(SessionEventTypes.TodoWrite, new SessionPayloads.TodoWrite(
            [new TodoItem("work", TodoItem.InProgress)]));
        session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(1, new TurnEndReason.Completed()));
        session.Append(SessionEventTypes.TodoClear, new { cleared = true });

        var snapshot = new ConversationAssembler(harness.Tools).Fold(session, agent: null);
        Assert.Empty(snapshot.Todos);
    }
}
