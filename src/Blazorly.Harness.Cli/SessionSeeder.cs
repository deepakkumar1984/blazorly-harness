using System.Diagnostics;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Persistence;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Cli;

/// <summary>
/// `blazorly sessions seed` — generates a synthetic long session (valid event stream: turns,
/// steps, streamed chunks, tool call/result pairs) straight into a persistence backend, then
/// measures the cold-load path: disk load, crash repair, session rehydrate, transcript fold,
/// stats fold, and model-history derivation. Load-tests scale without touching a provider.
/// </summary>
public static class SessionSeeder
{
    public static async Task<int> RunAsync(string[] args)
    {
        var turns = 1000;
        var persistenceKind = "sqlite";
        var home = Path.Combine(Path.GetTempPath(), "blazorly-seed-" + Guid.NewGuid().ToString("N")[..8]);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--turns" when i + 1 < args.Length && int.TryParse(args[++i], out var t):
                    turns = t;
                    break;
                case "--persistence" when i + 1 < args.Length && args[i + 1] is "sqlite" or "jsonl":
                    persistenceKind = args[++i];
                    break;
                case "--home" when i + 1 < args.Length:
                    home = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"unknown seed flag '{args[i]}' — usage: sessions seed [--turns N] [--persistence sqlite|jsonl] [--home PATH]");
                    return 1;
            }
        }
        if (turns is < 1 or > 100_000)
        {
            Console.Error.WriteLine("--turns must be between 1 and 100000");
            return 1;
        }

        Directory.CreateDirectory(home);
        Console.WriteLine($"seed: {turns} turns → {persistenceKind} under {home}");

        // ---- generate a valid event stream (Session validates every append) ------------------
        var header = new SessionHeader
        {
            Id = "seed-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x"),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Cwd = Directory.GetCurrentDirectory(),
        };
        var session = new Session(header);
        var events = new List<SessionEvent>(turns * 10);
        var random = new Random(42);
        var sw = Stopwatch.StartNew();
        for (var turn = 1; turn <= turns; turn++)
        {
            session.Append(SessionEventTypes.UserMessage, Message.CreateUserText(Brief(random)),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
            session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(turn));
            session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(turn, 1));

            var prose = Prose(random, 3);
            foreach (var part in prose) session.Append(SessionEventTypes.AssistantChunk,
                new SessionPayloads.AssistantChunk(turn, 1, new TextDeltaChunk(0, part)));
            session.Append(SessionEventTypes.AssistantMessage, new SessionPayloads.AssistantMessage(
                turn, 1,
                Message.CreateAssistant("deepseek", "deepseek-v4-flash", [new TextBlock(string.Concat(prose))]),
                new TokenUsage(InputTokens: 1200 + random.Next(400), OutputTokens: 300 + random.Next(150),
                    CacheReadTokens: 24000 + random.Next(4000))),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));

            var callId = $"call-{turn}";
            var callSeq = session.Seq;
            session.Append(SessionEventTypes.ToolCall, new SessionPayloads.ToolCall(
                turn, 1, callId, "bash", $$"""{"command":"pytest -q tests/","timeout":120}"""));
            session.Append(SessionEventTypes.ToolResult, new SessionPayloads.ToolResult(
                turn, 1, Message.CreateToolResult(callId, [new TextBlock(LogTail(random))])),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append(), SourceEventSeqs: [callSeq]));

            session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(turn, 1));
            session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(turn, new TurnEndReason.Completed()));
        }
        events.AddRange(session.Events);
        sw.Stop();
        Console.WriteLine($"generate+validate: {events.Count:N0} events in {sw.ElapsedMilliseconds} ms");

        // ---- persist in one batch (the same path Fork uses) --------------------------------
        ISessionPersistence writer = persistenceKind == "jsonl"
            ? new JsonlSessionPersistence(Path.Combine(home, "sessions"))
            : new SqliteSessionPersistence(Path.Combine(home, "sessions.db"));
        sw.Restart();
        await writer.CreateAsync(header).ConfigureAwait(false);
        await writer.AppendAsync(header.Id, events).ConfigureAwait(false);
        sw.Stop();
        var storeBytes = persistenceKind == "jsonl"
            ? new FileInfo(Directory.EnumerateFiles(Path.Combine(home, "sessions"), "session.jsonl", SearchOption.AllDirectories).Single()).Length
            : new FileInfo(Path.Combine(home, "sessions.db")).Length + (File.Exists(Path.Combine(home, "sessions.db-wal")) ? new FileInfo(Path.Combine(home, "sessions.db-wal")).Length : 0);
        Console.WriteLine($"persist ({persistenceKind}): {storeBytes / 1024 / 1024.0:F1} MB in {sw.ElapsedMilliseconds} ms");

        // ---- cold load path, exactly what OpenAsync pays -----------------------------------
        ISessionPersistence reader = persistenceKind == "jsonl"
            ? new JsonlSessionPersistence(Path.Combine(home, "sessions"))
            : new SqliteSessionPersistence(Path.Combine(home, "sessions.db"));

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        var (parsedHeader, parsedEvents) = await reader.LoadAsync(header.Id).ConfigureAwait(false);
        var loadMs = sw.ElapsedMilliseconds;
        var loadAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        allocated = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        var repaired = SessionRepair.Repair(parsedEvents);
        var rehydrated = new Session(parsedHeader, repaired);
        var rehydrateMs = sw.ElapsedMilliseconds;
        var rehydrateAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        // transcript fold with the real assembler (ToolRuntime over a bare context; no meter)
        var ctx = HarnessContext.CreateRoot();
        var prompt = Core.SystemPrompt.SystemPromptService.Mount(ctx);
        var tools = Core.Tools.ToolRuntime.Mount(ctx, prompt);
        var assembler = new ConversationAssembler(tools);
        var folder = assembler.CreateFolder(rehydrated);
        allocated = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        var snapshot = folder.Update(agent: null);
        var foldMs = sw.ElapsedMilliseconds;
        var foldAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        var projections = new SessionProjectionService(null!);
        allocated = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        var stats = projections.Stats(rehydrated);
        var statsMs = sw.ElapsedMilliseconds;
        var statsAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        allocated = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        var modelMessages = rehydrated.DeriveMessages();
        var deriveMs = sw.ElapsedMilliseconds;
        var deriveAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        Console.WriteLine();
        Console.WriteLine($"cold load          {loadMs,6} ms   {loadAllocated / 1024 / 1024.0,8:F1} MB allocated");
        Console.WriteLine($"repair+rehydrate   {rehydrateMs,6} ms   {rehydrateAllocated / 1024 / 1024.0,8:F1} MB allocated");
        Console.WriteLine($"transcript fold    {foldMs,6} ms   {foldAllocated / 1024 / 1024.0,8:F1} MB allocated");
        Console.WriteLine($"stats fold         {statsMs,6} ms   {statsAllocated / 1024 / 1024.0,8:F1} MB allocated");
        Console.WriteLine($"derive messages    {deriveMs,6} ms   {deriveAllocated / 1024 / 1024.0,8:F1} MB allocated  ({modelMessages.Count:N0} messages)");
        Console.WriteLine();
        Console.WriteLine($"events {parsedEvents.Count:N0} · nodes {snapshot.Nodes.Count:N0} · turns {stats.Turns:N0} · tool calls {stats.Tools.Sum(t => t.Count):N0}");
        return parsedEvents.Count == events.Count && stats.Turns == turns ? 0 : 1;
    }

    private static string Brief(Random random)
        => $"Turn task {random.Next(1000)}: " + string.Join(' ', Words(random, 60));

    private static string[] Prose(Random random, int chunks)
    {
        var parts = new string[chunks];
        for (var i = 0; i < chunks; i++) parts[i] = string.Join(' ', Words(random, 90)) + "\n\n";
        return parts;
    }

    private static string LogTail(Random random)
        => string.Join('\n', Enumerable.Range(0, 110).Select(n => $"tests/test_module_{random.Next(40):D2}.py::{Words(random, 4).First()}_{n} PASSED in 0.{random.Next(10):D1}s"));

    private static IEnumerable<string> Words(Random random, int count)
    {
        string[] bank = ["refactor", "harness", "session", "surface", "compaction", "trajectory", "fold", "stream", "guard", "sandbox", "context", "token", "circuit", "virtualize", "persist", "replay", "splice", "boundary", "repair", "snapshot"];
        for (var i = 0; i < count; i++) yield return bank[random.Next(bank.Length)];
    }
}

/// <summary>`blazorly sessions import` — one-time JSONL → SQLite migration for a harness home.</summary>
public static class SessionCommands
{
    public static async Task<int> ImportAsync(string[] args)
    {
        var home = Environment.GetEnvironmentVariable("BLAZORLY_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--home" && i + 1 < args.Length) home = args[++i];
        }
        var jsonlRoot = Path.Combine(home, "sessions");
        if (!Directory.Exists(jsonlRoot))
        {
            Console.Error.WriteLine($"no jsonl sessions under {jsonlRoot} — nothing to import");
            return 1;
        }
        var report = await PersistenceMigrator.ImportAsync(
            new JsonlSessionPersistence(jsonlRoot),
            new SqliteSessionPersistence(Path.Combine(home, "sessions.db")),
            message => Console.WriteLine(message)).ConfigureAwait(false);
        Console.WriteLine($"import done: {report.Imported} imported, {report.Skipped} already present, {report.Failed} failed");
        return report.Failed == 0 ? 0 : 1;
    }
}
