using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Mcp;

public sealed record McpOptions
{
    public required string ConfigPath { get; init; }
    public int ControlTimeoutMs { get; init; } = 30_000;
    public int CallTimeoutMs { get; init; } = 120_000;
    public int MaxRestartAttempts { get; init; } = 5;
}

public sealed record McpServerConfig(string Name, string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env);

/// <summary>
/// Bridges external MCP servers over stdio into native tools named
/// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> (dsh mcp-client). One plugin instance per server;
/// line-delimited JSON-RPC; reconnect supervision with budgeted attempts keeps last-good
/// tools registered during outages; tools/list_changed re-syncs. Resources and prompts are
/// not bridged; non-text content degrades to a text diagnostic.
/// </summary>
public sealed class McpClientService : IAsyncDisposable
{
    public const string ServiceKey = "mcp";

    private readonly McpOptions _options;
    private readonly ToolRuntime _tools;
    private readonly List<ServerConnection> _servers = [];
    private readonly ILogger _log;

    public interface ILogger { void Write(string message); }

    public McpClientService(ToolRuntime tools, McpOptions options, ILogger? log = null)
    {
        _tools = tools;
        _options = options;
        _log = log ?? ConsoleLogger.Instance;
    }

    public IReadOnlyList<ServerConnection> Servers => _servers;

    public static McpClientService Mount(HarnessContext ctx, McpOptions options, ILogger? log = null)
    {
        var service = new McpClientService(ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey), options, log);
        ctx.Provide(ServiceKey, service);
        service.Start();
        return service;
    }

    public void Start()
    {
        if (!File.Exists(_options.ConfigPath)) return;
        var config = JsonSerializer.Deserialize<McpConfigFile>(File.ReadAllText(_options.ConfigPath), SessionJson.Options);
        if (config?.Servers is null) return;
        foreach (var server in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Command)) continue;
            var connection = new ServerConnection(server, _tools, _options, _log);
            _servers.Add(connection);
            _ = connection.RunAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers) await server.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record McpConfigFile(IReadOnlyList<McpServerConfig>? Servers);

    /// <summary>One stdio MCP server: child process, pending-request map, tool registrations, reconnect budget.</summary>
    public sealed class ServerConnection : IAsyncDisposable
    {
        private readonly McpServerConfig _config;
        private readonly ToolRuntime _tools;
        private readonly McpOptions _options;
        private readonly ILogger _log;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly List<IDisposable> _registrations = [];
        private Process? _process;
        private long _nextId;
        private int _restartAttempts;
        private bool _initialized;

        public ServerConnection(McpServerConfig config, ToolRuntime tools, McpOptions options, ILogger log)
        {
            _config = config;
            _tools = tools;
            _options = options;
            _log = log;
        }

        public string Name => _config.Name;
        public string State { get; private set; } = "starting"; // starting | ready | restarting | failed

        public async Task RunAsync()
        {
            while (true)
            {
                try
                {
                    await InitializeAsync().ConfigureAwait(false);
                    State = "ready";
                    _restartAttempts = 0;
                    await SyncToolsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Write($"[mcp:{_config.Name}] {ex.Message}");
                    if (_restartAttempts >= _options.MaxRestartAttempts)
                    {
                        State = "failed";
                        _log.Write($"[mcp:{_config.Name}] giving up after {_options.MaxRestartAttempts} attempts; tools stay registered");
                        return;
                    }
                    State = "restarting";
                }
                var delay = TimeSpan.FromMilliseconds(Math.Min(1_000 * Math.Pow(2, _restartAttempts), 30_000));
                _restartAttempts++;
                try { await Task.Delay(delay).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task InitializeAsync()
        {
            StartProcess();
            var result = await RequestAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "blazorly-harness", version = "0.1.0" },
            }, _options.ControlTimeoutMs).ConfigureAwait(false);
            _ = result;
            _initialized = true;
            Notify("notifications/initialized");
        }

        private void StartProcess()
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.Command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in _config.Args) psi.ArgumentList.Add(arg);
            if (_config.Env is not null)
            {
                foreach (var (key, value) in _config.Env) psi.Environment[key] = value;
            }
            _process = Process.Start(psi) ?? throw new Kernel.HarnessException("MCP_SPAWN", $"failed to start '{_config.Command}'");
            _ = Task.Run(async () =>
            {
                var line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line)) _log.Write($"[mcp:{_config.Name}] stderr: {line}");
            });
            _ = Task.Run(ReadLoopAsync);
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                var stdout = _process!.StandardOutput;
                while (await stdout.ReadLineAsync().ConfigureAwait(false) is { } line)
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
                    if (message.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number)
                    {
                        var id = idElement.GetInt64();
                        if (_pending.TryRemove(id, out var pending))
                        {
                            if (message.TryGetProperty("error", out var error))
                            {
                                var text = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "server error";
                                pending.TrySetException(new ToolException("MCP_SERVER_ERROR", text ?? "server error"));
                            }
                            else
                            {
                                pending.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : JsonSerializer.SerializeToElement(new { }));
                            }
                        }
                    }
                    else if (message.TryGetProperty("method", out var method) && method.GetString() == "notifications/tools/list_changed")
                    {
                        _ = SyncToolsAsync();
                    }
                }
            }
            catch (Exception)
            {
                // stdout closed: the child is gone; the reconnect supervisor reacts
            }
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            _pending.Clear();
            _initialized = false;
        }

        private async Task SyncToolsAsync()
        {
            var result = await RequestAsync("tools/list", new { }, _options.ControlTimeoutMs).ConfigureAwait(false);
            var old = _registrations.ToList();
            _registrations.Clear();
            foreach (var toolElement in result.GetProperty("tools").EnumerateArray())
            {
                var rawName = toolElement.GetProperty("name").GetString() ?? "";
                var description = toolElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var schema = toolElement.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object ? s.Clone() : JsonSchema.Object().ToJson();
                var definition = new McpToolDefinition(_config.Name, rawName, description ?? $"MCP tool {rawName} on server {_config.Name}", schema, CallToolAsync);
                _registrations.Add(_tools.Register(definition));
            }
            foreach (var registration in old) registration.Dispose();
            _log.Write($"[mcp:{_config.Name}] {((JsonElement)result).GetProperty("tools").GetArrayLength()} tools registered");
        }

        private async Task<string> CallToolAsync(string rawName, JsonElement arguments)
        {
            if (!_initialized || _process is null || _process.HasExited)
                throw new ToolException("MCP_SERVER_UNAVAILABLE", $"mcp server '{_config.Name}' is not reachable");
            var result = await RequestAsync("tools/call", new { name = rawName, arguments }, _options.CallTimeoutMs).ConfigureAwait(false);
            var isError = result.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True;
            var text = new StringBuilder();
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "text" && part.TryGetProperty("text", out var partText))
                    {
                        text.Append(partText.GetString());
                    }
                    else
                    {
                        text.Append($"[{type ?? "unknown"} content not bridged]");
                    }
                    text.Append('\n');
                }
            }
            var rendered = text.ToString().TrimEnd();
            return isError ? throw new ToolException("MCP_TOOL_ERROR", rendered) : rendered;
        }

        private Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs)
        {
            if (_process is null || _process.HasExited)
                throw new ToolException("MCP_SERVER_UNAVAILABLE", $"mcp server '{_config.Name}' is not reachable");
            var id = Interlocked.Increment(ref _nextId);
            var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = pending;
            var message = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }, SessionJson.Options);
            try
            {
                WriteLine(message);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(id, out _);
                throw new ToolException("MCP_SERVER_UNAVAILABLE", $"write to '{_config.Name}' failed: {ex.Message}");
            }
            var timeout = Task.Delay(timeoutMs);
            return Task.WhenAny(pending.Task, timeout).ContinueWith(t =>
            {
                if (t.Result == pending.Task) return pending.Task;
                _pending.TryRemove(id, out _);
                throw new ToolException("MCP_TIMEOUT", $"request '{method}' to '{_config.Name}' timed out");
            }, TaskContinuationOptions.ExecuteSynchronously).Unwrap();
        }

        private void Notify(string method)
        {
            WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
            }, SessionJson.Options));
        }

        private void WriteLine(string line)
        {
            var stdin = _process?.StandardInput ?? throw new ToolException("MCP_SERVER_UNAVAILABLE", "server is not running");
            lock (stdin)
            {
                stdin.WriteLine(line);
                stdin.Flush();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var registration in _registrations) registration.Dispose();
            try { _process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process?.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    public sealed class ConsoleLogger : ILogger
    {
        public static readonly ConsoleLogger Instance = new();
        public void Write(string message) => Console.Error.WriteLine(message);
    }
}

/// <summary>The bridge from one remote MCP tool to a harness tool definition.</summary>
public sealed class McpToolDefinition(
    string server,
    string rawName,
    string description,
    JsonElement inputSchema,
    Func<string, JsonElement, Task<string>> call) : ToolDefinition
{
    public override string Name => $"mcp__{server}__{rawName}";

    public override string Description { get; } = description;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Raw(inputSchema);

    public override JsonSchema.Schema Output { get; } = JsonSchema.String("The tool's text content.");

    public override async Task<JsonElement> Execute(JsonElement args, ToolRunContext exec)
    {
        var text = await call(rawName, args).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(text, SessionJson.Options);
    }

    public override IReadOnlyList<ContentBlock> Render(JsonElement args, JsonElement value)
        => [new TextBlock(value.GetString() ?? "")];
}
