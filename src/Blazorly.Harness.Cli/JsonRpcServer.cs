using System.Collections.Concurrent;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Cli;

public sealed record JsonRpcServerOptions
{
    /// <summary>Workspace root for sessions; defaults to the invoking directory.</summary>
    public string? WorkspacePath { get; init; }
}

/// <summary>
/// Line-delimited JSON-RPC 2.0 automation server over arbitrary text streams (dsh
/// packages/sdk/server): stdout carries only protocol frames. `initialize` gates the session
/// methods; every durable event of sessions created here is forwarded verbatim as
/// `session.event`, and agent activity flips as `session.status`. stdin EOF or `shutdown`
/// ends the process. Extension vs. the original: `session/cancel` is supported.
/// </summary>
public static class JsonRpcServer
{
    private const string ProtocolVersion = "1.0";

    private sealed record TrackedSession(Agent Agent, IDisposable Subscription);

    public static async Task<int> RunAsync(JsonRpcServerOptions options, TextReader input, TextWriter output, TextWriter log, CancellationToken ct)
    {
        var bootstrapper = new HarnessBootstrapper();
        bootstrapper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var root = options.WorkspacePath ?? Environment.CurrentDirectory;
            var workspace = bootstrapper.Workspaces.Ensure(root);

            var sessions = new ConcurrentDictionary<string, TrackedSession>(StringComparer.Ordinal);
            var initialized = false;
            var shutdownRequested = false;

            // Status forwarding for sessions created through this server.
            _ = bootstrapper.Context.Events.On<StatusEvent>("agent/status", (payload, _) =>
            {
                if (sessions.ContainsKey(payload.Agent.Session.Id))
                {
                    Write(output, Notification("session.status", new { sessionId = payload.Agent.Session.Id, status = payload.Status }));
                }
                return Task.CompletedTask;
            });

            async Task<JsonElement?> HandleAsync(long id, string method, JsonElement parameters, CancellationToken requestCt)
            {
                switch (method)
                {
                    case "initialize":
                        initialized = true;
                        return JsonSerializer.SerializeToElement(new
                        {
                            protocolVersion = ProtocolVersion,
                            serverInfo = new { name = "blazorly-harness", version = "1.0.0" },
                            capabilities = new { sessions = true, events = true, cancel = true },
                        }, SessionJson.Options);

                    case "session/new" when initialized:
                    {
                        var cwd = parameters.TryGetProperty("cwd", out var cwdElement) && cwdElement.ValueKind == JsonValueKind.String
                            ? cwdElement.GetString()
                            : workspace.Root;
                        var effectiveRoot = cwd is null ? workspace.Root : bootstrapper.Workspaces.Ensure(cwd).Root;
                        var agent = bootstrapper.Loop.Create(new SessionMeta(Cwd: effectiveRoot));
                        var session = agent.Session;
                        var subscription = session.Subscribe(@event =>
                        {
                            Write(output, Notification("session.event", new
                            {
                                sessionId = session.Id,
                                @event = new { @event.Type, @event.Seq, @event.Time, data = @event.Data },
                            }));
                        });
                        sessions[session.Id] = new TrackedSession(agent, subscription);
                        _ = bootstrapper.Sessions.Persistence?.FlushAsync(session.Id, requestCt);
                        return JsonSerializer.SerializeToElement(new { sessionId = session.Id }, SessionJson.Options);
                    }

                    case "session/prompt" when initialized:
                    {
                        var sessionId = RequireString(parameters, "sessionId");
                        var text = RequireString(parameters, "text");
                        var tracked = sessions.GetValueOrDefault(sessionId)
                            ?? throw new JsonRpcFault(-32001, $"unknown session '{sessionId}'");
                        var message = Message.CreateUserText(text);
                        tracked.Agent.Followup(message);
                        _ = bootstrapper.Sessions.Persistence?.FlushAsync(sessionId, requestCt);
                        return JsonSerializer.SerializeToElement(new { messageId = message.Id }, SessionJson.Options);
                    }

                    case "session/cancel" when initialized:
                    {
                        var sessionId = RequireString(parameters, "sessionId");
                        var tracked = sessions.GetValueOrDefault(sessionId)
                            ?? throw new JsonRpcFault(-32001, $"unknown session '{sessionId}'");
                        tracked.Agent.Cancel(AgentCancelCause.User());
                        return JsonSerializer.SerializeToElement(new { ok = true }, SessionJson.Options);
                    }

                    case "shutdown":
                        shutdownRequested = true;
                        return JsonSerializer.SerializeToElement(new { ok = true }, SessionJson.Options);

                    default:
                        if (!initialized && method is "session/new" or "session/prompt" or "session/cancel")
                            throw new JsonRpcFault(-32002, "not initialized: call initialize first");
                        throw new JsonRpcFault(-32601, $"method not found: {method}");
                }
            }

            while (!ct.IsCancellationRequested && !shutdownRequested)
            {
                var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // stdin EOF: exit
                if (line.Length == 0 || line[0] != '{') continue;

                JsonElement message;
                try
                {
                    message = JsonDocument.Parse(line).RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    log.WriteLine($"[jsonrpc] ignoring malformed line: {ex.Message}");
                    continue;
                }

                var id = message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetInt64()
                    : (long?)null;
                var method = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
                    ? methodElement.GetString()!
                    : null;
                if (method is null) continue; // a stray response; we never send requests client-ward

                var parameters = message.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object
                    ? paramsElement.Clone()
                    : JsonSerializer.SerializeToElement(new { }, SessionJson.Options);

                try
                {
                    var result = await HandleAsync(id ?? 0, method, parameters, ct).ConfigureAwait(false);
                    if (id is not null) Write(output, Result(id.Value, result!.Value));
                }
                catch (JsonRpcFault fault)
                {
                    log.WriteLine($"[jsonrpc] {method}: {fault.Message}");
                    if (id is not null) Write(output, Fault(id.Value, fault.Code, fault.Message));
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[jsonrpc] {method} failed: {ex.Message}");
                    if (id is not null) Write(output, Fault(id.Value, -32000, ex.Message));
                }
            }

            foreach (var tracked in sessions.Values) tracked.Subscription.Dispose();
            return 0;
        }
        finally
        {
            await bootstrapper.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string RequireString(JsonElement parameters, string name)
        => parameters.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value
            ? value
            : throw new JsonRpcFault(-32602, $"missing string parameter '{name}'");

    private static void Write(TextWriter output, string frame)
    {
        lock (output) output.WriteLine(frame);
    }

    private static string Result(long id, JsonElement result)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }, SessionJson.Options);

    private static string Fault(long id, int code, string message)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        }, SessionJson.Options);

    private static string Notification(string method, object parameters)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, SessionJson.Options);

    private sealed class JsonRpcFault(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
