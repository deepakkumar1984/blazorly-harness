using System.Diagnostics;
using System.Text;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Jobs;

public sealed record JobInfo(string Id, string Kind, string Description, string Status, int? ExitCode, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>
/// ctx.jobs — the background-work registry. Producers (background bash, terminals, subagents)
/// register running work; the job_* tools read, list, and kill it; completion notices reach the
/// owning agent's inbox as injected context (picked up at the next step boundary).
/// </summary>
public sealed class JobsRuntime
{
    public const string ServiceKey = "jobs";

    private sealed class Job : IAsyncDisposable
    {
        public required string Id;
        public required string Kind;
        public required string Description;
        public required DateTimeOffset StartedAt;
        public string Status = "running";
        public int? ExitCode;
        public DateTimeOffset? EndedAt;
        public readonly StringBuilder Output = new();
        public readonly object OutputGate = new();
        public Process? Process;
        public WindowsProcessJob? ProcessJob;
        public string? OwnerSessionId;
        public CancellationTokenSource? Kill;

        public async ValueTask DisposeAsync()
        {
            Kill?.Cancel();
            ProcessJob?.Dispose();
            if (Process is not null)
            {
                try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
                Process.Dispose();
            }
        }
    }

    private readonly HarnessContext _ctx;
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _counter;

    public JobsRuntime(HarnessContext ctx) => _ctx = ctx;

    public static JobsRuntime Mount(HarnessContext ctx)
    {
        var service = new JobsRuntime(ctx);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    /// <summary>Starts a process as a background job and returns immediately with its job id.</summary>
    public string StartProcess(string kind, string description, ProcessStartInfo startInfo, Agent.Agent? owner = null)
    {
        var job = new Job
        {
            Id = $"job_{Interlocked.Increment(ref _counter)}",
            Kind = kind,
            Description = description,
            StartedAt = DateTimeOffset.UtcNow,
            OwnerSessionId = owner?.Id,
        };
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        job.Process = process;
        job.Kill = new CancellationTokenSource();
        lock (_gate) _jobs[job.Id] = job;

        try
        {
            job.ProcessJob = WindowsProcessJob.Create();
            process.Exited += (_, _) => Settle(job, process.ExitCode, owner);
            process.Start();
            job.ProcessJob?.Assign(process);
        }
        catch
        {
            job.ProcessJob?.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
            job.Kill.Dispose();
            lock (_gate) _jobs.Remove(job.Id);
            throw;
        }
        _ = PumpAsync(process, job);
        return job.Id;
    }

    private async Task PumpAsync(Process process, Job job)
    {
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(job.Kill!.Token);
            var stderr = process.StandardError.ReadToEndAsync(job.Kill.Token);
            var out1 = await stdout.ConfigureAwait(false);
            var err1 = await stderr.ConfigureAwait(false);
            lock (job.OutputGate)
            {
                if (out1.Length > 0) job.Output.Append(out1);
                if (err1.Length > 0) job.Output.Append(err1);
            }
        }
        catch
        {
            // drained or killed; whatever was read stays buffered
        }
    }

    private void Settle(Job job, int exitCode, Agent.Agent? owner)
    {
        job.ExitCode = exitCode;
        job.Status = "done";
        job.EndedAt = DateTimeOffset.UtcNow;
        if (owner is not null)
        {
            // Injected context: waits in the inbox until the next wake (dsh semantics).
            owner.Inject(Message.CreateUserText(
                $"Background job {job.Id} ({job.Description}) finished with exit code {exitCode}."));
        }
        _ = _ctx.Events.EmitAsync("jobs/changed", job.Id);
    }

    public IReadOnlyList<JobInfo> List()
    {
        lock (_gate)
        {
            return [.. _jobs.Values
                .OrderBy(j => j.StartedAt)
                .Select(j => new JobInfo(j.Id, j.Kind, j.Description, j.Status, j.ExitCode, j.StartedAt, j.EndedAt))];
        }
    }

    public JobInfo? Get(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job)) return null;
            return new JobInfo(job.Id, job.Kind, job.Description, job.Status, job.ExitCode, job.StartedAt, job.EndedAt);
        }
    }

    public string? ReadOutput(string id, int tailChars = 8000)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job)) return null;
        }
        Job target;
        lock (_gate) _jobs.TryGetValue(id, out target!);
        lock (target.OutputGate)
        {
            var text = target.Output.ToString();
            return text.Length <= tailChars ? text : text[^tailChars..];
        }
    }

    public bool KillJob(string id)
    {
        Job? job;
        lock (_gate) _jobs.TryGetValue(id, out job);
        if (job is null) return false;
        job.Kill?.Cancel();
        try
        {
            if (job.ProcessJob is not null) job.ProcessJob.Terminate();
            else if (job.Process is { HasExited: false } p) p.Kill(entireProcessTree: true);
        }
        catch { }
        return true;
    }

    public async Task StopForSessionsAsync(IReadOnlySet<string> sessionIds, CancellationToken ct = default)
    {
        Job[] jobs;
        lock (_gate) jobs = _jobs.Values.Where(j => j.OwnerSessionId is { } id && sessionIds.Contains(id)).ToArray();
        foreach (var job in jobs)
        {
            if (job.Process is not { } process) continue;
            if (job.ProcessJob is not null) job.ProcessJob.Terminate();
            else if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (job.ProcessJob is not null) await job.ProcessJob.WaitForEmptyAsync(ct).ConfigureAwait(false);
            job.Kill?.Cancel();
        }
    }
}
