using System.Diagnostics;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Tools;

/// <summary>
/// Injects a tmux overview (sessions, panes, running commands, pane directories) into the
/// runtime-context snapshot so the agent can reason about the user's terminal state. Fails
/// soft: no tmux binary or no server running contributes nothing. The listing is cached for
/// 30s so per-step prompt assembly never shells out hot.
/// </summary>
public sealed class TmuxContextPlugin : HarnessPlugin
{
    public override string Name => "tmux";
    public override string[] Inject { get; } = [Core.SystemPrompt.SystemPromptService.ServiceKey];

    private readonly TimeSpan _cacheFor;
    private readonly object _gate = new();
    private volatile string? _cached;
    private long _cachedAt;

    public TmuxContextPlugin(TimeSpan? cacheFor = null) => _cacheFor = cacheFor ?? TimeSpan.FromSeconds(30);

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var prompt = ctx.Get<Core.SystemPrompt.SystemPromptService>(Core.SystemPrompt.SystemPromptService.ServiceKey);
        var registration = prompt.RegisterContext("tmux", 12, _ => Snapshot());
        ctx.Effect(registration.Dispose);
        return Task.CompletedTask;
    }

    public string Snapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cached = _cached;
        if (cached is not null && now - Volatile.Read(ref _cachedAt) < _cacheFor.TotalMilliseconds) return cached;
        var listing = Listing();
        lock (_gate)
        {
            _cached = listing;
            _cachedAt = now;
        }
        return listing;
    }

    /// <summary>One line per pane: session:window.pane [command] path. Empty when tmux is absent.</summary>
    public static string Listing()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "tmux",
                Arguments = "list-panes -a -F '#{session_name}:#{window_index}.#{pane_index} [#{pane_current_command}] #{pane_current_path}'",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null) return "";
            if (!process.WaitForExit(3_000)) return "";
            if (process.ExitCode != 0) return ""; // no server / no tmux
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (output.Length == 0) return "";
            return "tmux sessions:\n" + output;
        }
        catch
        {
            return ""; // no tmux on PATH or spawn failure — contribute nothing
        }
    }
}
