using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Blazorly.Harness.Sdk;

public sealed record HarnessSpawnOptions
{
    public string DotNet { get; init; } = "dotnet";
    /// <summary>Path to the compiled Blazorly.Harness.Cli.dll (the `serve-stdio` host).</summary>
    public required string ServerDll { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? WorkspacePath { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public sealed record SessionEventFrame(string SessionId, string Type, long Seq, long Time, JsonElement Data);

public sealed record HarnessRunResult
{
    public required string SessionId { get; init; }
    public required string Response { get; init; }
    public required string Finish { get; init; }
    public required IReadOnlyList<SessionEventFrame> Events { get; init; }
}

/// <summary>
/// Client for the blazorly JSON-RPC stdio automation server (dsh packages/sdk/client).
/// Spawn a server, connect, then drive sessions: RunAsync owns a whole job (new session →
/// prompt → wait until the agent goes idle after activity) or use PromptAsync to steer an
/// existing session. Teardown ladder: shutdown request → exit wait → kill.
/// </summary>
public sealed class HarnessClient : IAsyncDisposable
{
    private readonly JsonSerializerOptions _wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<Action<SessionEventFrame>> _eventSubscribers = [];
    private readonly List<Action<string, string>> _statusSubscribers = [];
    private readonly object _gate = new();
    private readonly TimeSpan _requestTimeout;

    private TextWriter? _stdin;
    private TextReader? _stdout;
    private Process? _process;
    private long _nextId;
    private bool _initialized;

    private HarnessClient(TimeSpan requestTimeout) => _requestTimeout = requestTimeout;

    public static HarnessClient Spawn(HarnessSpawnOptions options, TimeSpan? requestTimeout = null)
    {
        var client = new HarnessClient(requestTimeout ?? TimeSpan.FromMinutes(10));
        var arguments = new List<string> { options.ServerDll, "serve-stdio" };
        if (!string.IsNullOrWhiteSpace(options.WorkspacePath)) arguments.AddRange(["--workspace", options.WorkspacePath]);

        var psi = new ProcessStartInfo
        {
            FileName = options.DotNet,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        if (options.WorkingDirectory is not null) psi.WorkingDirectory = options.WorkingDirectory;
        if (options.Environment is not null)
        {
            foreach (var (key, value) in options.Environment) psi.Environment[key] = value;
        }

        client._process = Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn the blazorly server");
        client.Attach(client._process.StandardOutput, client._process.StandardInput);
        _ = Task.Run(async () =>
        {
            try { while (await client._process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) { /* server logs */ } }
            catch (Exception) { /* process gone */ }
        });
        return client;
    }

    /// <summary>Attaches to an already-running server over arbitrary text streams.</summary>
    public void Attach(TextReader serverOutput, TextWriter serverInput)
    {
        _stdout = serverOutput;
        _stdin = serverInput;
        _ = Task.Run(ReadLoopAsync);
    }

    public async Task ConnectAsync(string? clientName = null, CancellationToken ct = default)
    {
        var result = await RequestAsync("initialize", new
        {
            clientInfo = new { name = clientName ?? "blazorly-sdk", version = "1.0.0" },
        }, ct).ConfigureAwait(false);
        _ = result;
        _initialized = true;
    }

    /// <summary>Owned run: new session, prompt, wait for idle after activity, derive the answer.</summary>
    public async Task<HarnessRunResult> RunAsync(string job, string? cwd = null, Action<SessionEventFrame>? onEvent = null, CancellationToken ct = default)
    {
        EnsureInitialized();
        var created = await RequestAsync("session/new", new { cwd }, ct).ConfigureAwait(false);
        var sessionId = created.GetProperty("sessionId").GetString()!;

        var frames = new List<SessionEventFrame>();
        var status = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawActivity = false;

        void OnFrame(SessionEventFrame frame)
        {
            if (frame.SessionId != sessionId) return;
            lock (frames) frames.Add(frame);
            onEvent?.Invoke(frame);
            if (frame.Type == SessionEventTypes_TurnEnd) sawActivity = true;
        }
        void OnStatus(string eventSessionId, string value)
        {
            if (eventSessionId == sessionId && value == "idle" && sawActivity) status.TrySetResult();
        }

        using var eventSub = SubscribeEvents(OnFrame);
        using var statusSub = SubscribeStatus(OnStatus);

        await RequestAsync("session/prompt", new { sessionId, text = job }, ct).ConfigureAwait(false);
        await status.Task.WaitAsync(ct).ConfigureAwait(false);

        List<SessionEventFrame> snapshot;
        lock (frames) snapshot = [.. frames];
        return new HarnessRunResult
        {
            SessionId = sessionId,
            Response = DeriveResponse(snapshot),
            Finish = DeriveFinish(snapshot),
            Events = snapshot,
        };
    }

    public async Task<string> PromptAsync(string sessionId, string text, CancellationToken ct = default)
    {
        EnsureInitialized();
        var result = await RequestAsync("session/prompt", new { sessionId, text }, ct).ConfigureAwait(false);
        return result.GetProperty("messageId").GetString()!;
    }

    /// <summary>Raw JSON-RPC call for protocol-level access (errors surface as HarnessServerException).</summary>
    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken ct = default)
        => RequestAsync(method, parameters ?? new { }, ct);

    /// <summary>True once the spawned server process has exited.</summary>
    public bool ServerHasExited => _process?.HasExited ?? true;

    public async Task CancelAsync(string sessionId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await RequestAsync("session/cancel", new { sessionId }, ct).ConfigureAwait(false);
    }

    public IDisposable SubscribeEvents(Action<SessionEventFrame> subscriber)
    {
        lock (_eventSubscribers) _eventSubscribers.Add(subscriber);
        return new Subscription(() => { lock (_eventSubscribers) _eventSubscribers.Remove(subscriber); });
    }

    public IDisposable SubscribeStatus(Action<string, string> subscriber)
    {
        lock (_statusSubscribers) _statusSubscribers.Add(subscriber);
        return new Subscription(() => { lock (_statusSubscribers) _statusSubscribers.Remove(subscriber); });
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        try
        {
            if (_initialized) await RequestAsync("shutdown", new { }, ct).ConfigureAwait(false);
        }
        catch
        {
            // fall through to the kill ladder
        }
        if (_process is not null)
        {
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await ShutdownAsync().ConfigureAwait(false); }
        catch
        {
            try { _process?.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    // ---- wire ----

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("client is not attached");
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        var frame = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }, _wire);
        lock (stdin)
        {
            stdin.WriteLine(frame);
            stdin.Flush();
        }
        var done = await Task.WhenAny(pending.Task, Task.Delay(_requestTimeout, ct)).ConfigureAwait(false);
        if (done != pending.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"request '{method}' timed out");
        }
        ct.ThrowIfCancellationRequested();
        return await pending.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _stdout!.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                JsonElement message;
                try
                {
                    message = JsonDocument.Parse(line).RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
                {
                    var id = idElement.GetInt64();
                    if (_pending.TryRemove(id, out var pending))
                    {
                        if (message.TryGetProperty("error", out var error))
                        {
                            var code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -32000;
                            var text = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : "server error";
                            pending.TrySetException(new HarnessServerException(code, text ?? "server error"));
                        }
                        else
                        {
                            pending.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : JsonSerializer.SerializeToElement(new { }, _wire));
                        }
                    }
                    continue;
                }

                var method = message.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
                if (method is null || !message.TryGetProperty("params", out var parameters)) continue;
                switch (method)
                {
                    case "session.event":
                    {
                        var sessionId = parameters.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                        if (parameters.TryGetProperty("event", out var payload))
                        {
                            var frame = new SessionEventFrame(
                                sessionId ?? "",
                                payload.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                                payload.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number ? seq.GetInt64() : -1,
                                payload.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number ? time.GetInt64() : -1,
                                payload.TryGetProperty("data", out var data) ? data.Clone() : JsonSerializer.SerializeToElement(new { }, _wire));
                            Action<SessionEventFrame>[] snapshot;
                            lock (_eventSubscribers) snapshot = [.. _eventSubscribers];
                            foreach (var subscriber in snapshot) subscriber(frame);
                        }
                        break;
                    }
                    case "session.status":
                    {
                        var sessionId = parameters.TryGetProperty("sessionId", out var s2) ? s2.GetString() ?? "" : "";
                        var value = parameters.TryGetProperty("status", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        Action<string, string>[] snapshot;
                        lock (_statusSubscribers) snapshot = [.. _statusSubscribers];
                        foreach (var subscriber in snapshot) subscriber(sessionId, value);
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // stream closed: the server process is gone
        }
        foreach (var pending in _pending.Values) pending.TrySetException(new HarnessServerException(-32000, "server connection closed"));
        _pending.Clear();
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("call ConnectAsync before using sessions");
    }

    private static string DeriveResponse(IReadOnlyList<SessionEventFrame> frames)
    {
        var last = frames
            .Where(f => f.Type == "assistant/message" && f.Data.ValueKind == JsonValueKind.Object)
            .LastOrDefault(f => TextOf(f.Data) is { Length: > 0 });
        return last is null ? string.Empty : TextOf(last.Data)!;
    }

    private static string DeriveFinish(IReadOnlyList<SessionEventFrame> frames)
    {
        var last = frames.LastOrDefault(f => f.Type == "turn/end" && f.Data.ValueKind == JsonValueKind.Object);
        if (last is null) return "error";
        return last.Data.TryGetProperty("reason", out var reason) && reason.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
            ? kind.GetString()!
            : "completed";
    }

    private static string? TextOf(JsonElement assistantMessage)
    {
        if (!assistantMessage.TryGetProperty("message", out var message)) return null;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
            {
                text.Append(value.GetString());
            }
        }
        return text.ToString();
    }

    private const string SessionEventTypes_TurnEnd = "turn/end";

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>A JSON-RPC error returned by the server.</summary>
public sealed class HarnessServerException(int code, string message) : Exception($"[{code}] {message}")
{
    public int Code { get; } = code;
}
