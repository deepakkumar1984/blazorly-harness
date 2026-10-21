using System.Diagnostics;
using System.Text;
using Blazorly.Harness.Core.Jobs;

namespace Blazorly.Harness.Web.Services;

public sealed record RunSnapshot(
    string Id, string Label, string CommandLine, string WorkingDirectory, bool Running,
    int? ExitCode, string Output, DateTimeOffset StartedAt, int ProcessId = 0, bool StopRequested = false);

/// <summary>Owns project processes independently of browser panels and keeps bounded recent output.</summary>
public sealed class RunSupervisor : IDisposable
{
    private const int MaxOutput = 200_000;
    private const int CompletedHistory = 30;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _runs = new(StringComparer.Ordinal);
    private readonly List<string> _deletingRoots = [];
    private int _next;
    private bool _disposed;

    public RunSnapshot Start(ProjectCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.CommandLine)) throw new InvalidOperationException("Empty command.");
        if (!Directory.Exists(command.WorkingDirectory)) throw new InvalidOperationException("The working directory is missing.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_deletingRoots.Any(root => WorkspaceFiles.IsInside(root, command.WorkingDirectory)))
                throw new InvalidOperationException("This workspace is being deleted.");
            var existing = _runs.Values.FirstOrDefault(s => s.Command.Id == command.Id
                && WorkspaceFiles.SameRoot(s.Command.WorkingDirectory, command.WorkingDirectory) && s.Snapshot().Running);
            if (existing is not null) return existing.Snapshot();

            foreach (var old in _runs.Values.Where(s => !s.Snapshot().Running).OrderByDescending(s => s.StartedAt).Skip(CompletedHistory).ToArray())
            {
                _runs.Remove(old.Id);
                old.Dispose();
            }
            var process = new Process { StartInfo = BuildStartInfo(command) };
            WindowsProcessJob? processJob = null;
            try
            {
                processJob = WindowsProcessJob.Create();
                if (!process.Start()) throw new InvalidOperationException($"Failed to start {command.CommandLine}.");
                processJob?.Assign(process);
            }
            catch
            {
                processJob?.Dispose();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.Dispose();
                throw;
            }
            var id = $"run-{++_next}";
            var slot = new Slot(id, command, process, processJob);
            _runs[id] = slot;
            slot.Begin();
            return slot.Snapshot();
        }
    }

    public void Stop(string id)
    {
        Slot? slot;
        lock (_gate) _runs.TryGetValue(id, out slot);
        slot?.Kill();
    }

    public RunSnapshot? Get(string id)
    {
        lock (_gate) return _runs.TryGetValue(id, out var slot) ? slot.Snapshot() : null;
    }

    public async Task StopWorkspaceAsync(string root, CancellationToken ct = default)
    {
        Slot[] slots;
        lock (_gate)
        {
            _deletingRoots.Add(WorkspaceFiles.NormalizeRoot(root));
            slots = _runs.Values.Where(s => WorkspaceFiles.IsInside(root, s.Command.WorkingDirectory)).ToArray();
        }
        foreach (var slot in slots) slot.Kill();
        await Task.WhenAll(slots.Select(s => s.Completion)).WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    public void FinishWorkspaceDeletion(string root, bool deleted)
    {
        lock (_gate)
        {
            _deletingRoots.RemoveAll(r => WorkspaceFiles.SameRoot(r, root));
            if (!deleted) return;
            foreach (var slot in _runs.Values.Where(s => WorkspaceFiles.IsInside(root, s.Command.WorkingDirectory)).ToArray())
            {
                _runs.Remove(slot.Id);
                slot.Dispose();
            }
        }
    }

    public IReadOnlyList<RunSnapshot> ForRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return [];
        lock (_gate)
            return _runs.Values.Where(s => WorkspaceFiles.IsInside(root, s.Command.WorkingDirectory))
                .OrderByDescending(s => s.StartedAt).Select(s => s.Snapshot()).ToList();
    }

    public void Dispose()
    {
        Slot[] slots;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            slots = _runs.Values.ToArray();
            _runs.Clear();
        }
        foreach (var slot in slots)
        {
            try { slot.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        try { Task.WhenAll(slots.Select(s => s.Completion)).Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        foreach (var slot in slots) slot.Dispose();
    }

    private static ProcessStartInfo BuildStartInfo(ProjectCommand command)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        if (command.Executable is { Length: > 0 } executable
            && !(OperatingSystem.IsWindows() && ProjectCommandDiscovery.UsesWindowsShim(executable)))
        {
            psi.FileName = executable;
            foreach (var argument in command.Arguments ?? []) psi.ArgumentList.Add(argument);
        }
        else if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            // cmd uses its own quoting rules; ArgumentList's C-runtime escaping breaks
            // quoted project/script names. Discovered shim arguments exclude cmd metacharacters.
            psi.Arguments = "/d /s /c \"" + command.CommandLine + "\"";
        }
        else
        {
            psi.FileName = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command.CommandLine);
        }
        return psi;
    }

    private sealed class Slot : IDisposable
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();
        private readonly Process _process;
        private readonly WindowsProcessJob? _processJob;
        private readonly int _pid;
        private int? _exitCode;
        private bool _running = true;
        private bool _stopRequested;
        private int _disposed;

        public Slot(string id, ProjectCommand command, Process process, WindowsProcessJob? processJob)
        {
            Id = id;
            Command = command;
            _process = process;
            _processJob = processJob;
            _pid = process.Id;
        }
        public string Id { get; }
        public ProjectCommand Command { get; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Begin()
        {
            // Run is a non-interactive output panel. Commands that need input use Terminal.
            _process.StandardInput.Close();
            Completion = Observe();
        }

        private async Task Observe()
        {
            var stdout = Pump(_process.StandardOutput);
            var stderr = Pump(_process.StandardError);
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                lock (_gate) _exitCode = _process.ExitCode;
                if (_processJob is not null)
                {
                    _processJob.Terminate();
                    await _processJob.WaitForEmptyAsync().ConfigureAwait(false);
                }
                // A detached child can inherit the pipes after its parent exits. Bound
                // the drain so the UI still reports the parent process's actual state.
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException
                or System.ComponentModel.Win32Exception or TimeoutException)
            {
                Append("\n" + ex.Message);
            }
            finally
            {
                lock (_gate) _running = false;
                Dispose();
            }
        }

        private async Task Pump(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    Append(new string(buffer, 0, count));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        private void Append(string text)
        {
            lock (_gate)
            {
                _output.Append(text);
                if (_output.Length > MaxOutput) _output.Remove(0, _output.Length - MaxOutput);
            }
        }

        public void Kill()
        {
            lock (_gate)
            {
                if (!_running) return;
                try
                {
                    if (_processJob is not null) _processJob.Terminate();
                    else if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    else return;
                    _stopRequested = true;
                }
                catch (InvalidOperationException) { /* exited while checking */ }
            }
        }

        public RunSnapshot Snapshot()
        {
            lock (_gate) return new(Id, Command.Label, Command.CommandLine, Command.WorkingDirectory,
                _running, _exitCode, _output.ToString(), StartedAt, _pid, _stopRequested);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _processJob?.Dispose();
            _process.Dispose();
        }
    }
}
