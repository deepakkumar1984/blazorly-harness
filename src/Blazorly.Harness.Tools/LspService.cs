using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record LspLocation(string File, int Line, string Text);

/// <summary>
/// A language-server client: spawns the configured command, speaks Content-Length framed
/// JSON-RPC over its stdio, and resolves responses through a pending-id dictionary fed by a
/// background reader task. An empty command array means "not configured" and every query
/// fails with LSP_UNAVAILABLE.
/// </summary>
public sealed class LspService(string[] command, string? workspaceRoot = null) : IAsyncDisposable
{
    public const string ServiceKey = "lsp";
    public const string UnavailableCode = "LSP_UNAVAILABLE";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    private readonly string[] _command = command;
    private readonly string? _workspaceRoot = workspaceRoot;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    private Task<Process>? _startTask;
    private Process? _process;
    private Stream? _input;
    private Task? _readerTask;
    private int _nextId;
    private bool _disposed;

    public bool IsConfigured => _command is { Length: > 0 };

    // ---- queries (line is 1-based, character is 0-based, like the lsp tool) ----

    public Task<IReadOnlyList<LspLocation>> Definition(string file, int line, int character, CancellationToken ct = default)
        => LocationsAsync("textDocument/definition", file, line, character, context: null, ct);

    public Task<IReadOnlyList<LspLocation>> References(string file, int line, int character, CancellationToken ct = default)
        => LocationsAsync("textDocument/references", file, line, character, new { includeDeclaration = false }, ct);

    private async Task<IReadOnlyList<LspLocation>> LocationsAsync(string method, string file, int line, int character, object? context, CancellationToken ct)
    {
        var absolute = ResolveFile(file);
        var parameters = new
        {
            textDocument = new { uri = FileUri(absolute) },
            position = new { line = line - 1, character },
            context,
        };
        var result = await RequestAsync(method, parameters, ct).ConfigureAwait(false);
        return NormalizeLocations(result);
    }

    public async Task<LspLocation> Hover(string file, int line, int character, CancellationToken ct = default)
    {
        var absolute = ResolveFile(file);
        var parameters = new
        {
            textDocument = new { uri = FileUri(absolute) },
            position = new { line = line - 1, character },
        };
        var result = await RequestAsync("textDocument/hover", parameters, ct).ConfigureAwait(false);
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new LspLocation(absolute, line, "");
        var resultLine = line;
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("range", out var range)
            && range.TryGetProperty("start", out var start)
            && start.TryGetProperty("line", out var startLine)
            && startLine.ValueKind == JsonValueKind.Number)
        {
            resultLine = startLine.GetInt32() + 1;
        }
        return new LspLocation(absolute, resultLine, ContentsText(result));
    }

    // ---- server lifecycle ----

    private Task<Process> EnsureStartedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _startTask ??= StartCoreAsync();
        }
    }

    private async Task<Process> StartCoreAsync()
    {
        if (!IsConfigured)
            throw new Kernel.HarnessException(UnavailableCode, "no language server command is configured");
        Process process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _command[0],
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _workspaceRoot ?? Directory.GetCurrentDirectory(),
            };
            foreach (var argument in _command.Skip(1)) startInfo.ArgumentList.Add(argument);
            process = Process.Start(startInfo)
                ?? throw new Kernel.HarnessException(UnavailableCode, $"failed to spawn '{_command[0]}'");
        }
        catch (Exception ex) when (ex is not Kernel.HarnessException)
        {
            throw new Kernel.HarnessException(UnavailableCode,
                $"failed to start the language server '{string.Join(' ', _command)}': {ex.Message}");
        }

        try
        {
            lock (_gate)
            {
                _process = process;
                _input = process.StandardInput.BaseStream;
            }
            _readerTask = Task.Run(ReadPumpAsync);
            var initialize = new
            {
                processId = (int?)null,
                rootUri = "file://" + (_workspaceRoot ?? Directory.GetCurrentDirectory()),
                capabilities = new { },
            };
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            handshakeCts.CancelAfter(StartTimeout);
            await SendRequestCoreAsync("initialize", initialize, handshakeCts.Token).ConfigureAwait(false);
            await WriteMessageAsync(new JsonRpcNotification("initialized"), CancellationToken.None).ConfigureAwait(false);
            return process;
        }
        catch
        {
            lock (_gate) _startTask = null; // a failed start may be retried on the next query
            Kill(process);
            FailPending("the language server failed to start");
            throw;
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        return await SendRequestCoreAsync(method, parameters, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendRequestCoreAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId); // initialize is always id 1
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            await WriteMessageAsync(new JsonRpcRequest(id, method, parameters), ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        var message = await pending.Task.WaitAsync(ct).ConfigureAwait(false);
        if (message.TryGetProperty("error", out var error))
            throw new Kernel.HarnessException("LSP_ERROR", $"the language server rejected '{method}': {error}");
        return message.TryGetProperty("result", out var result) ? result : default;
    }

    private async Task WriteMessageAsync(object message, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, SessionJson.Options));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = Volatile.Read(ref _input)
                ?? throw new Kernel.HarnessException(UnavailableCode, "the language server is not running");
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadPumpAsync()
    {
        try
        {
            var stream = _process!.StandardOutput.BaseStream;
            while (true)
            {
                var contentLength = -1;
                while (true)
                {
                    var line = await ReadLineAsync(stream).ConfigureAwait(false);
                    if (line is null) return; // EOF: the server exited
                    if (line.Length == 0) break;
                    var separator = line.IndexOf(':');
                    if (separator < 0) continue;
                    if (line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line[(separator + 1)..].Trim(), CultureInfo.InvariantCulture);
                }
                if (contentLength < 0) continue;
                var body = new byte[contentLength];
                await ReadExactAsync(stream, body).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var message = document.RootElement.Clone();
                if (message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                    && _pending.TryRemove(idElement.GetInt32(), out var pending))
                {
                    pending.TrySetResult(message);
                }
                // notifications and server-initiated requests are skipped
            }
        }
        catch
        {
            // framing or stream failure: fail everything still waiting below
        }
        finally
        {
            FailPending("the language server closed the connection");
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            var read = await ReadByteAsync(stream).ConfigureAwait(false);
            if (read < 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            if (read == '\n') break;
            if (read != '\r') bytes.Add((byte)read);
        }
        return Encoding.ASCII.GetString([.. bytes]);
    }

    private static async Task<int> ReadByteAsync(Stream stream)
    {
        var buffer = new byte[1];
        return await stream.ReadAsync(buffer).ConfigureAwait(false) == 0 ? -1 : buffer[0];
    }

    private static async Task ReadExactAsync(Stream stream, byte[] target)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = await stream.ReadAsync(target.AsMemory(offset)).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("the language server exited mid-message");
            offset += read;
        }
    }

    private void FailPending(string message)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
                pending.TrySetException(new Kernel.HarnessException(UnavailableCode, message));
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort teardown
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<Process>? startTask;
        Process? process;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            startTask = _startTask;
            process = _process;
        }
        if (startTask is { IsCompleted: false })
        {
            try { process = await startTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* teardown continues */ }
        }
        if (process is not null && !process.HasExited)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendRequestCoreAsync("shutdown", null, shutdownCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // a dead or wedged server is killed below
            }
            try { await WriteMessageAsync(new JsonRpcNotification("exit"), CancellationToken.None).ConfigureAwait(false); }
            catch { /* see above */ }
            try
            {
                if (!process.WaitForExit(2_000)) Kill(process);
            }
            catch { /* see above */ }
        }
        if (process is not null)
        {
            try { process.Dispose(); } catch { /* see above */ }
        }
        FailPending("the language service was disposed");
        _writeLock.Dispose();
    }

    // ---- result normalization ----

    private static IReadOnlyList<LspLocation> NormalizeLocations(JsonElement result)
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        IEnumerable<JsonElement> items = result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray()
            : [result];
        var locations = new List<LspLocation>();
        foreach (var item in items)
        {
            if (TryLocation(item, out var location)) locations.Add(location);
        }
        return locations;
    }

    private static bool TryLocation(JsonElement item, out LspLocation location)
    {
        location = new LspLocation("", 0, "");
        if (item.ValueKind != JsonValueKind.Object) return false;
        string? uri = null;
        if (item.TryGetProperty("uri", out var uriElement) && uriElement.ValueKind == JsonValueKind.String)
            uri = uriElement.GetString();
        else if (item.TryGetProperty("targetUri", out var targetUri) && targetUri.ValueKind == JsonValueKind.String)
            uri = targetUri.GetString();
        if (uri is null) return false;
        var line = 1;
        if (item.TryGetProperty("range", out var range) || item.TryGetProperty("targetSelectionRange", out range))
        {
            if (range.TryGetProperty("start", out var start)
                && start.TryGetProperty("line", out var startLine)
                && startLine.ValueKind == JsonValueKind.Number)
            {
                line = startLine.GetInt32() + 1;
            }
        }
        location = new LspLocation(ToPath(uri!), line, "");
        return true;
    }

    private static string ContentsText(JsonElement hover)
    {
        if (!hover.TryGetProperty("contents", out var contents)) return "";
        return MarkedText(contents);
    }

    private static string MarkedText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Object when element.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n\n", element.EnumerateArray().Select(MarkedText)),
        _ => "",
    };

    private string ResolveFile(string file) =>
        Path.GetFullPath(file, _workspaceRoot ?? Directory.GetCurrentDirectory());

    private static string FileUri(string path) => new Uri(path).AbsoluteUri;

    private static string ToPath(string uri)
    {
        try { return new Uri(uri).LocalPath; }
        catch { return uri; }
    }

    private sealed record JsonRpcRequest(int Id, string Method, object? Params)
    {
        public string Jsonrpc => "2.0";
    }

    private sealed record JsonRpcNotification(string Method, object? Params = null)
    {
        public string Jsonrpc => "2.0";
    }
}

public sealed record LspArgs(string Operation, string File, int Line, int Character);

public sealed record LspOutput(string Operation, IReadOnlyList<LspLocation> Locations);

/// <summary>lsp: definition/references/hover through a configured language server.</summary>
public sealed class LspTool(LspService service) : ToolDefinition<LspArgs, LspOutput>
{
    public override string Name => "lsp";

    public override string Description =>
        "Resolve code navigation through a language server: definition finds where a symbol is "
        + "declared, references finds where it is used, hover returns the documentation at a "
        + "position. line is 1-based; character is 0-based. Results render as path:line text.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["operation"] = JsonSchema.String("The navigation query to run.",
                values:
                [
                    JsonSerializer.SerializeToElement("definition"),
                    JsonSerializer.SerializeToElement("references"),
                    JsonSerializer.SerializeToElement("hover"),
                ]),
            ["file"] = JsonSchema.String("Path to the file, resolved against the session workspace."),
            ["line"] = JsonSchema.Integer("1-based line of the position."),
            ["character"] = JsonSchema.Integer("0-based character offset of the position."),
        },
        required: ["operation", "file", "line", "character"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["operation"] = JsonSchema.String(),
            ["locations"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["file"] = JsonSchema.String(),
                    ["line"] = JsonSchema.Integer(),
                    ["text"] = JsonSchema.String(),
                },
                Required = ["file", "line", "text"],
                AdditionalProperties = false,
            }),
        },
        required: ["operation", "locations"]);

    public override int? TimeoutMs => 15_000;

    protected override bool IsConcurrencySafeTyped(LspArgs args) => true;

    protected override async Task<LspOutput> ExecuteTyped(LspArgs args, ToolRunContext exec)
    {
        if (!service.IsConfigured)
            throw new ToolException(LspService.UnavailableCode,
                "no language server is configured — set BLAZORLY_LSP to a server command "
                + "(for example \"clangd\" or \"typescript-language-server --stdio\") and restart the harness");
        if (args.Line < 1) throw new ToolException("INVALID_ARGS", "line must be 1-based (>= 1)");
        if (args.Character < 0) throw new ToolException("INVALID_ARGS", "character must be >= 0");

        var root = exec.Agent?.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
        var file = Path.GetFullPath(args.File, root);
        try
        {
            switch (args.Operation)
            {
                case "definition":
                    return new LspOutput(args.Operation, await service.Definition(file, args.Line, args.Character, exec.Signal).ConfigureAwait(false));
                case "references":
                    return new LspOutput(args.Operation, await service.References(file, args.Line, args.Character, exec.Signal).ConfigureAwait(false));
                case "hover":
                    return new LspOutput(args.Operation, [await service.Hover(file, args.Line, args.Character, exec.Signal).ConfigureAwait(false)]);
                default:
                    throw new ToolException("INVALID_ARGS", $"unknown lsp operation '{args.Operation}'");
            }
        }
        catch (Exception ex) when (ex is not ToolException)
        {
            throw new ToolException(LspService.UnavailableCode,
                $"the language server is unavailable ({ex.Message}) — verify the BLAZORLY_LSP command starts a stdio LSP server");
        }
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(LspArgs args, LspOutput output)
    {
        if (output.Locations.Count == 0) return [new TextBlock("No results.")];
        var builder = new StringBuilder();
        foreach (var location in output.Locations)
        {
            builder.Append(location.File).Append(':').Append(location.Line);
            if (location.Text.Length > 0) builder.Append(' ').Append(location.Text.ReplaceLineEndings(" "));
            builder.AppendLine();
        }
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(LspArgs args) => new()
    {
        Card = "generic",
        Kind = "search",
        Title = $"{args.Operation} {Path.GetFileName(args.File)}:{args.Line}",
    };
}

/// <summary>Mounts the lsp tool; the server command comes from BLAZORLY_LSP (space-split) unless given.</summary>
public sealed class LspPlugin(string[]? command = null, string? workspaceRoot = null) : HarnessPlugin
{
    public override string Name => "lsp";
    public override string[] Inject { get; } = ["tools"];

    public LspService Service { get; } = new(command ?? FromEnv(), workspaceRoot);

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        ctx.Provide(LspService.ServiceKey, Service);
        var tools = ctx.Get<ToolRuntime>("tools");
        ctx.Effect(tools.Register(new LspTool(Service)).Dispose);
        ctx.Effect(() => Service.DisposeAsync().GetAwaiter().GetResult());
        return Task.CompletedTask;
    }

    private static string[] FromEnv()
    {
        var raw = Environment.GetEnvironmentVariable("BLAZORLY_LSP");
        return string.IsNullOrWhiteSpace(raw) ? [] : raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
