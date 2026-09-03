using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Tools;

namespace Blazorly.Harness.Tests;

file static class TestInput
{
    public static ToolExecutionInput For(Agent agent, string name, object args) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = Ids.NewCallId(),
        Signal = CancellationToken.None,
        Agent = agent,
    };

    internal static JsonElement V(this ToolExecutionResult result) => result.Value ?? default;
}

public class LspTests
{
    private static string FakeLspPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(Path.Combine(dir.FullName, "tests", "Blazorly.Harness.Tests", "fake-lsp.py")))
        {
            dir = dir.Parent!;
        }
        return Path.Combine(dir!.FullName, "tests", "Blazorly.Harness.Tests", "fake-lsp.py");
    }

    private static bool PythonAvailable()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "python3",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            return process is not null && process.WaitForExit(5_000);
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task Definition_AgainstFakeServer_ReturnsOneBasedLocation()
    {
        if (!PythonAvailable()) return;
        await using var service = new LspService(["python3", FakeLspPath()], "/tmp");
        var locations = await service.Definition("/tmp/anything.cs", 3, 5);

        var location = Assert.Single(locations);
        Assert.Equal("/tmp/x.cs", location.File);
        Assert.Equal(10, location.Line); // the server answers 0-based line 9
        Assert.Equal("", location.Text);
    }

    [Fact]
    public async Task Tool_WithoutServer_IsErrorWithLspUnavailable()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        new LspPlugin([]).Apply(harness.Ctx); // explicitly not configured
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(TestInput.For(agent, "lsp",
            new { operation = "definition", file = "/tmp/a.cs", line = 1, character = 0 }));

        Assert.True(result.IsError);
        Assert.Equal("LSP_UNAVAILABLE", result.Error?.Info?.Code);
        Assert.Contains("BLAZORLY_LSP", result.Error?.Message);
    }

    [Fact]
    public async Task Tool_Definition_RendersPathAndLine()
    {
        if (!PythonAvailable()) return;
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        new LspPlugin(["python3", FakeLspPath()], "/tmp").Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: "/tmp");

        var result = await harness.Tools.Execute(TestInput.For(agent, "lsp",
            new { operation = "definition", file = "main.cs", line = 4, character = 2 }));

        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("/tmp/x.cs:10", text);
        var locations = result.V().GetProperty("locations");
        Assert.Equal(10, locations[0].GetProperty("line").GetInt32());
    }
}

public class TerminalTests
{
    [Fact]
    public async Task Session_RoundTripsAndStatePersistsAcrossSends()
    {
        await using var harness = TestHarness.Create();
        new TerminalPlugin().Apply(harness.Ctx);
        var start = Directory.CreateTempSubdirectory("bz-term-start").FullName;
        var agent = harness.CreateAgent(cwd: start);

        var opened = await harness.Tools.Execute(TestInput.For(agent, "terminal_open", new { type = "shell" }));
        Assert.False(opened.IsError);
        var sessionId = opened.V().GetProperty("sessionId").GetString()!;
        Assert.StartsWith("term_", sessionId);

        var hello = await harness.Tools.Execute(TestInput.For(agent, "terminal_send",
            new { session_id = sessionId, text = "echo hello" }));
        Assert.False(hello.IsError);
        Assert.Contains("hello", hello.V().GetProperty("output").GetString());

        // state persists: cd then pwd in the same shell. The cd's prompt-settle can lag under
        // host load, so poll pwd until the shell reports the moved cwd (bounded).
        await harness.Tools.Execute(TestInput.For(agent, "terminal_send",
            new { session_id = sessionId, text = "cd /tmp" }));
        string pwdOutput = "";
        for (var attempt = 0; attempt < 25 && !pwdOutput.Contains("/tmp"); attempt++)
        {
            var pwd = await harness.Tools.Execute(TestInput.For(agent, "terminal_send",
                new { session_id = sessionId, text = "pwd" }));
            pwdOutput = pwd.V().GetProperty("output").GetString()!;
            if (!pwdOutput.Contains("/tmp")) await Task.Delay(200);
        }
        Assert.Contains("/tmp", pwdOutput);
        Assert.DoesNotContain(start, pwdOutput);

        var read = await harness.Tools.Execute(TestInput.For(agent, "terminal_read",
            new { session_id = sessionId }));
        Assert.False(read.IsError);
        Assert.Contains("hello", read.V().GetProperty("text").GetString());

        var list = await harness.Tools.Execute(TestInput.For(agent, "terminal_list", new { }));
        Assert.False(list.IsError);
        Assert.Contains(list.V().GetProperty("sessions").EnumerateArray(),
            s => s.GetProperty("sessionId").GetString() == sessionId);

        var closed = await harness.Tools.Execute(TestInput.For(agent, "terminal_close",
            new { session_id = sessionId }));
        Assert.False(closed.IsError);
        Assert.True(closed.V().GetProperty("closed").GetBoolean());

        var afterClose = await harness.Tools.Execute(TestInput.For(agent, "terminal_send",
            new { session_id = sessionId, text = "echo gone" }));
        Assert.True(afterClose.IsError);
        Assert.Equal("NO_SESSION", afterClose.Error?.Info?.Code);
    }

    [Fact]
    public async Task Sessions_AreOwnerScoped()
    {
        await using var harness = TestHarness.Create();
        new TerminalPlugin().Apply(harness.Ctx);
        var owner = harness.CreateAgent();
        var stranger = harness.CreateAgent();

        var opened = await harness.Tools.Execute(TestInput.For(owner, "terminal_open", new { type = "shell", name = "build" }));
        var sessionId = opened.V().GetProperty("sessionId").GetString()!;

        var foreignSend = await harness.Tools.Execute(TestInput.For(stranger, "terminal_send",
            new { session_id = sessionId, text = "echo nope" }));
        Assert.True(foreignSend.IsError);
        Assert.Equal("NOT_OWNER", foreignSend.Error?.Info?.Code);

        var foreignList = await harness.Tools.Execute(TestInput.For(stranger, "terminal_list", new { }));
        Assert.Empty(foreignList.V().GetProperty("sessions").EnumerateArray());

        var foreignClose = await harness.Tools.Execute(TestInput.For(stranger, "terminal_close",
            new { session_id = sessionId }));
        Assert.True(foreignClose.IsError);

        var ownedClose = await harness.Tools.Execute(TestInput.For(owner, "terminal_close",
            new { session_id = sessionId }));
        Assert.False(ownedClose.IsError);
    }

    [Fact]
    public async Task Tools_WithoutAgent_FailClosed()
    {
        await using var harness = TestHarness.Create();
        new TerminalPlugin().Apply(harness.Ctx);

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "terminal_list",
            Arguments = JsonSerializer.SerializeToElement(new { }),
            CallId = Ids.NewCallId(),
            Signal = CancellationToken.None,
        });

        Assert.True(result.IsError);
        Assert.Equal("NO_AGENT", result.Error?.Info?.Code);
    }
}

public class HooksTests
{
    private static async Task<string> WriteHooksAsync(string json)
    {
        var dir = Directory.CreateTempSubdirectory("bz-hooks").FullName;
        var path = Path.Combine(dir, "hooks.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task PreStepBlockingHook_RejectsTurnAndLogsHookEvents()
    {
        var path = await WriteHooksAsync(
            """
            [{"point":"pre-step","matcher":null,"command":"printf '{\"decision\":\"block\",\"reason\":\"not allowed\"}'"}]
            """);
        await using var harness = TestHarness.Create(_ => Scripted.Text("never"));
        new HooksPlugin(path).Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetDirectoryName(path));

        agent.Followup(Message.CreateUserText("blocked"));
        await agent.WhenIdleAsync();

        var events = agent.Session.Events;
        Assert.DoesNotContain(events, e => e.Type == SessionEventTypes.StepStart);
        var turnEnd = Assert.Single(events, e => e.Type == SessionEventTypes.TurnEnd);
        Assert.IsType<TurnEndReason.Blocked>(SessionEventRead.TurnEndReasonOf(turnEnd));

        var invoked = Assert.Single(events, e => e.Type == SessionEventTypes.HookInvoked);
        Assert.Equal("pre-step", invoked.Data.GetProperty("point").GetString());
        Assert.Equal("printf '{\"decision\":\"block\",\"reason\":\"not allowed\"}'",
            invoked.Data.GetProperty("handlerId").GetString());
        var result = Assert.Single(events, e => e.Type == SessionEventTypes.HookResult);
        Assert.Equal("block", result.Data.GetProperty("decision").GetString());
        Assert.Equal(0, result.Data.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task PreStepNonBlockingHook_LetsTheTurnRun()
    {
        var path = await WriteHooksAsync(
            """
            [{"point":"pre-step","command":"true"}]
            """);
        await using var harness = TestHarness.Create(_ => Scripted.Text("done"));
        new HooksPlugin(path).Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetDirectoryName(path));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var events = agent.Session.Events;
        Assert.Contains(events, e => e.Type == SessionEventTypes.StepStart);
        Assert.Contains(events, e => e.Type == SessionEventTypes.AssistantMessage);
        var result = Assert.Single(events, e => e.Type == SessionEventTypes.HookResult);
        Assert.Equal("allow", result.Data.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task TurnEndHook_RunsWhenTheTurnStops()
    {
        var path = await WriteHooksAsync(
            """
            [{"point":"turn-end","command":"echo '{\"decision\":\"allow\"}'"}]
            """);
        await using var harness = TestHarness.Create(_ => Scripted.Text("fin"));
        new HooksPlugin(path).Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetDirectoryName(path));

        agent.Followup(Message.CreateUserText("finish"));
        await agent.WhenIdleAsync();

        var events = agent.Session.Events;
        var invoked = Assert.Single(events, e => e.Type == SessionEventTypes.HookInvoked);
        Assert.Equal("turn-end", invoked.Data.GetProperty("point").GetString());
        var result = Assert.Single(events, e => e.Type == SessionEventTypes.HookResult);
        Assert.Equal("allow", result.Data.GetProperty("decision").GetString());
        Assert.Equal(0, result.Data.GetProperty("exitCode").GetInt32());
        Assert.Contains(events, e => e.Type == SessionEventTypes.TurnEnd);
    }

    [Fact]
    public async Task PostStepBlockingHook_EndsTurnBlockedAfterTheStep()
    {
        var path = await WriteHooksAsync(
            """
            [{"point":"post-step","matcher":null,"command":"printf '{\"decision\":\"block\",\"reason\":\"stop here\"}'"}]
            """);
        await using var harness = TestHarness.Create(_ => Scripted.Text("done"));
        new HooksPlugin(path).Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetDirectoryName(path));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var events = agent.Session.Events;
        Assert.Contains(events, e => e.Type == SessionEventTypes.StepStart); // the step ran first
        Assert.Contains(events, e => e.Type == SessionEventTypes.StepEnd);
        var turnEnd = Assert.Single(events, e => e.Type == SessionEventTypes.TurnEnd);
        Assert.IsType<TurnEndReason.Blocked>(SessionEventRead.TurnEndReasonOf(turnEnd));

        var invoked = Assert.Single(events, e => e.Type == SessionEventTypes.HookInvoked);
        Assert.Equal("post-step", invoked.Data.GetProperty("point").GetString());
        var result = Assert.Single(events, e => e.Type == SessionEventTypes.HookResult);
        Assert.Equal("block", result.Data.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task PostStepAllowingHook_LetsTheTurnComplete()
    {
        var path = await WriteHooksAsync(
            """
            [{"point":"post-step","command":"true"}]
            """);
        await using var harness = TestHarness.Create(_ => Scripted.Text("done"));
        new HooksPlugin(path).Apply(harness.Ctx);
        var agent = harness.CreateAgent(cwd: Path.GetDirectoryName(path));

        agent.Followup(Message.CreateUserText("go"));
        await agent.WhenIdleAsync();

        var events = agent.Session.Events;
        var turnEnd = Assert.Single(events, e => e.Type == SessionEventTypes.TurnEnd);
        Assert.IsType<TurnEndReason.Completed>(SessionEventRead.TurnEndReasonOf(turnEnd));
        var result = Assert.Single(events, e => e.Type == SessionEventTypes.HookResult);
        Assert.Equal("allow", result.Data.GetProperty("decision").GetString());
    }
}
