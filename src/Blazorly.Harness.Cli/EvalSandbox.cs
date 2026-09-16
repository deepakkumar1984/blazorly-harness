using System.Text.Json;
using Blazorly.Harness.Tools;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Cli;

/// <summary>
/// The execution-backend axis of an eval run. A score is only comparable across runs when the
/// backend is named and pinned: the same task trace can pass under Landlock and fail unconfined
/// (or the reverse) without the agent loop changing at all. Backends are materialized into the
/// seeded eval home rather than inherited from the developer's ambient settings.
/// </summary>
public static class EvalSandbox
{
    /// <summary>Local Landlock confinement (Linux only): workspace-write, fail closed if unavailable.</summary>
    public const string Landlock = "landlock";

    /// <summary>Remote E2B sandbox execution; requires an API key.</summary>
    public const string E2b = "e2b";

    /// <summary>No confinement: tools run directly on the host (danger-full-access).</summary>
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [Landlock, E2b, None];

    /// <summary>Null (no explicit choice) is valid: the runner resolves the default backend.</summary>
    public static bool IsKnown(string? value)
        => value is null || All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// The default when neither the CLI nor the task names a backend: confined where the host can
    /// confine, unconfined where it cannot. Recorded in the manifest either way, so a score is
    /// never silently measured against a different backend than a previous run.
    /// </summary>
    public static string Default => SandboxPolicy.ConfinementSupported ? Landlock : None;

    /// <summary>Parses a comma-separated <c>--sandbox</c> matrix; rejects unknown backends.</summary>
    public static IReadOnlyList<string> ParseMatrix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var backends = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var backend in backends)
        {
            if (!All.Contains(backend, StringComparer.Ordinal))
                throw new EvalLoadException("--sandbox", $"unknown sandbox backend '{backend}' (expected one of: {string.Join(", ", All)})");
        }
        return backends.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A deterministic behavioral baseline for evals: the documented defaults, not whatever the
    /// developer happens to have configured. Credentials, provider routes and model discovery are
    /// inherited separately (see <see cref="InheritRoutes"/>), because those select *which* model
    /// answers rather than *how* the harness behaves.
    /// </summary>
    public static HarnessSettings PinnedSettings(string backend)
    {
        var settings = new HarnessSettings
        {
            // Pinned so a score change cannot come from context pressure or compaction tuning.
            Persistence = "jsonl",
            ContextWindowTokens = 65_536,
            CompactionThreshold = 0.72,
            CompactionPrunerChars = 4_000,
            SpillThresholdChars = 20_000,
            // Pinned plugin set: the capability surface is part of what is being measured.
            EnableTeams = false,
            EnableWorkflows = true,
            EnableTerminals = true,
            EnableLsp = false,
            EnableHooks = true,
            EnableCodeMode = true,
            EnableWeb = true,
            EnableSkills = true,
            EnableGoals = true,
            EnablePlanMode = true,
            EnableAutoPlan = true,
            EnableAskUser = true,
            EnableSessionQuery = true,
            EnableProjectInstructions = true,
            EnableTime = true,
            EnableTmux = true,
            EnableAutoTitles = false,
            EnableSpill = true,
            EnableSchedule = true,
            EnableMcp = false,
            // Third-party plugins would change the tool set under measurement.
            PluginDirs = [],
            DisabledPlugins = [],
            // Evals are a measurement, not a workload to report on.
            TelemetryEnabled = false,
            WebSearchBackend = "duckduckgo",
        };
        ApplyBackend(settings, backend);
        return settings;
    }

    /// <summary>Maps a backend onto the settings keys that select it.</summary>
    public static void ApplyBackend(HarnessSettings settings, string backend)
    {
        switch (backend)
        {
            case Landlock:
                settings.SandboxMode = SandboxPolicy.WorkspaceWrite;
                // Fail closed: a run measured as "landlock" must actually be confined.
                settings.SandboxFailClosedWhenUnsupported = true;
                settings.EnableE2b = false;
                break;
            case E2b:
                // E2B executes remotely, so the local shell is not the confinement boundary.
                settings.SandboxMode = SandboxPolicy.DangerFullAccess;
                settings.SandboxFailClosedWhenUnsupported = false;
                settings.EnableE2b = true;
                break;
            case None:
                settings.SandboxMode = SandboxPolicy.DangerFullAccess;
                settings.SandboxFailClosedWhenUnsupported = false;
                settings.EnableE2b = false;
                break;
            default:
                throw new EvalLoadException(backend, $"unknown sandbox backend (expected one of: {string.Join(", ", All)})");
        }
    }

    /// <summary>
    /// Copies only route/credential fields from the ambient settings onto a pinned baseline, so
    /// evals use the developer's providers without inheriting their behavioral configuration.
    /// </summary>
    public static HarnessSettings InheritRoutes(HarnessSettings pinned, HarnessSettings ambient)
    {
        pinned.Provider = ambient.Provider;
        pinned.Model = ambient.Model;
        pinned.BaseUrl = ambient.BaseUrl;
        pinned.ApiKey = ambient.ApiKey;
        pinned.ProviderKeys = ambient.ProviderKeys;
        pinned.DiscoveredModels = ambient.DiscoveredModels;
        pinned.CustomProviders = ambient.CustomProviders;
        pinned.Retry = ambient.Retry;
        pinned.E2bApiKey = ambient.E2bApiKey;
        pinned.E2bApiKeyEnv = ambient.E2bApiKeyEnv;
        pinned.E2bTemplate = ambient.E2bTemplate;
        pinned.E2bBaseUrl = ambient.E2bBaseUrl;
        pinned.TavilyApiKey = ambient.TavilyApiKey;
        pinned.TavilyApiKeyEnv = ambient.TavilyApiKeyEnv;
        pinned.BraveApiKey = ambient.BraveApiKey;
        pinned.BraveApiKeyEnv = ambient.BraveApiKeyEnv;
        return pinned;
    }

    /// <summary>
    /// Null when this backend can genuinely run here; otherwise the reason the row is recorded as
    /// skipped. Skips are reported, never counted as passes — a vacuous green is worse than a gap.
    /// </summary>
    public static string? UnavailableReason(string backend, HarnessSettings settings) => backend switch
    {
        Landlock when !SandboxPolicy.ConfinementSupported =>
            "confinement-unavailable: Landlock needs Linux plus a C compiler for landlock-exec",
        E2b when string.IsNullOrWhiteSpace(settings.ResolveE2bApiKey()) =>
            "e2b-not-configured: no E2B API key (settings.e2bApiKey or E2B_API_KEY)",
        _ => null,
    };

    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Writes the seeded home's settings.json for one backend.</summary>
    public static string WriteHome(string home, HarnessSettings settings)
    {
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings, SettingsJson));
        return path;
    }

    /// <summary>Loads ambient settings tolerantly: a missing or corrupt file yields defaults.</summary>
    public static HarnessSettings LoadAmbient(string? homeOverride = null)
    {
        var home = !string.IsNullOrWhiteSpace(homeOverride)
            ? homeOverride
            : Environment.GetEnvironmentVariable("BLAZORLY_HOME") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly");
        var path = Path.Combine(home, "settings.json");
        if (!File.Exists(path)) return new HarnessSettings();
        try
        {
            return JsonSerializer.Deserialize<HarnessSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? new HarnessSettings();
        }
        catch (JsonException)
        {
            return new HarnessSettings();
        }
    }

    /// <summary>Settings snapshot for the manifest with every credential masked.</summary>
    public static IReadOnlyDictionary<string, object?> RedactedSnapshot(HarnessSettings settings) => new Dictionary<string, object?>
    {
        ["provider"] = settings.Provider,
        ["model"] = settings.Model,
        ["baseUrl"] = settings.BaseUrl,
        ["sandboxMode"] = settings.SandboxMode,
        ["sandboxFailClosedWhenUnsupported"] = settings.SandboxFailClosedWhenUnsupported,
        ["enableE2b"] = settings.EnableE2b,
        ["e2bTemplate"] = settings.E2bTemplate,
        ["persistence"] = settings.Persistence,
        ["contextWindowTokens"] = settings.ContextWindowTokens,
        ["compactionThreshold"] = settings.CompactionThreshold,
        ["compactionPrunerChars"] = settings.CompactionPrunerChars,
        ["spillThresholdChars"] = settings.SpillThresholdChars,
        ["webSearchBackend"] = settings.WebSearchBackend,
        ["plugins"] = PluginToggles(settings),
        ["apiKey"] = Mask(settings.ApiKey),
        ["providerKeys"] = settings.ProviderKeys.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        ["customProviders"] = settings.CustomProviders.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
        ["e2bKeyPresent"] = !string.IsNullOrWhiteSpace(settings.ResolveE2bApiKey()),
    };

    private static IReadOnlyDictionary<string, bool> PluginToggles(HarnessSettings s) => new Dictionary<string, bool>
    {
        ["teams"] = s.EnableTeams, ["workflows"] = s.EnableWorkflows, ["terminals"] = s.EnableTerminals,
        ["lsp"] = s.EnableLsp, ["hooks"] = s.EnableHooks, ["codeMode"] = s.EnableCodeMode,
        ["web"] = s.EnableWeb, ["skills"] = s.EnableSkills, ["goals"] = s.EnableGoals,
        ["planMode"] = s.EnablePlanMode, ["autoPlan"] = s.EnableAutoPlan, ["askUser"] = s.EnableAskUser,
        ["sessionQuery"] = s.EnableSessionQuery, ["projectInstructions"] = s.EnableProjectInstructions,
        ["time"] = s.EnableTime, ["tmux"] = s.EnableTmux, ["autoTitles"] = s.EnableAutoTitles,
        ["spill"] = s.EnableSpill, ["schedule"] = s.EnableSchedule, ["mcp"] = s.EnableMcp,
    };

    private static string Mask(string? secret)
        => string.IsNullOrWhiteSpace(secret) ? "(unset)" : $"(set, {secret!.Length} chars)";
}
