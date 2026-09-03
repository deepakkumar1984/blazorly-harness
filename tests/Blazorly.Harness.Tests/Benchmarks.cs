using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Persistence;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

// Interruption-first benchmarks: cancel-propagation latency per interruption phase,
// log replay + projection cost vs. session size, and FTS backfill throughput.
// Run the suite:  dotnet test --filter Category=Benchmark
// Results land in <repo>/benchmarks/results-<timestamp>/ (results.json + summary.md)
// and are also printed to the test console. Every benchmark doubles as a correctness
// assertion — the numbers are only meaningful if the contract they measure holds.

public static class BenchStats
{
    public static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    public static double P95(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
    }

    public static string Spread(List<double> values)
        => $"median {Median(values):F1} ms · p95 {P95(values):F1} ms · min {values.Min():F1} · max {values.Max():F1} (n={values.Count})";
}

public sealed class BenchReport : IDisposable
{
    public sealed record Row(string Group, string Name, string Detail);
    private readonly List<Row> _rows = [];
    private readonly string _dir;

    public BenchReport()
    {
        var root = RepoRoot();
        var baseDir = root is null ? Path.GetTempPath() : Path.Combine(root, "benchmarks");
        _dir = Environment.GetEnvironmentVariable("BLAZORLY_BENCH_OUT")
            ?? Path.Combine(baseDir, "results-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_dir);
    }

    public void Add(string group, string name, string detail)
    {
        _rows.Add(new Row(group, name, detail));
        Console.WriteLine($"[bench] {group} | {name} | {detail}");
    }

    public void Dispose()
    {
        var payload = JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, rows = _rows },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_dir, "results.json"), payload);
        var builder = new StringBuilder();
        builder.AppendLine("# Benchmark results");
        builder.AppendLine();
        builder.AppendLine($"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} (local).");
        builder.AppendLine();
        builder.AppendLine("| Group | Metric | Result |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var row in _rows)
            builder.AppendLine($"| {row.Group} | {row.Name} | {row.Detail} |");
        File.WriteAllText(Path.Combine(_dir, "summary.md"), builder.ToString());
        Console.WriteLine($"[bench] report written to {_dir}");
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "tasks")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}

/// <summary>Cancel propagation, in-process scripted adapter (raw OCE → true aborted path).</summary>
[Trait("Category", "Benchmark")]
public class CancelLatencyBenchmarks
{
    private static async Task<SessionEvent> AwaitEventAsync(Core.Sessions.Session session, string type, int afterSeq, int timeoutSeconds)
    {
        var tcs = new TaskCompletionSource<SessionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = session.Subscribe(e =>
        {
            if (e.Type == type && e.Seq > afterSeq) tcs.TrySetResult(e);
        });
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        Assert.True(winner == tcs.Task, $"timed out waiting for a durable '{type}' after seq {afterSeq}");
        return tcs.Task.Result;
    }

    [Fact]
    public async Task Cancel_DuringToolPhase_AbortsFastAndDurable()
    {
        using var report = new BenchReport();
        await using var harness = Blazorly.Harness.Tests.TestHarness.Create(script: ToolThenSummary);
        harness.ScriptedLlm.ChunkDelayMs = 5;
        var workspace = Directory.CreateTempSubdirectory("blazorly-bench-tool").FullName;
        var agent = harness.CreateAgent(cwd: workspace);
        var runs = new List<double>();
        const int n = 10;
        for (var i = 0; i < n; i++)
        {
            var startSeq = agent.Session.Events.Count > 0 ? agent.Session.Events[^1].Seq : -1;
            agent.Followup(Llm.Message.CreateUserText($"run {i}"));
            await AwaitEventAsync(agent.Session, SessionEventTypes.ToolCall, startSeq, 20);
            var sw = Stopwatch.StartNew();
            agent.Cancel(AgentCancelCause.User());
            var turnEnd = await AwaitEventAsync(agent.Session, SessionEventTypes.TurnEnd, startSeq, 30);
            sw.Stop();
            runs.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal("aborted", turnEnd.Data.GetProperty("reason").GetProperty("kind").GetString());
            await agent.WhenIdleAsync();
        }
        // The contract these numbers ride on: every cancel durably closed its pending tool
        // (bash aborts report a structured result, not an exception) and the log holds n
        // complete aborted turns.
        var calls = agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.ToolCall)
            .Select(SessionEventRead.ToolCallOf)
            .Select(c => c.CallId)
            .ToHashSet();
        var answered = agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.ToolResult)
            .Select(SessionEventRead.ToolResultOf)
            .Select(r => r.Message.Content.OfType<ToolResultBlock>().FirstOrDefault()?.ToolCallId)
            .Where(id => id is not null)
            .ToHashSet()!;
        Assert.Subset(answered, calls);
        report.Add("cancel-tool-phase", "cancel → durable turn/end (aborted)", BenchStats.Spread(runs));
    }

    [Fact]
    public async Task Cancel_MidStream_CommitsPartialWorkAndAborts()
    {
        using var report = new BenchReport();
        await using var harness = Blazorly.Harness.Tests.TestHarness.Create(script: _ => SlowTextStream());
        harness.ScriptedLlm.ChunkDelayMs = 20;
        var workspace = Directory.CreateTempSubdirectory("blazorly-bench-stream").FullName;
        var agent = harness.CreateAgent(cwd: workspace);
        var runs = new List<double>();
        const int n = 5;
        for (var i = 0; i < n; i++)
        {
            var startSeq = agent.Session.Events.Count > 0 ? agent.Session.Events[^1].Seq : -1;
            agent.Followup(Llm.Message.CreateUserText($"summarize {i}"));
            await AwaitEventAsync(agent.Session, SessionEventTypes.AssistantChunk, startSeq, 20);
            await Task.Delay(250); // settle into the stream
            var sw = Stopwatch.StartNew();
            agent.Cancel(AgentCancelCause.User());
            var turnEnd = await AwaitEventAsync(agent.Session, SessionEventTypes.TurnEnd, startSeq, 30);
            sw.Stop();
            runs.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal("aborted", turnEnd.Data.GetProperty("reason").GetProperty("kind").GetString());
            await agent.WhenIdleAsync();
        }
        var interrupted = agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.AssistantMessage)
            .Select(SessionEventRead.AssistantMessageOf)
            .Count(a => a.Interrupted == true && a.Message.Content.OfType<TextBlock>().Any());
        Assert.Equal(n, interrupted); // partial work is preserved, not discarded
        report.Add("cancel-mid-stream", "cancel → durable turn/end (aborted, partial message committed)", BenchStats.Spread(runs));
    }

    private static IReadOnlyList<StreamChunk> ToolThenSummary(GenerateOptions options)
    {
        var lastUser = options.Messages.LastOrDefault(m =>
            m.Role == "user" && m.Content.OfType<TextBlock>().Any() && m.Content.All(b => b is not ToolResultBlock));
        if (lastUser?.FlattenText().StartsWith("summarize", StringComparison.Ordinal) == true)
            return Scripted.Text("The summary.");
        return Scripted.ToolCalls(("bash", new { command = "sleep 30", description = "hold" }));
    }

    private static IReadOnlyList<StreamChunk> SlowTextStream()
    {
        var chunks = new List<StreamChunk> { new BlockStartChunk(0, "text") };
        for (var i = 0; i < 100; i++) chunks.Add(new TextDeltaChunk(0, $"word{i} "));
        chunks.Add(new BlockEndChunk(0, new TextBlock("done")));
        chunks.Add(new FinishChunk(FinishReason.Stop));
        return chunks;
    }
}

/// <summary>Cancel mid-SSE over a real HTTP adapter: the documented asymmetry — the abort
/// surfaces as turn/end error (code ABORTED), not aborted. Latency still matters: this is
/// the user-visible stop path for every OpenAI-compatible route.</summary>
[Trait("Category", "Benchmark")]
[Collection("BlazorlyHome")]
public class HttpCancelLatencyBenchmarks : BootstrapperTestBase
{
    [Fact]
    public async Task Cancel_MidSse_HttpAdapter_MapsToErrorFinishFast()
    {
        using var report = new BenchReport();
        using var server = new FakeOpenAiServer(chunkDelayMs: 150);
        ScriptedSettings.WriteFakeRoute(Home, server.BaseUrl);
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var facade = new SessionFacade(boot, new UiEventBroker());
            var session = facade.CreateSession();
            var agent = facade.EnsureAgent(session);
            var runs = new List<double>();
            var kinds = new List<string>();
            const int n = 5;
            for (var i = 0; i < n; i++)
            {
                var startSeq = session.Events.Count > 0 ? session.Events[^1].Seq : -1;
                // Subscribe before the prompt: a fast first chunk must not beat the listener.
                var turnEnded = new TaskCompletionSource<SessionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                var chunkSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var turnSub = session.Subscribe(e =>
                {
                    if (e.Type == SessionEventTypes.TurnEnd && e.Seq > startSeq) turnEnded.TrySetResult(e);
                });
                using var chunkSub = session.Subscribe(e =>
                {
                    if (e.Type == SessionEventTypes.AssistantChunk && e.Seq > startSeq) chunkSeen.TrySetResult();
                });
                await facade.PromptAsync(session.Id, $"run the scripted task {i}", "queue");
                var sawChunk = await Task.WhenAny(chunkSeen.Task, Task.Delay(TimeSpan.FromSeconds(20)));
                Assert.True(sawChunk == chunkSeen.Task, "no assistant chunk within 20s");
                await Task.Delay(30); // inside the 3-event tool-call SSE stream (150 ms pacing)
                var sw = Stopwatch.StartNew();
                facade.Cancel(session.Id);
                var winner = await Task.WhenAny(turnEnded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                sw.Stop();
                Assert.True(winner == turnEnded.Task, "turn/end not written within 30s of cancel");
                var turnEnd = turnEnded.Task.Result;
                var kind = turnEnd.Data.GetProperty("reason").GetProperty("kind").GetString();
                // Under host load the cancel can slip past the short SSE stream into the bash
                // tool: mid-SSE → error (code ABORTED, the adapter asymmetry), mid-tool →
                // aborted. Both are valid durable outcomes; the latency is the measurement.
                Assert.True(kind is "error" or "aborted", $"unexpected turn/end kind '{kind}'");
                kinds.Add(kind!);
                runs.Add(sw.Elapsed.TotalMilliseconds);
                await agent.WhenIdleAsync();
            }
            report.Add("cancel-mid-sse-http", "cancel → durable turn/end (error/ABORTED mid-SSE; aborted mid-tool)",
                BenchStats.Spread(runs) + $" · kinds seen: {string.Join(",", kinds.Distinct())}");
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }
}

/// <summary>Log replay, projection, and FTS backfill cost vs. session size — synthetic
/// sessions at 1K/5K/20K/100K events plus the largest real session under ~/.blazorly.</summary>
[Trait("Category", "Benchmark")]
public class ReplayFtsBenchmarks : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-bench-replay-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Replay_Projection_Fts_CostVsSessionSize()
    {
        using var report = new BenchReport();
        foreach (var size in new[] { 1_000, 5_000, 20_000, 100_000 })
            await MeasureAsync(report, $"synthetic-{size}", "session-bench" + size, Generate(size));
        var real = LargestRealSession();
        if (real is null)
        {
            report.Add("real-session", "skipped", "no persisted session under ~/.blazorly/sessions");
        }
        else
        {
            var (id, file, events) = real.Value;
            await MeasureAsync(report, $"real-{events}", id, file);
        }
    }

    private async Task MeasureAsync(BenchReport report, string group, string sessionId, string? preparedLog = null)
    {
        var file = preparedLog ?? Path.Combine(_root, "bench", sessionId, "session.jsonl");
        await using var ctx = HarnessContext.CreateRoot();
        var persistenceRoot = Path.Combine(_root, "sessions-" + sessionId);
        var store = SessionStore.Mount(ctx, new JsonlSessionPersistence(persistenceRoot));
        // Place the log where the persistence root can find it (one project dir, escaped id).
        var target = Path.Combine(persistenceRoot, "bench", Uri.EscapeDataString(sessionId), "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);

        var sw = Stopwatch.StartNew();
        var session = await store.OpenAsync(sessionId);
        sw.Stop();
        report.Add(group, "replay: OpenAsync (load + validate + repair-check)", $"{sw.Elapsed.TotalMilliseconds:F1} ms for {session.Events.Count} events");

        sw.Restart();
        var messages = session.DeriveMessages();
        sw.Stop();
        report.Add(group, "surface projection: DeriveMessages", $"{sw.Elapsed.TotalMilliseconds:F1} ms → {messages.Count} model-visible messages");

        var projections = new SessionProjectionService(store);
        sw.Restart();
        var (stats, through) = await projections.ProjectAsync(sessionId, "stats");
        sw.Stop();
        report.Add(group, "fold: stats (cold)", $"{sw.Elapsed.TotalMilliseconds:F1} ms through {through} events (turns {stats.GetProperty("turns").GetInt32()})");
        sw.Restart();
        await projections.ProjectAsync(sessionId, "stats");
        sw.Stop();
        report.Add(group, "fold: stats (warm, count-keyed cache)", $"{sw.Elapsed.TotalMilliseconds:F2} ms");

        using (var index = new SessionSearchIndex(Path.Combine(_root, $"fts-{sessionId}.db")))
        {
            sw.Restart();
            await index.SyncSessionAsync(sessionId, session.Events);
            sw.Stop();
            var perSecond = session.Events.Count / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            report.Add(group, "FTS5 backfill: SyncSessionAsync", $"{sw.Elapsed.TotalMilliseconds:F1} ms for {session.Events.Count} events ({perSecond:F0} events/s)");
            sw.Restart();
            var hits = await index.SearchAsync("task", sessionId);
            sw.Stop();
            report.Add(group, "FTS5 query: phrase search", $"{sw.Elapsed.TotalMilliseconds:F1} ms → {hits.Count} hits");
        }
    }

    /// <summary>Writes a replay-valid JSONL log of ~target events: alternating tool/no-tool
    /// turns with realistic block shapes, closed cleanly (no repair needed). Payloads are
    /// serialized from anonymous objects through the session camelCase options, so the
    /// shapes can never drift from the wire format.</summary>
    private string Generate(int target)
    {
        var sessionId = "session-bench" + target;
        var dir = Path.Combine(_root, "bench", sessionId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.jsonl");
        var builder = new StringBuilder();
        builder.Append(SessionJson.ToElement(new { type = "session", version = 1, id = sessionId, createdAt = 1L, cwd = _root })).Append('\n');
        var seq = 0;
        long time = 1_700_000_000_000;
        var turn = 0;
        void Emit(string type, object data, bool surface = false)
        {
            builder.Append("{\"type\":\"").Append(type)
                .Append("\",\"seq\":").Append(seq)
                .Append(",\"time\":").Append(time)
                .Append(",\"data\":").Append(SessionJson.ToElement(data));
            if (surface) builder.Append(",\"surfaceOp\":\"append\"");
            builder.Append("}\n");
            seq++;
            time += 3;
        }
        while (seq < target)
        {
            turn++;
            Emit("user/message", new
            {
                id = $"msg_u_{turn}",
                role = "user",
                content = new object[] { new { type = "text", text = $"task {turn}: analyze the batch" } },
                source = new { kind = "user" },
            }, surface: true);
            Emit("turn/start", new { turn });
            Emit("request/context", new { provider = "bench", model = "bench", contextWindow = 65536 });
            Emit("step/start", new { turn, step = 1 });
            for (var c = 0; c < 6; c++)
                Emit("assistant/chunk", new { turn, step = 1, chunk = new { type = "text-delta", text = $"chunk {c} of turn {turn} " } });
            Emit("assistant/message", new
            {
                turn,
                step = 1,
                message = new
                {
                    id = $"msg_a_{turn}",
                    role = "assistant",
                    content = new object[] { new { type = "text", text = $"analysis for task {turn}" } },
                    source = new { kind = "model", provider = "bench", model = "bench" },
                },
            }, surface: true);
            if (turn % 2 == 0)
            {
                var callId = $"call_{turn}";
                Emit("tool/call", new
                {
                    turn,
                    step = 1,
                    callId,
                    name = "bash",
                    arguments = JsonSerializer.Serialize(new { command = $"echo task {turn}" }),
                });
                Emit("tool/result", new
                {
                    turn,
                    step = 1,
                    message = new
                    {
                        id = $"msg_t_{turn}",
                        role = "user",
                        content = new object[] { new { type = "tool-result", toolCallId = callId, content = new object[] { new { type = "text", text = $"output for task {turn}" } } } },
                        source = new { kind = "tool", callId },
                    },
                }, surface: true);
            }
            Emit("step/end", new { turn, step = 1 });
            Emit("turn/end", new { turn, reason = new { kind = "completed" } });
        }
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private (string Id, string File, int Events)? LargestRealSession()
    {
        var sessionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly", "sessions");
        if (!Directory.Exists(sessionsRoot)) return null;
        var candidates = Directory.EnumerateFiles(sessionsRoot, "session.jsonl", SearchOption.AllDirectories)
            .Select(log => (File: log, Lines: File.ReadLines(log).Count()))
            .OrderByDescending(x => x.Lines)
            .ToList();
        if (candidates.Count == 0 || candidates[0].Lines < 1_000) return null;
        var id = new DirectoryInfo(Path.GetDirectoryName(candidates[0].File)!).Name;
        return (Uri.UnescapeDataString(id), candidates[0].File, candidates[0].Lines - 1);
    }
}
