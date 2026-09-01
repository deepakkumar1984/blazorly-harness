using System.Text.Json;
using Blazorly.Harness.Sdk;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Real-process tests: the SDK client spawns the built CLI (`serve-stdio`) in an isolated
/// BLAZORLY_HOME and drives sessions over the wire.
/// </summary>
public class SdkClientTests : BootstrapperTestBase, IAsyncDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "blazorly-sdk-ws-" + Guid.NewGuid().ToString("N")[..8]);

    private HarnessClient Spawn()
    {
        Directory.CreateDirectory(_workspace);
        var serverDll = typeof(Blazorly.Harness.Cli.HeadlessRunner).Assembly.Location;
        return HarnessClient.Spawn(new HarnessSpawnOptions
        {
            ServerDll = serverDll,
            WorkspacePath = _workspace,
            Environment = new Dictionary<string, string> { ["BLAZORLY_HOME"] = Home },
        });
    }

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var client = Spawn();
        try
        {
            await client.ConnectAsync();
            var result = await client.CallAsync("session/new"); // proves the gated surface opened
            Assert.Contains("session-", result.GetProperty("sessionId").GetString());
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_CompletesWithResponseEventsAndStatus()
    {
        var client = Spawn();
        try
        {
            await client.ConnectAsync();
            var statuses = new List<string>();
            using var statusSub = client.SubscribeStatus((_, status) => statuses.Add(status));

            var result = await client.RunAsync("run the demo task", onEvent: _ => { });

            Assert.Equal("completed", result.Finish);
            Assert.Contains("session-", result.SessionId);
            Assert.Contains("demo run completed", result.Response);
            Assert.Contains(result.Events, f => f.Type == "user/message");
            Assert.Contains(result.Events, f => f.Type == "tool/result");
            Assert.Contains(result.Events, f => f.Type == "assistant/message");
            Assert.Contains(result.Events, f => f.Type == "turn/end");
            Assert.Contains("running", statuses);
            Assert.Contains("idle", statuses);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task PromptAfterRun_AppendsAFurtherTurnToIdle()
    {
        var client = Spawn();
        try
        {
            await client.ConnectAsync();
            var first = await client.RunAsync("run the demo task");

            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long lastSeq = first.Events.Count == 0 ? -1 : first.Events.Max(f => f.Seq);
            var idleAfterActivity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activity = false;
            using var events = client.SubscribeEvents(frame =>
            {
                if (frame.SessionId != first.SessionId) return;
                if (frame.Seq > lastSeq && frame.Type == "turn/end") ended.TrySetResult();
            });
            using var statuses = client.SubscribeStatus((sessionId, status) =>
            {
                if (sessionId != first.SessionId) return;
                if (status == "running") activity = true;
                if (status == "idle" && activity) idleAfterActivity.TrySetResult();
            });

            await client.PromptAsync(first.SessionId, "and once more");
            await idleAfterActivity.Task.WaitAsync(TimeSpan.FromSeconds(120));

            var endedFired = ended.Task.IsCompleted || (await Task.WhenAny(ended.Task, Task.Delay(100))) == ended.Task;
            Assert.True(endedFired);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProtocolErrors_SurfaceAsServerExceptions()
    {
        var client = Spawn();
        try
        {
            // Session method before initialize.
            var notInitialized = await Assert.ThrowsAsync<HarnessServerException>(
                () => client.CallAsync("session/new"));
            Assert.Equal(-32002, notInitialized.Code);

            await client.ConnectAsync();

            // Unknown method.
            var unknown = await Assert.ThrowsAsync<HarnessServerException>(
                () => client.CallAsync("definitely/not/a/method"));
            Assert.Equal(-32601, unknown.Code);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task Shutdown_StopsTheServerProcess()
    {
        var client = Spawn();
        await client.ConnectAsync();
        Assert.False(client.ServerHasExited);
        await client.ShutdownAsync();
        Assert.True(client.ServerHasExited);
    }

    public async ValueTask DisposeAsync()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
        await Task.CompletedTask;
    }
}
