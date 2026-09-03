using System.Text.Json;
using Blazorly.Harness.Cli;
using Xunit;

namespace Blazorly.Harness.Tests;

public class EvalTaskLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-eval-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string WriteTask(string id, string taskJson)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), taskJson);
        return dir;
    }

    [Fact]
    public void Load_ParsesValidTask()
    {
        var dir = WriteTask("t1", """{"description":"d","prompt":"p","checks":[{"name":"c","run":"true"}]}""");
        var task = EvalTask.Load(dir);
        Assert.Equal("t1", task.Id);
        Assert.Equal("d", task.Description);
        Assert.Equal("p", task.Prompt);
        Assert.Equal(300, task.TimeoutSeconds);
        Assert.Single(task.Checks);
        Assert.Null(task.ExpectFinish);
        Assert.Null(task.Interrupt);
    }

    [Fact]
    public void Load_ParsesExpectFinishAndInterrupt()
    {
        var dir = WriteTask("int", """
            {"description":"d","prompt":"p","expectFinish":"aborted",
             "interrupt":{"cancelAfterMs":1200},
             "checks":[{"name":"c","run":"true"}]}
            """);
        var task = EvalTask.Load(dir);
        Assert.Equal("aborted", task.ExpectFinish);
        Assert.Equal(1200, task.Interrupt!.CancelAfterMs);
        Assert.Null(task.Interrupt.KillAfterMs);

        var kill = WriteTask("kill", """
            {"description":"d","prompt":"p","expectFinish":"completed",
             "interrupt":{"killAfterMs":300,"resumePrompt":"continue"},
             "checks":[{"name":"c","run":"true"}]}
            """);
        var loaded = EvalTask.Load(kill);
        Assert.Equal(300, loaded.Interrupt!.KillAfterMs);
        Assert.Equal("continue", loaded.Interrupt.ResumePrompt);
    }

    [Fact]
    public void Load_RejectsMissingFileAndBadJson()
    {
        var missing = Assert.Throws<EvalLoadException>(() => EvalTask.Load(Path.Combine(_root, "absent")));
        Assert.Contains("missing task.json", missing.Message);

        var dir = WriteTask("bad", "{not json");
        var bad = Assert.Throws<EvalLoadException>(() => EvalTask.Load(dir));
        Assert.Contains("invalid task.json", bad.Message);
    }

    [Fact]
    public void Load_RejectsMissingFields()
    {
        var noPrompt = WriteTask("np", """{"description":"d","checks":[{"name":"c","run":"true"}]}""");
        Assert.Throws<EvalLoadException>(() => EvalTask.Load(noPrompt));

        var noChecks = WriteTask("nc", """{"description":"d","prompt":"p"}""");
        Assert.Throws<EvalLoadException>(() => EvalTask.Load(noChecks));

        var badCheck = WriteTask("bc", """{"description":"d","prompt":"p","checks":[{"name":"c"}]}""");
        Assert.Throws<EvalLoadException>(() => EvalTask.Load(badCheck));
    }

    [Fact]
    public void Load_RejectsBadFinishAndInterruptShapes()
    {
        var badFinish = WriteTask("bf", """{"description":"d","prompt":"p","expectFinish":"exploded","checks":[{"name":"c","run":"true"}]}""");
        Assert.Throws<EvalLoadException>(() => EvalTask.Load(badFinish));

        var both = WriteTask("both", """{"description":"d","prompt":"p","interrupt":{"cancelAfterMs":10,"killAfterMs":10},"checks":[{"name":"c","run":"true"}]}""");
        Assert.Throws<EvalLoadException>(() => EvalTask.Load(both));

        var none = WriteTask("none", """{"description":"d","prompt":"p","interrupt":{},"checks":[{"name":"c","run":"true"}]}""");
        Assert.Throws<EvalLoadException>(() => EvalTask.Load(none));

        var orphanResume = WriteTask("orphan", """{"description":"d","prompt":"p","interrupt":{"cancelAfterMs":10,"resumePrompt":"x"},"checks":[{"name":"c","run":"true"}]}""");
        Assert.Throws<EvalLoadException>(() => EvalTask.Load(orphanResume));
    }

    [Fact]
    public void ExitOfFinish_MapsToHeadlessExitContract()
    {
        Assert.Equal(0, EvalTask.ExitOfFinish(null));
        Assert.Equal(0, EvalTask.ExitOfFinish("completed"));
        Assert.Equal(0, EvalTask.ExitOfFinish("max-tokens"));
        Assert.Equal(2, EvalTask.ExitOfFinish("error"));
        Assert.Equal(2, EvalTask.ExitOfFinish("blocked"));
        Assert.Equal(3, EvalTask.ExitOfFinish("aborted"));
        Assert.Equal(3, EvalTask.ExitOfFinish("interrupted"));
    }

    [Fact]
    public async Task ShellAsync_ReportsPassFailAndTimeout()
    {
        var cwd = Path.GetTempPath();
        var ok = await EvalRunner.ShellAsync("true", cwd, 10, CancellationToken.None);
        Assert.Equal(0, ok.ExitCode);
        var fail = await EvalRunner.ShellAsync("exit 3", cwd, 10, CancellationToken.None);
        Assert.Equal(3, fail.ExitCode);
        var timedOut = await EvalRunner.ShellAsync("sleep 30", cwd, 1, CancellationToken.None);
        Assert.Equal(124, timedOut.ExitCode);
        Assert.Contains("timed out", timedOut.Output);
    }

    [Fact]
    public async Task ShellAsync_PassesEnvironmentToChecks()
    {
        var cwd = Path.GetTempPath();
        var (exit, output) = await EvalRunner.ShellAsync("echo \"$BLAZORLY_SESSION_ID\"", cwd, 10, CancellationToken.None,
            new Dictionary<string, string> { ["BLAZORLY_SESSION_ID"] = "session-abc" });
        Assert.Equal(0, exit);
        Assert.Contains("session-abc", output);
    }

    [Fact]
    public void RepoEvalTasks_AllLoad()
    {
        // Walk up from the test bin to the repo root and load every shipped task.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "tasks")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var tasksRoot = Path.Combine(dir!.FullName, "eval", "tasks");
        var loaded = Directory.GetDirectories(tasksRoot).Select(EvalTask.Load).ToList();
        Assert.True(loaded.Count >= 7);
        Assert.Contains(loaded, t => t.Id == "interrupt-cancel" && t.ExpectFinish == "aborted" && t.Interrupt!.CancelAfterMs > 0);
        Assert.Contains(loaded, t => t.Id == "interrupt-timeout" && t.ExpectFinish == "aborted" && t.Interrupt is null);
        Assert.Contains(loaded, t => t.Id == "interrupt-restart" && t.ExpectFinish == "completed" && t.Interrupt!.KillAfterMs > 0);
    }
}

[Collection("BlazorlyHome")]
public class EvalRunnerTests : BootstrapperTestBase
{
    private string WriteTasks((string Id, string Json)[] tasks)
    {
        var root = Path.Combine(Path.GetTempPath(), "blazorly-eval-t-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (id, json) in tasks)
        {
            var dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "task.json"), json);
        }
        return root;
    }

    [Fact]
    public async Task Run_ScoresTasksAndWritesReport()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks([
            ("smoke", """{"description":"smoke","prompt":"run the scripted task","checks":[{"name":"always","run":"true"}]}"""),
            ("failing", """{"description":"failing","prompt":"run the scripted task","checks":[{"name":"never","run":"exit 2"}]}"""),
        ]);
        var finished = Path.Combine(Path.GetTempPath(), "blazorly-eval-o-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var summary = await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks,
                OutDir = finished,
                Out = new StringWriter(),
            });
            Assert.Equal(2, summary.Total);
            Assert.Equal(1, summary.Passed);
            Assert.Equal(1, summary.Failed);
            Assert.True(File.Exists(Path.Combine(finished, "results.json")));
            Assert.True(File.Exists(Path.Combine(finished, "summary.md")));
            Assert.True(File.Exists(Path.Combine(finished, "smoke.json")));
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(finished, "results.json")));
            Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
            var md = File.ReadAllText(Path.Combine(finished, "summary.md"));
            Assert.Contains("smoke", md);
            Assert.Contains("PASS", md);
            // Eval sessions stay out of the ambient home.
            Assert.False(Directory.Exists(Path.Combine(Home, "sessions")));
        }
        finally
        {
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
            try { Directory.Delete(finished, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Run_RejectsSetupEscapeAndBadTasksDir()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks([
            ("escape", """{"description":"e","prompt":"x","setup":{"files":{"../outside.txt":"o"}},"checks":[{"name":"c","run":"true"}]}"""),
        ]);
        var finished = Path.Combine(Path.GetTempPath(), "blazorly-eval-o2-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var summary = await EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = tasks,
                OutDir = finished,
                Out = new StringWriter(),
            });
            Assert.Equal(1, summary.Failed);
            Assert.Contains("escapes the workspace", summary.Tasks[0].Error);

            await Assert.ThrowsAsync<EvalLoadException>(() => EvalRunner.RunAsync(new EvalOptions
            {
                TasksDir = Path.Combine(Path.GetTempPath(), "blazorly-eval-absent-" + Guid.NewGuid().ToString("N")[..8]),
                OutDir = finished,
                Out = new StringWriter(),
            }));
        }
        finally
        {
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
            try { Directory.Delete(finished, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Run_InterruptCancel_ExpectAbortedScoresTheDurableContract()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks([("interrupt-cancel", """
            {"description":"user stop mid-turn","prompt":"run the scripted task",
             "provider":"scripted","model":"test","timeoutSeconds":60,
             "expectFinish":"aborted","interrupt":{"cancelAfterMs":1200},
             "checks":[
               {"name":"log-env","run":"test -f \"$BLAZORLY_SESSION_LOG\""},
               {"name":"aborted","run":"grep -q '\"kind\":\"aborted\"' \"$BLAZORLY_SESSION_LOG\""}
             ]}
            """)]);
        var finished = Path.Combine(Path.GetTempPath(), "blazorly-eval-ic-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // DefaultTimeoutSeconds 0 = honor each task's own timeoutSeconds (no CLI override).
            var summary = await EvalRunner.RunAsync(new EvalOptions { TasksDir = tasks, OutDir = finished, Out = new StringWriter(), DefaultTimeoutSeconds = 0 });
            Assert.Equal(1, summary.Passed);
            var result = summary.Tasks[0];
            Assert.Equal("aborted", result.Finish);
            Assert.Equal(3, result.ExitCode);
            Assert.Null(result.Error);
            Assert.All(result.Checks, c => Assert.True(c.Pass, $"{c.Name}: {c.Output}"));
        }
        finally
        {
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
            try { Directory.Delete(finished, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Run_InterruptTimeout_ExpectAbortedScoresTheDurableContract()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks([("interrupt-timeout", """
            {"description":"watchdog timeout mid-turn","prompt":"run the scripted task",
             "provider":"scripted","model":"test","timeoutSeconds":2,
             "expectFinish":"aborted",
             "checks":[
               {"name":"aborted","run":"grep -q '\"kind\":\"aborted\"' \"$BLAZORLY_SESSION_LOG\""}
             ]}
            """)]);
        var finished = Path.Combine(Path.GetTempPath(), "blazorly-eval-it-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var summary = await EvalRunner.RunAsync(new EvalOptions { TasksDir = tasks, OutDir = finished, Out = new StringWriter(), DefaultTimeoutSeconds = 0 });
            Assert.Equal(1, summary.Passed);
            var result = summary.Tasks[0];
            Assert.Equal("aborted", result.Finish);
            Assert.Equal(3, result.ExitCode);
        }
        finally
        {
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
            try { Directory.Delete(finished, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Run_InterruptRestart_KillsAndResumesToCompletion()
    {
        using var server = new FakeOpenAiServer();
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var tasks = WriteTasks([("interrupt-restart", """
            {"description":"restart kill mid-turn","prompt":"run the scripted task",
             "provider":"scripted","model":"test","timeoutSeconds":60,
             "expectFinish":"completed",
             "interrupt":{"killAfterMs":300,"resumePrompt":"Continue: summarize."},
             "checks":[
               {"name":"log-env","run":"test -f \"$BLAZORLY_SESSION_LOG\""},
               {"name":"resumed","run":"grep -q '\"kind\":\"completed\"' \"$BLAZORLY_SESSION_LOG\""}
             ]}
            """)]);
        var finished = Path.Combine(Path.GetTempPath(), "blazorly-eval-ir-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var summary = await EvalRunner.RunAsync(new EvalOptions { TasksDir = tasks, OutDir = finished, Out = new StringWriter(), DefaultTimeoutSeconds = 0 });
            Assert.Equal(1, summary.Passed);
            var result = summary.Tasks[0];
            Assert.Equal("completed", result.Finish);
            Assert.Equal(0, result.ExitCode);
            Assert.All(result.Checks, c => Assert.True(c.Pass, $"{c.Name}: {c.Output}"));
            Assert.True(Directory.Exists(Path.Combine(finished, "home", "sessions", "interrupt-restart")));
        }
        finally
        {
            try { Directory.Delete(tasks, recursive: true); } catch (IOException) { }
            try { Directory.Delete(finished, recursive: true); } catch (IOException) { }
        }
    }
}
