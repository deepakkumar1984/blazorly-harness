using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record TerminalSessionInfo(string SessionId, string? Name, int Pid, bool Running);

/// <summary>
/// Persistent shell sessions over PIPED stdio — there is no PTY, so interactive TUIs
/// (vim, top, anything that needs terminal control sequences) are not supported.
/// Sessions are owned by the agent that opened them; other agents get an error.
/// </summary>
public sealed class TerminalService : IDisposable
{
    public const string ServiceKey = "terminals";
    public const string SentinelPrefix = "__BZT_DONE_";

    private const int SigInt = 2;
    private const int SigTerm = 15;
    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private readonly object _gate = new();
    private readonly Dictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);
    private int _nextId;

    public string Open(Agent owner, string? name, string? cwd)
    {
        string id;
        lock (_gate) id = $"term_{Interlocked.Increment(ref _nextId)}";
        var workingDirectory = cwd ?? owner.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
        // setsid puts the shell in its own session/process group so a kill can address
        // exactly the shell's group (kill(-pid)) — never the harness process itself and
        // never an unrelated pid picked up by a racy /proc tree walk.
        var setsid = FindSetsid();
        var startInfo = new ProcessStartInfo
        {
            FileName = setsid ?? "/bin/bash",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
        };
        if (setsid is not null) startInfo.ArgumentList.Add("/bin/bash");
        startInfo.ArgumentList.Add("--noprofile");
        startInfo.ArgumentList.Add("--norc");
        startInfo.Environment["PS1"] = "$ ";
        var process = Process.Start(startInfo)
            ?? throw new ToolException("TERMINAL_START", "failed to spawn /bin/bash");

        var session = new TerminalSession(id, owner.Id, name, process, groupIsolated: setsid is not null);
        _ = Task.Run(() => PumpAsync(process.StandardOutput, session));
        _ = Task.Run(() => PumpAsync(process.StandardError, session));
        lock (_gate) _sessions[id] = session;
        return id;
    }

    private static string? FindSetsid()
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (var candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public IReadOnlyList<TerminalSessionInfo> List(Agent owner)
    {
        lock (_gate)
        {
            return _sessions.Values
                .Where(s => s.OwnerAgentId == owner.Id)
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new TerminalSessionInfo(s.Id, s.Name, s.Pid, s.Running))
                .ToList();
        }
    }

    public TerminalSessionInfo Info(Agent caller, string id)
    {
        var session = Resolve(caller, id);
        return new TerminalSessionInfo(session.Id, session.Name, session.Pid, session.Running);
    }

    /// <summary>Returns the current buffer snapshot (stdout+stderr, prompts included).</summary>
    public string Read(Agent caller, string id)
    {
        var session = Resolve(caller, id);
        lock (session.Gate) return session.Buffer.ToString();
    }

    /// <summary>
    /// Runs text, waits (up to waitMs) for the completion sentinel, and returns only the output
    /// produced since the previous send, with sentinel lines and any echoed command stripped.
    /// </summary>
    public async Task<string> SendAsync(Agent caller, string id, string text, int waitMs = 1500)
    {
        var session = Resolve(caller, id);
        int start;
        lock (session.Gate)
        {
            start = session.Buffer.Length;
            session.Mark = start;
        }
        var seq = session.NextSeq();
        var sentinel = $"{SentinelPrefix}{seq}_";
        Write(session, text + "\n");
        Write(session, $"echo \"{sentinel}$?\"\n");

        var deadline = Environment.TickCount64 + Math.Max(0, waitMs);
        while (Environment.TickCount64 < deadline)
        {
            if (ContainsSentinel(session, start, sentinel)) break;
            await Task.Delay(20).ConfigureAwait(false);
        }
        string output;
        lock (session.Gate)
        {
            output = session.Buffer.ToString(start, session.Buffer.Length - start);
            session.Mark = session.Buffer.Length;
        }
        return CleanOutput(output, sentinel, text);
    }

    /// <summary>Types text without submitting it (no newline, no sentinel wait); returns nothing.</summary>
    public void Write(Agent caller, string id, string text)
    {
        var session = Resolve(caller, id);
        Write(session, text);
    }

    /// <summary>Empties the buffer so Read (and any UI polling it) starts from blank.</summary>
    public void Clear(Agent caller, string id)
    {
        var session = Resolve(caller, id);
        lock (session.Gate)
        {
            session.Buffer.Clear();
            session.Mark = 0;
        }
    }

    /// <summary>
    /// Best-effort signal delivery. SIGINT interrupts the shell's direct children
    /// (what Ctrl+C would do in a real terminal); SIGTERM and SIGKILL take down the
    /// shell and — when it runs in its own process group — everything it spawned,
    /// without ever touching the harness process.
    /// </summary>
    public void Signal(Agent caller, string id, string signal)
    {
        var session = Resolve(caller, id);
        switch (signal)
        {
            case "SIGKILL":
                KillTree(session);
                break;
            case "SIGINT":
                InterruptChildren(session);
                break;
            case "SIGTERM":
                KillTree(session);
                break;
            default:
                throw new ToolException("INVALID_SIGNAL", $"unknown signal '{signal}' (expected SIGINT, SIGTERM, or SIGKILL)");
        }
    }

    public void Close(Agent caller, string id)
    {
        var session = Resolve(caller, id);
        lock (_gate) _sessions.Remove(id);
        KillTree(session);
        session.Process.Dispose();
    }

    public void Dispose()
    {
        List<TerminalSession> sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var session in sessions)
        {
            KillTree(session);
            session.Process.Dispose();
        }
    }

    internal TerminalSession Resolve(Agent caller, string id)
    {
        TerminalSession? session;
        lock (_gate) _sessions.TryGetValue(id, out session);
        if (session is null)
            throw new ToolException("NO_SESSION", $"no terminal session '{id}'");
        if (!string.Equals(session.OwnerAgentId, caller.Id, StringComparison.Ordinal))
            throw new ToolException("NOT_OWNER", $"terminal session '{id}' belongs to another agent");
        return session;
    }

    private static async Task PumpAsync(StreamReader reader, TerminalSession session)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0) break;
                lock (session.Gate) session.Buffer.Append(buffer, 0, read);
            }
        }
        catch
        {
            // the stream closed: the pump ends
        }
    }

    private static void Write(TerminalSession session, string text)
    {
        try
        {
            var writer = session.Process.StandardInput;
            writer.Write(text);
            writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new ToolException("SESSION_CLOSED", $"terminal session '{session.Id}' is no longer running");
        }
    }

    private static bool ContainsSentinel(TerminalSession session, int start, string sentinel)
    {
        lock (session.Gate)
        {
            if (session.Buffer.Length <= start) return false;
            return session.Buffer.ToString(start, session.Buffer.Length - start).Contains(sentinel, StringComparison.Ordinal);
        }
    }

    private static string CleanOutput(string output, string sentinel, string command)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var kept = lines.Where(line => !line.Contains(sentinel, StringComparison.Ordinal)).ToList();
        // piped shells do not echo, but strip the command line when one is present
        var echoed = command.Trim();
        if (kept.Count > 0 && echoed.Length > 0)
        {
            var first = kept[0].Trim();
            if (first.StartsWith("$ ", StringComparison.Ordinal)) first = first[2..].Trim();
            if (first == echoed) kept.RemoveAt(0);
        }
        return string.Join('\n', kept).Trim();
    }

    /// <summary>
    /// Kills the shell and what it spawned. Group-isolated shells (setsid) are taken down
    /// with one kill(-pgid) — exact and race-free. Otherwise only the shell itself is
    /// killed: Process.Kill(entireProcessTree) snapshots /proc and can catch unrelated
    /// processes via pid reuse, so it is never used here.
    /// </summary>
    private static void KillTree(TerminalSession session)
    {
        try
        {
            if (session.Process.HasExited) return;
            if (session.GroupIsolated && OperatingSystem.IsLinux())
            {
                kill(-session.Process.Id, SigKill); // the shell's whole group, nothing else
            }
            else
            {
                session.Process.Kill();
            }
        }
        catch
        {
            // best-effort teardown
        }
    }

    /// <summary>What Ctrl+C would do: interrupt the commands the shell is currently
    /// running (its direct children). SIGINT first; children that ignore it (the ignore
    /// disposition is inherited when the harness itself was started as a background job —
    /// POSIX forbids the shell from resetting it) get SIGTERM shortly after.</summary>
    private static void InterruptChildren(TerminalSession session)
    {
        var children = DirectChildren(session.Process.Id);
        foreach (var child in children) kill(child, SigInt);
        if (children.Count == 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(200).ConfigureAwait(false);
            foreach (var child in children)
            {
                try
                {
                    if (Directory.Exists($"/proc/{child}")) kill(child, SigTerm);
                }
                catch { /* raced with exit */ }
            }
        });
    }

    /// <summary>Direct children of a pid from /proc (Linux); empty elsewhere.</summary>
    private static IReadOnlyList<int> DirectChildren(int parentPid)
    {
        if (!OperatingSystem.IsLinux()) return [];
        try
        {
            var children = new List<int>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir.AsSpan());
                if (name.Length == 0 || !char.IsAsciiDigit(name[0])) continue;
                var statPid = int.Parse(name);
                if (statPid == parentPid) continue;
                try
                {
                    // field 4 of /proc/<pid>/stat is the parent pid; comm (field 2) may
                    // contain spaces, so parse after the closing paren.
                    var text = File.ReadAllText(Path.Combine(dir, "stat"));
                    var close = text.LastIndexOf(')');
                    if (close < 0) continue;
                    var fields = text[(close + 2)..].Split(' ');
                    if (fields.Length >= 2 && int.Parse(fields[1]) == parentPid)
                        children.Add(statPid);
                }
                catch
                {
                    // the process exited between the enumeration and the read
                }
            }
            return children;
        }
        catch
        {
            return [];
        }
    }

    internal sealed class TerminalSession(string id, string ownerAgentId, string? name, Process process, bool groupIsolated)
    {
        public string Id { get; } = id;
        public string OwnerAgentId { get; } = ownerAgentId;
        public string? Name { get; } = name;
        public Process Process { get; } = process;
        public bool GroupIsolated { get; } = groupIsolated;
        internal readonly StringBuilder Buffer = new();
        internal readonly object Gate = new();
        internal int Mark;
        private int _seq;

        public int Pid
        {
            get { try { return Process.Id; } catch { return -1; } }
        }

        public bool Running
        {
            get { try { return !Process.HasExited; } catch { return false; } }
        }

        internal int NextSeq() => Interlocked.Increment(ref _seq);
    }
}

internal static class TerminalToolCommon
{
    internal static Agent RequireAgent(ToolRunContext exec)
        => exec.Agent ?? throw new ToolException("NO_AGENT", "terminal tools require an owning agent");
}

public sealed record TerminalOpenArgs(string Type, string? Name = null, string? Cwd = null);

public sealed record TerminalOpenOutput(string SessionId, string? Name, int Pid);

/// <summary>terminal_open: start a persistent shell session owned by the calling agent.</summary>
public sealed class TerminalOpenTool(TerminalService terminals) : ToolDefinition<TerminalOpenArgs, TerminalOpenOutput>
{
    public override string Name => "terminal_open";

    public override string Description =>
        "Open a persistent interactive shell session whose state (cwd, variables, background "
        + "jobs) survives across terminal_send calls. No PTY: interactive TUI programs are not supported.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["type"] = JsonSchema.String("Kind of terminal to open.", values: [JsonSerializer.SerializeToElement("shell")]),
            ["name"] = JsonSchema.String("Optional human-friendly name for the session."),
            ["cwd"] = JsonSchema.String("Working directory. Defaults to the session workspace."),
        },
        required: ["type"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["name"] = JsonSchema.String(),
            ["pid"] = JsonSchema.Integer(),
        },
        required: ["sessionId", "pid"]);

    protected override Task<TerminalOpenOutput> ExecuteTyped(TerminalOpenArgs args, ToolRunContext exec)
    {
        if (args.Type != "shell")
            throw new ToolException("INVALID_ARGS", $"unsupported terminal type '{args.Type}'");
        var agent = TerminalToolCommon.RequireAgent(exec);
        var id = terminals.Open(agent, args.Name, args.Cwd);
        var info = terminals.Info(agent, id);
        return Task.FromResult(new TerminalOpenOutput(info.SessionId, info.Name, info.Pid));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TerminalOpenArgs args, TerminalOpenOutput output)
        => [new TextBlock($"Opened terminal session {output.SessionId} (pid {output.Pid}).")];

    protected override ToolCallView? PresentCallTyped(TerminalOpenArgs args) => new()
    {
        Card = "terminal",
        Kind = "execute",
        Title = args.Name is { Length: > 0 } ? $"open {args.Name}" : "open shell",
    };
}

public sealed record TerminalListArgs();

public sealed record TerminalListOutput(IReadOnlyList<TerminalSessionInfo> Sessions);

/// <summary>terminal_list: the calling agent's open shell sessions.</summary>
public sealed class TerminalListTool(TerminalService terminals) : ToolDefinition<TerminalListArgs, TerminalListOutput>
{
    public override string Name => "terminal_list";

    public override string Description =>
        "List the persistent terminal sessions owned by this agent, with their ids, names, and liveness.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessions"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["sessionId"] = JsonSchema.String(),
                    ["name"] = JsonSchema.String(),
                    ["pid"] = JsonSchema.Integer(),
                    ["running"] = JsonSchema.Boolean(),
                },
                Required = ["sessionId", "pid", "running"],
                AdditionalProperties = false,
            }),
        },
        required: ["sessions"]);

    protected override bool IsConcurrencySafeTyped(TerminalListArgs args) => true;

    protected override Task<TerminalListOutput> ExecuteTyped(TerminalListArgs args, ToolRunContext exec)
        => Task.FromResult(new TerminalListOutput(terminals.List(TerminalToolCommon.RequireAgent(exec))));

    protected override IReadOnlyList<ContentBlock> RenderTyped(TerminalListArgs args, TerminalListOutput output)
    {
        if (output.Sessions.Count == 0) return [new TextBlock("No open terminal sessions.")];
        var builder = new StringBuilder();
        foreach (var session in output.Sessions)
        {
            builder.Append(session.SessionId);
            if (session.Name is { Length: > 0 }) builder.Append(" (").Append(session.Name).Append(')');
            builder.Append(session.Running ? " running" : " exited").AppendLine();
        }
        return [new TextBlock(builder.ToString().TrimEnd())];
    }
}

public sealed record TerminalReadArgs(
    [property: JsonPropertyName("session_id")] string SessionId,
    int Offset = 0,
    int Count = 500);

public sealed record TerminalReadOutput(string SessionId, string Text, int TotalLines);

/// <summary>terminal_read: a tail window over a session's captured output.</summary>
public sealed class TerminalReadTool(TerminalService terminals) : ToolDefinition<TerminalReadArgs, TerminalReadOutput>
{
    public override string Name => "terminal_read";

    public override string Description =>
        "Read captured output (stdout and stderr, prompts included) from a terminal session. "
        + "Returns up to count lines ending at the newest line; offset skips further back from the end.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("The terminal session to read."),
            ["offset"] = JsonSchema.Integer("Lines to skip back from the newest line. Defaults to 0."),
            ["count"] = JsonSchema.Integer("Maximum lines to return. Defaults to 500."),
        },
        required: ["session_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["text"] = JsonSchema.String(),
            ["totalLines"] = JsonSchema.Integer(),
        },
        required: ["sessionId", "text", "totalLines"]);

    protected override bool IsConcurrencySafeTyped(TerminalReadArgs args) => true;

    protected override Task<TerminalReadOutput> ExecuteTyped(TerminalReadArgs args, ToolRunContext exec)
    {
        var agent = TerminalToolCommon.RequireAgent(exec);
        var buffer = terminals.Read(agent, args.SessionId);
        var lines = buffer.Replace("\r\n", "\n").Split('\n');
        var count = args.Count is > 0 ? args.Count : 500;
        var skip = Math.Max(0, lines.Length - count - Math.Max(0, args.Offset));
        var window = string.Join('\n', lines.Skip(skip).Take(count));
        return Task.FromResult(new TerminalReadOutput(args.SessionId, window, lines.Length));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TerminalReadArgs args, TerminalReadOutput output)
        => [new TextBlock(output.Text.Length > 0 ? output.Text : "(no output)")];

    protected override ToolResultView? PresentResultTyped(TerminalReadArgs args, ToolExecutionResult result)
        => new() { Card = "terminal", Title = args.SessionId, Text = result.Content.OfType<TextBlock>().FirstOrDefault()?.Text };
}

public sealed record TerminalSendArgs(
    [property: JsonPropertyName("session_id")] string SessionId,
    string Text,
    bool Submit = true);

public sealed record TerminalSendOutput(string SessionId, string Output);

/// <summary>terminal_send: run a command in a persistent session and return the new output.</summary>
public sealed class TerminalSendTool(TerminalService terminals) : ToolDefinition<TerminalSendArgs, TerminalSendOutput>
{
    public override string Name => "terminal_send";

    public override string Description =>
        "Send text to a persistent terminal session. With submit (the default) the text runs as a "
        + "command and the output produced since the previous send is returned. State (cwd, "
        + "variables) persists between calls. Not concurrency-safe: sends are serialized by the scheduler.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("The terminal session to send to."),
            ["text"] = JsonSchema.String("Text or command to send."),
            ["submit"] = JsonSchema.Boolean("Press enter after the text. Defaults to true."),
        },
        required: ["session_id", "text"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["output"] = JsonSchema.String(),
        },
        required: ["sessionId", "output"]);

    protected override async Task<TerminalSendOutput> ExecuteTyped(TerminalSendArgs args, ToolRunContext exec)
    {
        var agent = TerminalToolCommon.RequireAgent(exec);
        if (!args.Submit)
        {
            terminals.Write(agent, args.SessionId, args.Text);
            return new TerminalSendOutput(args.SessionId, "");
        }
        var output = await terminals.SendAsync(agent, args.SessionId, args.Text).ConfigureAwait(false);
        return new TerminalSendOutput(args.SessionId, output);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TerminalSendArgs args, TerminalSendOutput output)
        => [new TextBlock(output.Output.Length > 0 ? output.Output : "(no output)")];

    protected override ToolCallView? PresentCallTyped(TerminalSendArgs args) => new()
    {
        Card = "terminal",
        Kind = "execute",
        Title = args.Text,
    };

    protected override ToolResultView? PresentResultTyped(TerminalSendArgs args, ToolExecutionResult result)
        => new() { Card = "terminal", Title = args.Text, Text = result.Content.OfType<TextBlock>().FirstOrDefault()?.Text };
}

public sealed record TerminalSignalArgs(
    [property: JsonPropertyName("session_id")] string SessionId,
    string Signal);

public sealed record TerminalSignalOutput(string SessionId, string Signal);

/// <summary>terminal_signal: interrupt or kill a session's shell (approximate POSIX delivery).</summary>
public sealed class TerminalSignalTool(TerminalService terminals) : ToolDefinition<TerminalSignalArgs, TerminalSignalOutput>
{
    public override string Name => "terminal_signal";

    public override string Description =>
        "Deliver a signal to a terminal session's shell. SIGINT interrupts the foreground command, "
        + "SIGTERM terminates the shell, SIGKILL kills the whole process tree. Delivery is approximate "
        + "(no PTY, no raw POSIX signals): SIGINT writes the interrupt byte to stdin.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("The terminal session to signal."),
            ["signal"] = JsonSchema.String("The signal to deliver.",
                values:
                [
                    JsonSerializer.SerializeToElement("SIGINT"),
                    JsonSerializer.SerializeToElement("SIGTERM"),
                    JsonSerializer.SerializeToElement("SIGKILL"),
                ]),
        },
        required: ["session_id", "signal"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["signal"] = JsonSchema.String(),
        },
        required: ["sessionId", "signal"]);

    protected override Task<TerminalSignalOutput> ExecuteTyped(TerminalSignalArgs args, ToolRunContext exec)
    {
        var agent = TerminalToolCommon.RequireAgent(exec);
        terminals.Signal(agent, args.SessionId, args.Signal);
        return Task.FromResult(new TerminalSignalOutput(args.SessionId, args.Signal));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TerminalSignalArgs args, TerminalSignalOutput output)
        => [new TextBlock($"Sent {output.Signal} to terminal session {output.SessionId}.")];

    protected override ToolCallView? PresentCallTyped(TerminalSignalArgs args) => new()
    {
        Card = "terminal",
        Kind = "execute",
        Title = $"{args.Signal} {args.SessionId}",
    };
}

public sealed record TerminalCloseArgs([property: JsonPropertyName("session_id")] string SessionId);

public sealed record TerminalCloseOutput(string SessionId, bool Closed);

/// <summary>terminal_close: shut a persistent session down and release its shell.</summary>
public sealed class TerminalCloseTool(TerminalService terminals) : ToolDefinition<TerminalCloseArgs, TerminalCloseOutput>
{
    public override string Name => "terminal_close";

    public override string Description =>
        "Close a terminal session: the shell and its children are killed and the session id becomes invalid.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("The terminal session to close."),
        },
        required: ["session_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["closed"] = JsonSchema.Boolean(),
        },
        required: ["sessionId", "closed"]);

    protected override Task<TerminalCloseOutput> ExecuteTyped(TerminalCloseArgs args, ToolRunContext exec)
    {
        var agent = TerminalToolCommon.RequireAgent(exec);
        terminals.Close(agent, args.SessionId);
        return Task.FromResult(new TerminalCloseOutput(args.SessionId, true));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(TerminalCloseArgs args, TerminalCloseOutput output)
        => [new TextBlock($"Closed terminal session {output.SessionId}.")];
}

/// <summary>Mounts the persistent-terminal tool family (terminal_open/list/read/send/signal/close).</summary>
public sealed class TerminalPlugin(TerminalService? service = null) : HarnessPlugin
{
    public override string Name => "terminals";
    public override string[] Inject { get; } = ["tools"];

    public TerminalService Service { get; } = service ?? new TerminalService();

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        ctx.Provide(TerminalService.ServiceKey, Service);
        var tools = ctx.Get<ToolRuntime>("tools");
        ctx.Effect(tools.Register(new TerminalOpenTool(Service)).Dispose);
        ctx.Effect(tools.Register(new TerminalListTool(Service)).Dispose);
        ctx.Effect(tools.Register(new TerminalReadTool(Service)).Dispose);
        ctx.Effect(tools.Register(new TerminalSendTool(Service)).Dispose);
        ctx.Effect(tools.Register(new TerminalSignalTool(Service)).Dispose);
        ctx.Effect(tools.Register(new TerminalCloseTool(Service)).Dispose);
        ctx.Effect(Service.Dispose);
        return Task.CompletedTask;
    }
}
