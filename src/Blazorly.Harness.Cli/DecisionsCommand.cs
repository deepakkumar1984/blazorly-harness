using System.Text.Json;
using Blazorly.Harness.Core.Decisions;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Cli;

/// <summary>
/// <c>blazorly decisions</c> — inspect and exercise the System One decision seam without running a
/// session. <c>doctor</c> reports what the harness resolved from settings; <c>probe</c> makes one
/// real call and prints the raw request and reply, which is how the wire format gets verified
/// against a live key before any seam is trusted.
/// </summary>
public static class DecisionsCommand
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Re-indents a JSON payload for reading; passes non-JSON through untouched.</summary>
    private static string Print(string json)
    {
        try { return JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json), Pretty); }
        catch (JsonException) { return json; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args.Length > 0 ? args[0] : "doctor";
        return verb switch
        {
            "doctor" => Doctor(),
            "probe" => await ProbeAsync(args[1..]).ConfigureAwait(false),
            "--help" or "-h" or "help" => Help(),
            _ => Unknown(verb),
        };
    }

    private static int Doctor()
    {
        var settings = EvalSandbox.LoadAmbient();
        var key = settings.ResolveSystemOneApiKey();
        Console.WriteLine("System One decision layer");
        Console.WriteLine($"  enabled          {YesNo(settings.EnableSystemOne)}");
        Console.WriteLine($"  api key          {(key is { Length: > 0 } ? Mask(key) + (settings.SystemOneApiKey is { Length: > 0 } ? " (settings.json)" : $" (env {settings.SystemOneApiKeyEnv})") : "not set")}");
        Console.WriteLine($"  ready            {YesNo(settings.SystemOneReady)}");
        Console.WriteLine($"  endpoint         {settings.SystemOneBaseUrl.TrimEnd('/')}{settings.SystemOnePath}");
        Console.WriteLine($"  model            {(string.IsNullOrWhiteSpace(settings.SystemOneModel) ? "(service default)" : settings.SystemOneModel)}");
        Console.WriteLine($"  auth             {settings.SystemOneAuthStyle}");
        Console.WriteLine($"  timeout          {settings.SystemOneTimeoutMs}ms");
        Console.WriteLine($"  seams            {(settings.SystemOneSeams.Count == 0 ? "*" : string.Join(", ", settings.SystemOneSeams))}");
        var unwired = settings.SystemOneSeams.Where(s => s != "*" && !DecisionSeams.IsWired(s)).ToList();
        if (unwired.Count > 0)
            Console.WriteLine($"  not wired yet    {string.Join(", ", unwired)} (accepted in settings, but nothing consumes them)");
        Console.WriteLine();
        Console.WriteLine("Seam consumers");
        Console.WriteLine($"  auto-plan        {(settings.EnableSystemOne && settings.EnableAutoPlan && settings.EnablePlanMode ? $"on (engage ≥ {settings.AutoPlanEngageAt:P0}, skip ≤ {settings.AutoPlanSkipAt:P0}, else heuristic {settings.AutoPlanThreshold}/100)" : "off — heuristic scorer only")}");
        Console.WriteLine($"  risk-gate        {(settings.EnableRiskGate ? $"on (park at P(risky) ≥ {settings.RiskGateThreshold:P0})" : "off — session permission preset only")}");
        Console.WriteLine();
        if (!settings.SystemOneReady)
        {
            Console.WriteLine("Not ready: every seam falls back to its deterministic logic, with no");
            Console.WriteLine("network call and nothing new in the session log. To turn it on:");
            Console.WriteLine("  set enableSystemOne + systemOneApiKey in ~/.blazorly/settings.json");
            Console.WriteLine($"  or export {settings.SystemOneApiKeyEnv}=… and set enableSystemOne");
            return settings.EnableSystemOne ? 1 : 0;
        }
        Console.WriteLine("Run `blazorly decisions probe` to verify the endpoint and wire format.");
        return 0;
    }

    private static async Task<int> ProbeAsync(string[] args)
    {
        string? seam = null, text = null;
        var asJson = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seam" when i + 1 < args.Length: seam = args[++i]; break;
                case "--text" when i + 1 < args.Length: text = args[++i]; break;
                case "--json": asJson = true; break;
            }
        }

        var settings = EvalSandbox.LoadAmbient();
        if (settings.ResolveSystemOneApiKey() is not { Length: > 0 } key)
        {
            Console.Error.WriteLine("no System One API key: set systemOneApiKey in settings.json "
                + $"or export {settings.SystemOneApiKeyEnv}");
            return 1;
        }

        var (state, questions) = Sample(seam ?? DecisionSeams.AutoPlan, text);
        if (DecisionSeams.Planned.Contains(seam ?? "", StringComparer.Ordinal))
            Console.Error.WriteLine($"note: '{seam}' is a declared-but-unwired seam — this exercises the wire shape only.");
        using var client = new SystemOneClient(HarnessBootstrapper.SystemOneOptionsOf(settings, key));
        Console.Error.WriteLine($"POST {settings.SystemOneBaseUrl.TrimEnd('/')}{settings.SystemOnePath} "
            + $"({questions.Count} question(s), {state.Chars} state chars)");

        string request;
        int status;
        string response;
        try
        {
            (request, status, response) = await client.ProbeAsync(state, questions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"request failed: {ex.Message}");
            return 1;
        }

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status, request = Parse(request), response = Parse(response) }, Pretty));
            return status is >= 200 and < 300 ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine("--- request ---");
        Console.WriteLine(Print(request));
        Console.WriteLine();
        Console.WriteLine($"--- response (HTTP {status}) ---");
        Console.WriteLine(Print(response));
        Console.WriteLine();

        // Now show what the adapter made of it — the part that decides whether a seam can trust it.
        var result = await client.DecideAsync(state, questions).ConfigureAwait(false);
        Console.WriteLine("--- parsed by the harness adapter ---");
        if (result is null)
        {
            Console.WriteLine("no result (the client returned no opinion)");
        }
        else
        {
            Console.WriteLine($"impl {result.Impl} · latency {result.LatencyMs}ms · degraded {YesNo(result.Degraded)}"
                + (result.Error is { Length: > 0 } ? $" · error: {result.Error}" : ""));
            if (result.Answers.Count == 0) Console.WriteLine("no answers recognised — see ADAPT notes in SystemOneClient.cs");
            foreach (var (questionKey, answer) in result.Answers)
            {
                Console.WriteLine($"  {questionKey} [{answer.Kind}] " + answer.Kind switch
                {
                    DecisionAnswer.NoulKind => $"P(yes) = {answer.Probability:0.###}",
                    DecisionAnswer.ChoiceKind => $"= {answer.Value}"
                        + (answer.Options is { Count: > 0 }
                            ? "  {" + string.Join(", ", answer.Options.Select(kv => $"{kv.Key}: {kv.Value:0.###}")) + "}"
                            : ""),
                    DecisionAnswer.RatingKind => $"= {answer.Score:0.###}",
                    _ => "?",
                });
            }
        }
        return status is >= 200 and < 300 && result?.Answers.Count > 0 ? 0 : 1;
    }

    /// <summary>A representative state + question set per seam, so a probe exercises the real shape.</summary>
    private static (DecisionState State, IReadOnlyList<DecisionQuestion> Questions) Sample(string seam, string? text)
    {
        switch (seam)
        {
            case DecisionSeams.RiskGate:
                var risky = new DecisionState()
                    .Add("tool", "bash")
                    .Add("kind", "execute")
                    .Add("arguments", text ?? """{"command":"git push --force origin main && rm -rf ./dist"}""")
                    .Add("cwd", Directory.GetCurrentDirectory())
                    .Add("sandboxMode", "workspace-write")
                    .Add("recentTools", "read → grep → edit → bash");
                return (risky, [new Noul(RiskGatePlugin.RiskKey,
                    "Could this action destroy data, leak secrets, or cause other harm that cannot be undone "
                    + "by re-running or reverting? Answer yes only for irreversible or outward-facing effects.")]);

            case DecisionSeams.Loop:
                var loop = new DecisionState()
                    .Add("recentToolCalls", text ?? "read a.ts → grep TODO → read a.ts → read b.ts → read a.ts")
                    .Add("goal", "port the auth module to the new session API");
                return (loop, [new Rating("progress", "Is the agent making forward progress toward the goal?", 0, 10)]);

            default:
                var plan = new DecisionState()
                    .Add("brief", text ?? """
                        Refactor the session persistence layer so JSONL and SQLite share one writer.
                        First audit both backends, then extract the common interface, then migrate
                        each caller across, and finally add coverage for the fork path.
                        """)
                    .Add("cwd", Directory.GetCurrentDirectory());
                return (plan,
                [
                    new Noul("needs_plan",
                        "Does this request need investigation and an approved plan before anything is changed? "
                        + "Answer yes for multi-file, multi-step or design-shaped work; no for a question, "
                        + "a lookup, or a single small edit whose scope is already obvious."),
                    new Choice("shape", "What kind of work is this?", ["question", "small-edit", "multi-file", "design"]),
                    new Rating("complexity", "How complex is this request?", 0, 100),
                ]);
        }
    }

    private static object Parse(string json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return json; }
    }

    private static string Mask(string key)
        => key.Length <= 8 ? "****" : key[..4] + "…" + key[^4..];

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"unknown decisions subcommand '{verb}'");
        return Help();
    }

    private static int Help()
    {
        Console.WriteLine("""
            blazorly decisions — inspect the System One decision layer

            Subcommands:
              doctor                     Show the resolved configuration and which seams are live.
              probe                      Make one real call and print the raw request, the raw reply
                                         and what the harness adapter parsed out of it.

            Probe flags:
              --seam <name>              auto-plan (default) | risk-gate | loop
                                         (loop is declared but not wired into the agent loop yet;
                                         probing it exercises the request/reply shape only)
              --text "<…>"              Override the sample state (the brief, or the tool arguments).
              --json                     Emit one JSON object instead of the readable report.

            Exit codes: 0 healthy · 1 not configured, request failed, or no answer was recognised.

            With System One off, or on but keyless, every seam falls back to the deterministic
            logic it already used — the harness behaves exactly as it did before.
            """);
        return 0;
    }
}
