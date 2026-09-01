using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>A JSON-RPC error frame from the ACP server, surfaced as an exception.</summary>
public sealed class AcpServerFaultException(long code, string message) : Exception($"[{code}] {message}")
{
    public long Code { get; } = code;

    public string FaultMessage { get; } = message;
}

/// <summary>
/// Minimal in-test ACP client: spawns the built CLI (`serve-acp`) in an isolated
/// BLAZORLY_HOME and exchanges newline-delimited JSON-RPC frames over real stdio pipes.
/// </summary>
public sealed class AcpTestClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<(string SessionId, JsonElement Update)> _updates = new();
    private readonly List<(string Method, JsonElement Params)> _serverRequests = new();
    private readonly object _writeLock = new();
    private int _nextId;

    /// <summary>Auto-answer for server-ward request_permission frames: "allow-once" or "reject-once".</summary>
    public string AutoPermissionOptionId { get; set; } = "allow-once";

    public List<(string Method, JsonElement Params)> ServerRequests
    {
        get { lock (_serverRequests) return [.. _serverRequests]; }
    }

    private AcpTestClient(Process process)
    {
        _process = process;
        _ = Task.Run(ReadLoop);
    }

    public static AcpTestClient Spawn(string home, string workspace, int chunkDelayMs = 0, string? permission = null)
    {
        Directory.CreateDirectory(workspace);
        var serverDll = typeof(Blazorly.Harness.Cli.HeadlessRunner).Assembly.Location;
        var arguments = $"\"{serverDll}\" serve-acp --workspace \"{workspace}\"";
        if (chunkDelayMs > 0) arguments += $" --chunk-delay {chunkDelayMs}";
        if (permission is not null) arguments += $" --permission {permission}";
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workspace,
        };
        start.Environment["BLAZORLY_HOME"] = home;
        var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start serve-acp");
        return new AcpTestClient(process);
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_closed.Token).ConfigureAwait(false);
                if (line is null) return;
                if (line.Length == 0 || line[0] != '{') continue;
                var frame = JsonDocument.Parse(line).RootElement.Clone();
                if (frame.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
                {
                    if (_pending.TryRemove(idElement.GetInt64(), out var pending)) pending.TrySetResult(frame);
                }
                else if (frame.TryGetProperty("method", out var requestMethod) && frame.TryGetProperty("id", out var requestId))
                {
                    // A server-ward request (e.g. session/request_permission): record and answer.
                    var methodName = requestMethod.GetString() ?? "";
                    var requestParams = frame.TryGetProperty("params", out var requestParamsElement) ? requestParamsElement.Clone() : default;
                    lock (_serverRequests) _serverRequests.Add((methodName, requestParams));
                    JsonElement? result = methodName == "session/request_permission"
                        ? JsonSerializer.SerializeToElement(new { outcome = new { outcome = "selected", optionId = AutoPermissionOptionId } })
                        : JsonSerializer.SerializeToElement(new { });
                    await SendAsync(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = requestId.Clone(), ["result"] = result }).ConfigureAwait(false);
                }
                else if (frame.TryGetProperty("method", out var method) && method.GetString() == "session/update"
                    && frame.TryGetProperty("params", out var parameters))
                {
                    var update = parameters.GetProperty("update").Clone();
                    lock (_updates) _updates.Add((parameters.GetProperty("sessionId").GetString()!, update));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // the reader dies with the pipe at dispose; pending calls fail via dispose
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters = null, int timeoutSeconds = 90)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        await SendAsync(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }).ConfigureAwait(false);
        var winner = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);
        if (winner != pending.Task) throw new TimeoutException($"no response for '{method}' within {timeoutSeconds}s");
        var response = await pending.Task.ConfigureAwait(false);
        if (response.TryGetProperty("error", out var error))
            throw new AcpServerFaultException(error.GetProperty("code").GetInt64(), error.GetProperty("message").GetString() ?? "");
        return response.GetProperty("result").Clone();
    }

    public Task NotifyAsync(string method, object parameters)
        => SendAsync(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    private async Task SendAsync(Dictionary<string, object?> frame)
    {
        var line = JsonSerializer.Serialize(frame);
        lock (_writeLock)
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public List<JsonElement> Updates(string sessionId)
    {
        lock (_updates) return [.. _updates.Where(u => u.SessionId == sessionId).Select(u => u.Update)];
    }

    public async Task<JsonElement> WaitForUpdateAsync(string sessionId, string sessionUpdateType, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var match = Updates(sessionId).FirstOrDefault(u => u.GetProperty("sessionUpdate").GetString() == sessionUpdateType);
            if (match.ValueKind != JsonValueKind.Undefined) return match;
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new TimeoutException($"no '{sessionUpdateType}' update within {timeoutSeconds}s");
    }

    public async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        try
        {
            _process.StandardInput.Close();
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        _process.Dispose();
    }
}

/// <summary>
/// Real-process ACP wire tests: handshake and gating, committed-event updates, error
/// discipline, cancellation, the single prompt slot, and load replay from another process.
/// </summary>
public class AcpServerTests : BootstrapperTestBase
{
    private string Workspace() => Path.Combine(Path.GetTempPath(), "blazorly-acp-ws-" + Guid.NewGuid().ToString("N")[..8]);

    private static object[] TextPrompt(string text) => [new { type = "text", text }];

    [Fact]
    public async Task Initialize_NegotiatesAndGatesSessionMethods()
    {
        var workspace = Workspace();
        await using var client = AcpTestClient.Spawn(Home, workspace);

        var gated = await Assert.ThrowsAsync<AcpServerFaultException>(() =>
            client.RequestAsync("session/new", new { cwd = workspace }));
        Assert.Equal(-32602, gated.Code);

        var init = await client.RequestAsync("initialize", new { protocolVersion = 1, clientCapabilities = new { } });
        Assert.Equal(1, init.GetProperty("protocolVersion").GetInt64());
        Assert.Equal("blazorly-harness-acp", init.GetProperty("agentInfo").GetProperty("name").GetString());
        var promptCapabilities = init.GetProperty("agentCapabilities").GetProperty("promptCapabilities");
        Assert.False(promptCapabilities.GetProperty("image").GetBoolean());
        Assert.False(promptCapabilities.GetProperty("audio").GetBoolean());
        Assert.True(init.GetProperty("agentCapabilities").GetProperty("sessionCapabilities").TryGetProperty("resume", out _));
        Assert.Empty(init.GetProperty("authMethods").EnumerateArray());

        var created = await client.RequestAsync("session/new", new { cwd = workspace });
        Assert.Contains("session-", created.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Prompt_StreamsCommittedUpdates_AndSettlesWithEndTurn()
    {
        var workspace = Workspace();
        await using var client = AcpTestClient.Spawn(Home, workspace);
        await client.RequestAsync("initialize");
        var sessionId = (await client.RequestAsync("session/new", new { cwd = workspace })).GetProperty("sessionId").GetString()!;

        var result = await client.RequestAsync("session/prompt", new { sessionId, prompt = TextPrompt("run the demo task") });

        Assert.Equal("end_turn", result.GetProperty("stopReason").GetString());
        var updates = client.Updates(sessionId);

        var call = updates.First(u => u.GetProperty("sessionUpdate").GetString() == "tool_call"
            && u.GetProperty("title").GetString() == "bash");
        Assert.Equal("in_progress", call.GetProperty("status").GetString());
        Assert.Equal("other", call.GetProperty("kind").GetString());
        Assert.Contains("hello from blazorly harness", call.GetProperty("rawInput").GetProperty("command").GetString());
        Assert.False(call.TryGetProperty("messageId", out _), "tool_call frames carry no messageId");

        var done = updates.First(u => u.GetProperty("sessionUpdate").GetString() == "tool_call_update"
            && u.GetProperty("toolCallId").GetString() == call.GetProperty("toolCallId").GetString());
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Contains("hello from blazorly harness",
            done.GetProperty("content")[0].GetProperty("content").GetProperty("text").GetString());
        Assert.True(
            updates.FindIndex(u => u.GetProperty("sessionUpdate").GetString() == "tool_call")
            < updates.FindIndex(u => u.GetProperty("sessionUpdate").GetString() == "tool_call_update"),
            "tool_call must precede its tool_call_update");

        var plan = updates.Single(u => u.GetProperty("sessionUpdate").GetString() == "plan");
        Assert.Equal(2, plan.GetProperty("entries").GetArrayLength());
        Assert.Equal("completed", plan.GetProperty("entries")[0].GetProperty("status").GetString());
        Assert.Equal("in_progress", plan.GetProperty("entries")[1].GetProperty("status").GetString());

        var chunk = updates.Single(u => u.GetProperty("sessionUpdate").GetString() == "agent_message_chunk");
        Assert.Contains("demo run completed", chunk.GetProperty("content").GetProperty("text").GetString());
        Assert.False(string.IsNullOrEmpty(chunk.GetProperty("messageId").GetString()));

        var usage = updates.Last(u => u.GetProperty("sessionUpdate").GetString() == "usage_update");
        Assert.True(updates.Count(u => u.GetProperty("sessionUpdate").GetString() == "usage_update") >= 2, "usage_update follows every assistant message");
        Assert.True(usage.GetProperty("size").GetInt64() > 0);
    }

    [Fact]
    public async Task Errors_UnknownSession_UnknownMethod_RelativeCwd_UnsupportedBlock()
    {
        var workspace = Workspace();
        await using var client = AcpTestClient.Spawn(Home, workspace);
        await client.RequestAsync("initialize");

        var unknownSession = await Assert.ThrowsAsync<AcpServerFaultException>(() =>
            client.RequestAsync("session/prompt", new { sessionId = "session-missing", prompt = TextPrompt("hi") }));
        Assert.Equal(-32602, unknownSession.Code);

        var unknownMethod = await Assert.ThrowsAsync<AcpServerFaultException>(() => client.RequestAsync("session/frobnicate"));
        Assert.Equal(-32601, unknownMethod.Code);

        var relativeCwd = await Assert.ThrowsAsync<AcpServerFaultException>(() =>
            client.RequestAsync("session/new", new { cwd = "relative/path" }));
        Assert.Equal(-32602, relativeCwd.Code);

        var extraDirs = await Assert.ThrowsAsync<AcpServerFaultException>(() =>
            client.RequestAsync("session/new", new { cwd = workspace, additionalDirectories = new[] { "/tmp" } }));
        Assert.Equal(-32602, extraDirs.Code);

        var sessionId = (await client.RequestAsync("session/new", new { cwd = workspace })).GetProperty("sessionId").GetString()!;
        var image = await Assert.ThrowsAsync<AcpServerFaultException>(() =>
            client.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new object[] { new { type = "image", data = "aGk=", mimeType = "image/png" } },
            }));
        Assert.Equal(-32602, image.Code);
    }

    [Fact]
    public async Task Cancel_SettlesThePromptAsCancelled()
    {
        var workspace = Workspace();
        await using var client = AcpTestClient.Spawn(Home, workspace, chunkDelayMs: 120);
        await client.RequestAsync("initialize");
        var sessionId = (await client.RequestAsync("session/new", new { cwd = workspace })).GetProperty("sessionId").GetString()!;

        var promptTask = client.RequestAsync("session/prompt", new { sessionId, prompt = TextPrompt("start the demo") });
        await client.WaitForUpdateAsync(sessionId, "tool_call");
        await client.NotifyAsync("session/cancel", new { sessionId });

        var result = await promptTask;
        Assert.Equal("cancelled", result.GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task SecondPromptWhileInFlight_FaultsAndFirstStillSettles()
    {
        var workspace = Workspace();
        await using var client = AcpTestClient.Spawn(Home, workspace, chunkDelayMs: 120);
        await client.RequestAsync("initialize");
        var sessionId = (await client.RequestAsync("session/new", new { cwd = workspace })).GetProperty("sessionId").GetString()!;

        var first = client.RequestAsync("session/prompt", new { sessionId, prompt = TextPrompt("start the demo") });
        await client.WaitForUpdateAsync(sessionId, "tool_call");

        var second = await Assert.ThrowsAsync<AcpServerFaultException>(() =>
            client.RequestAsync("session/prompt", new { sessionId, prompt = TextPrompt("second") }));
        Assert.Equal(-32602, second.Code);
        Assert.Contains("already in flight", second.FaultMessage);

        await client.NotifyAsync("session/cancel", new { sessionId });
        Assert.Equal("cancelled", (await first).GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task Load_ReplaysHistoryFromAnotherProcess_AndContinuesTheSession()
    {
        var workspace = Workspace();
        await using var first = AcpTestClient.Spawn(Home, workspace);
        await first.RequestAsync("initialize");
        var sessionId = (await first.RequestAsync("session/new", new { cwd = workspace })).GetProperty("sessionId").GetString()!;
        Assert.Equal("end_turn", (await first.RequestAsync("session/prompt", new { sessionId, prompt = TextPrompt("run the demo task") }))
            .GetProperty("stopReason").GetString());

        var active = await Assert.ThrowsAsync<AcpServerFaultException>(() =>
            first.RequestAsync("session/load", new { sessionId, cwd = workspace }));
        Assert.Equal(-32602, active.Code);
        Assert.Contains("already active", active.FaultMessage);

        await first.DisposeAsync();

        await using var second = AcpTestClient.Spawn(Home, workspace);
        await second.RequestAsync("initialize");
        var loaded = await second.RequestAsync("session/load", new { sessionId, cwd = workspace });
        Assert.Equal(JsonValueKind.Null, loaded.ValueKind);

        var replay = second.Updates(sessionId);
        Assert.Contains(replay, u => u.GetProperty("sessionUpdate").GetString() == "user_message_chunk");
        var callIndex = replay.FindIndex(u => u.GetProperty("sessionUpdate").GetString() == "tool_call");
        var doneIndex = replay.FindIndex(u => u.GetProperty("sessionUpdate").GetString() == "tool_call_update");
        var chunkIndex = replay.FindIndex(u => u.GetProperty("sessionUpdate").GetString() == "agent_message_chunk");
        Assert.True(callIndex >= 0 && doneIndex >= 0 && chunkIndex >= 0, "replay must include tool lifecycle and text");
        Assert.True(0 < callIndex && callIndex < doneIndex && doneIndex < chunkIndex, "replay order must follow the log");

        var result = await second.RequestAsync("session/prompt", new { sessionId, prompt = TextPrompt("summarize again") });
        Assert.Equal("end_turn", result.GetProperty("stopReason").GetString());
    }
}
