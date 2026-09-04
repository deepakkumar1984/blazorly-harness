using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Persistence;
using Blazorly.Harness.Core.Jobs;
using Blazorly.Harness.Tools;
using Microsoft.Extensions.Options;

namespace Blazorly.Harness.Web.Services;

/// <summary>User-editable runtime settings persisted under the harness home.</summary>
public sealed class HarnessSettings
{
    public string Provider { get; set; } = "deepseek";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string? ApiKey { get; set; }
    /// <summary>API key stash per provider id so switching providers keeps each key typed once.</summary>
    public Dictionary<string, string> ProviderKeys { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Model ids loaded live from each provider's /models endpoint; replaces catalog seeds once present.</summary>
    public Dictionary<string, List<string>> DiscoveredModels { get; set; } = new(StringComparer.Ordinal);
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string WorkspaceRoot { get; set; } = Directory.GetCurrentDirectory();
    public string SandboxMode { get; set; } = SandboxPolicy.WorkspaceWrite;
    public string Persistence { get; set; } = "jsonl"; // jsonl | sqlite
    public long ContextWindowTokens { get; set; } = 65_536;
    public double CompactionThreshold { get; set; } = 0.72;
    public int CompactionPrunerChars { get; set; } = 4_000;
    public Blazorly.Harness.Core.Retry.RetryPolicyConfig Retry { get; set; } = new();
    public List<CustomProviderConfig> CustomProviders { get; set; } = [];
    public bool EnableTeams { get; set; }
    public bool EnableWorkflows { get; set; } = true;
    public bool EnableTerminals { get; set; } = true;
    public bool EnableLsp { get; set; }
    public bool EnableHooks { get; set; } = true;
    public bool EnableCodeMode { get; set; } = true;
    public bool EnableWeb { get; set; } = true;
    public bool EnableSkills { get; set; } = true;
    public bool EnableGoals { get; set; } = true;
    public bool EnablePlanMode { get; set; } = true;
    public bool EnableAutoPlan { get; set; } = true;
    /// <summary>Complexity total (0–100) at which auto-plan engages a fresh turn's brief.</summary>
    public int AutoPlanThreshold { get; set; } = Blazorly.Harness.Tools.AutoPlanPlugin.DefaultThreshold;
    public bool EnableAskUser { get; set; } = true;
    public bool EnableSessionQuery { get; set; } = true;
    public bool EnableProjectInstructions { get; set; } = true;
    public bool EnableTime { get; set; } = true;
    public bool EnableTmux { get; set; } = true;
    public bool EnableAutoTitles { get; set; } = true;
    public bool EnableSpill { get; set; } = true;
    public int SpillThresholdChars { get; set; } = 20_000;
    public bool EnableSchedule { get; set; } = true;
    public bool EnableMcp { get; set; } = true;

    /// <summary>Third-party plugin directories (each *.dll with IHarnessPlugin impls loads);
    /// empty means &lt;home&gt;/plugins. Restart to pick up changes.</summary>
    public List<string> PluginDirs { get; set; } = [];

    /// <summary>Plugin names to skip at boot (built-in, capability, or third-party).</summary>
    public List<string> DisabledPlugins { get; set; } = [];

    /// <summary>web_search backend: duckduckgo (keyless default), tavily, or brave.</summary>
    public string WebSearchBackend { get; set; } = "duckduckgo";
    public string? TavilyApiKey { get; set; }
    public string TavilyApiKeyEnv { get; set; } = "TAVILY_API_KEY";
    public string? BraveApiKey { get; set; }
    public string BraveApiKeyEnv { get; set; } = "BRAVE_API_KEY";

    /// <summary>Settings key first, then the configured environment variable.</summary>
    public string? ResolveTavilyApiKey()
        => !string.IsNullOrWhiteSpace(TavilyApiKey) ? TavilyApiKey : Environment.GetEnvironmentVariable(TavilyApiKeyEnv);

    /// <summary>Settings key first, then the configured environment variable.</summary>
    public string? ResolveBraveApiKey()
        => !string.IsNullOrWhiteSpace(BraveApiKey) ? BraveApiKey : Environment.GetEnvironmentVariable(BraveApiKeyEnv);

    /// <summary>Local-only usage aggregates (turns/tokens/tool calls); nothing leaves the machine.</summary>
    public bool TelemetryEnabled { get; set; } = true;

    /// <summary>Remote sandbox (E2B) execution; requires a key via settings or the environment.</summary>
    public bool EnableE2b { get; set; } = false;
    public string? E2bApiKey { get; set; }
    public string E2bApiKeyEnv { get; set; } = "E2B_API_KEY";
    public string E2bTemplate { get; set; } = "base";
    public string E2bBaseUrl { get; set; } = "https://api.e2b.app";

    /// <summary>Settings key first, then the configured environment variable.</summary>
    public string? ResolveE2bApiKey()
        => !string.IsNullOrWhiteSpace(E2bApiKey) ? E2bApiKey : Environment.GetEnvironmentVariable(E2bApiKeyEnv);

    /// <summary>Resolved per request, never persisted; never sends one provider's key to another provider's route.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveApiKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ApiKey)) return ApiKey;
            if (ProviderKeys.TryGetValue(Provider, out var stashed) && !string.IsNullOrWhiteSpace(stashed)) return stashed;
            var catalogEnv = ProviderCatalog.Info(Provider)?.ApiKeyEnv;
            if (!string.IsNullOrWhiteSpace(catalogEnv))
            {
                var fromCatalog = Environment.GetEnvironmentVariable(catalogEnv);
                if (!string.IsNullOrWhiteSpace(fromCatalog)) return fromCatalog;
            }
            var providerSpecific = Environment.GetEnvironmentVariable(
                $"{Provider.ToUpperInvariant().Replace('-', '_')}_API_KEY"); // DEEPSEEK/OPENAI/ANTHROPIC_API_KEY
            if (!string.IsNullOrWhiteSpace(providerSpecific)) return providerSpecific;
            // Documented legacy fallback for the OpenAI-compatible routes; custom/anthropic routes
            // must not inherit an unrelated provider's key.
            if (Provider is "deepseek" or "openai" or "openai-compatible")
            {
                var deepseek = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
                if (!string.IsNullOrWhiteSpace(deepseek)) return deepseek;
                return Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            }
            return null;
        }
    }
}

/// <summary>An extra OpenAI-compatible provider route configured from the Settings UI.</summary>
public sealed class CustomProviderConfig
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? ApiKeyEnv { get; set; }
    public List<string> Models { get; set; } = [];

    /// <summary>Comma-separated editor view of the model ids (the Settings page binds this).</summary>
    public string ModelsText
    {
        get => string.Join(", ", Models);
        set => Models = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}

public sealed class HarnessBootstrapper : IHostedService, IAsyncDisposable
{
    public HarnessContext Context { get; private set; } = null!;
    public AgentLoopService Loop { get; private set; } = null!;
    public SessionStore Sessions { get; private set; } = null!;
    public SessionProjectionService Projections { get; private set; } = null!;
    public SessionSearchIndex SearchIndex { get; private set; } = null!;
    public AgentRuntime Agents { get; private set; } = null!;
    public ToolRuntime Tools { get; private set; } = null!;
    public LlmRuntime Llm { get; private set; } = null!;
    public SandboxPolicy Sandbox { get; private set; } = null!;
    public WorkspaceRegistry Workspaces { get; private set; } = null!;
    public HarnessSettings Settings { get; private set; } = new();

    private readonly Dictionary<string, IDisposable> _routeEffects = new(StringComparer.Ordinal);
    private readonly string _home;

    /// <summary>One long-lived client for streaming adapter requests (no request-level timeout; the caller's token governs).</summary>
    internal static readonly HttpClient StreamingHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public HarnessBootstrapper()
    {
        // BLAZORLY_HOME isolates the whole harness home (settings, sessions, spills, …);
        // used by tests and by users who want a portable home.
        _home = Environment.GetEnvironmentVariable("BLAZORLY_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly");
        Directory.CreateDirectory(_home);
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => StartAsyncCore();

    /// <summary>
    /// Composition root: settings (+patches) load first, then the whole harness — spine
    /// services, capability plugins, third-party assemblies — boots through
    /// <see cref="PluginHost.ApplyAllAsync"/> in Inject order. Everything after the boot
    /// (provider routes, default selection, session reattach) is re-runnable state sync,
    /// not composition: ApplyProviderSelection also runs on every Settings save.
    /// </summary>
    public async Task StartAsyncCore()
    {
        LoadSettings();
        Context = HarnessContext.CreateRoot();
        Context.Events.OnListenerError = (_, ex) => Console.Error.WriteLine($"[harness] listener error: {ex.Message}");

        var plugins = BuildPluginList();
        var applied = new List<string>();
        await PluginHost.ApplyAllAsync(Context, plugins, applied).ConfigureAwait(false);
        AppliedPlugins = applied;
        // JobsRuntime is mounted by JobsPlugin itself (it self-mounts when absent).
        Jobs = Context.Get<JobsRuntime>(JobsRuntime.ServiceKey);

        ApplyProviderSelection();
        ApplyDefaultSelection();
        await ReattachPersistedSessionsAsync().ConfigureAwait(false);
    }

    /// <summary>Plugin names in the order they applied; useful diagnostics and tests.</summary>
    public IReadOnlyList<string> AppliedPlugins { get; private set; } = [];

    /// <summary>
    /// The boot composition: spine mounts (as <see cref="MountPlugin"/> adapters so load order
    /// derives from Inject keys), settings-gated capability plugins, then third-party assemblies.
    /// </summary>
    public List<IHarnessPlugin> BuildPluginList()
    {
        Core.Sessions.ISessionPersistence persistence = Settings.Persistence == "sqlite"
            ? new SqliteSessionPersistence(Path.Combine(_home, "sessions.db"))
            : new JsonlSessionPersistence(Path.Combine(_home, "sessions"));
        var tracker = new FsObservationTracker();
        Sandbox = new SandboxPolicy { DefaultMode = Settings.SandboxMode };
        SystemPromptService? prompt = null;

        var plugins = new List<IHarnessPlugin>
        {
            MountPlugin.Sync("llm", [], ctx => Llm = LlmRuntime.Mount(ctx)),
            MountPlugin.Sync("systemPrompt", [], ctx => prompt = SystemPromptService.Mount(ctx)),
            MountPlugin.Sync("tools", [SystemPromptService.ServiceKey],
                ctx => Tools = ToolRuntime.Mount(ctx, prompt!)),
            MountPlugin.Sync("sessions", [], ctx => Sessions = SessionStore.Mount(ctx, persistence)),
            MountPlugin.Sync("projections", [SessionStore.ServiceKey],
                ctx => Projections = SessionProjectionService.Mount(ctx, Sessions)),
            MountPlugin.Sync("search-index", [SessionStore.ServiceKey],
                ctx => SearchIndex = SessionSearchIndex.Mount(ctx, Sessions, Path.Combine(_home, "sessions-index.db"))),
            MountPlugin.Sync("agents", [], ctx => Agents = AgentRuntime.Mount(ctx)),
            MountPlugin.Sync("approval", [], ctx => Approval = ApprovalService.Mount(ctx)),
            MountPlugin.Sync("userQuestions", [], ctx => UserQuestions = UserQuestionsService.Mount(ctx)),
            MountPlugin.Sync("agentLoop",
                [AgentRuntime.ServiceKey, SessionStore.ServiceKey, LlmRuntime.ServiceKey, ToolRuntime.ServiceKey, SystemPromptService.ServiceKey],
                ctx =>
            {
                Loop = AgentLoopService.Mount(ctx);
                Loop.RegisterDefaultPrompt();
            }),
            new BuiltInToolsPlugin(tracker, Sandbox),
            MountPlugin.Sync("subagents", [], ctx => Subagents = Core.Subagents.SubagentService.Mount(ctx)),
            MountPlugin.Sync("toolPolicy", [], ctx => ToolPolicy = Core.Tools.ToolPolicyService.Mount(ctx)),
        };
        if (Settings.TelemetryEnabled)
        {
            plugins.Add(MountPlugin.Sync("telemetry", [], ctx =>
                Telemetry = Core.Telemetry.UsageTelemetryService.Mount(ctx, Path.Combine(_home, "telemetry.json"), enabled: true)));
        }
        // Order matters: compaction owns context-overflow recovery; the retry policy handles
        // everything else before the driver's built-in default.
        plugins.Add(MountPlugin.Sync("compaction", [], ctx =>
            Compaction = Core.Compaction.CompactionService.Mount(ctx, new Core.Compaction.CompactionOptions
            {
                ContextWindowTokens = Settings.ContextWindowTokens,
                Threshold = Settings.CompactionThreshold,
                PrunerChars = Settings.CompactionPrunerChars,
            })));
        plugins.Add(MountPlugin.Sync("llmRetry", [], ctx =>
            Retry = Core.Retry.RetryService.Mount(ctx, new Core.Retry.RetryOptions { Default = Settings.Retry })));
        plugins.Add(MountPlugin.Sync("credentials", [], ctx =>
            Credentials = Core.Credentials.CredentialsService.Mount(ctx, Path.Combine(_home, "credentials.json"))));
        plugins.Add(MountPlugin.Sync("attachments", [], ctx =>
            Attachments = Core.Attachments.AttachmentService.Mount(ctx, Path.Combine(_home, "attachments"))));
        if (Settings.EnableProjectInstructions)
        {
            plugins.Add(MountPlugin.Sync("projectInstructions", [], ctx =>
                Instructions = Core.Instructions.ProjectInstructionsService.Mount(ctx, _home)));
        }
        if (Settings.EnableTime) plugins.Add(new Core.Context.TimeContextPlugin());
        if (Settings.EnableTmux) plugins.Add(new Tools.TmuxContextPlugin());
        plugins.Add(MountPlugin.Sync("tokenMeter", [SystemPromptService.ServiceKey], ctx =>
        {
            Meter = Core.TokenMeter.TokenMeterService.Mount(ctx);
            Meter.ContextWindowTokens = Settings.ContextWindowTokens;
            // The current route's catalog entry is the authority on the context window;
            // historical request/context declarations can be stale after a catalog change.
            // A 0 window means "unknown" (discovered/local models) — fall through to declarations.
            Meter.ModelWindowResolver = (provider, model) => RuntimeModels(provider ?? "")
                .FirstOrDefault(m => m.Id == model)?.ContextWindowTokens is { } window && window > 0
                    ? window
                    : null;
        }));
        if (Settings.EnableSpill)
        {
            plugins.Add(MountPlugin.Sync("spill", [ToolRuntime.ServiceKey], ctx =>
                Spills = Core.Spill.SpillService.Mount(ctx, Path.Combine(_home, "spills"),
                    new Core.Spill.SpillOptions { ThresholdChars = Settings.SpillThresholdChars })));
        }
        plugins.Add(MountPlugin.Sync("repeatGuard", [], ctx =>
            RepeatGuard = Core.Guards.RepeatCallGuard.Mount(ctx)));
        if (Settings.EnableSchedule)
        {
            plugins.Add(MountPlugin.Sync("schedule", [ToolRuntime.ServiceKey], ctx =>
                Schedules = Core.Schedule.ScheduleService.Mount(ctx)));
        }
        if (Settings.EnableMcp)
        {
            plugins.Add(MountPlugin.Sync("mcp", [ToolRuntime.ServiceKey], ctx =>
                Mcp = Core.Mcp.McpClientService.Mount(ctx,
                    new Core.Mcp.McpOptions { ConfigPath = Path.Combine(_home, "mcp.json") })));
        }

        plugins.Add(new JobsPlugin());
        if (Settings.EnableAskUser) plugins.Add(new AskUserPlugin());
        plugins.Add(new SubagentToolsPlugin());
        if (Settings.EnableWeb) plugins.Add(new WebPlugin(BuildWebProvider(Settings), ownsProvider: true));
        if (Settings.EnableSkills) plugins.Add(new SkillPlugin());
        if (Settings.EnableSessionQuery) plugins.Add(new SessionQueryPlugin());
        if (Settings.EnableGoals) plugins.Add(new GoalPlugin());
        if (Settings.EnablePlanMode) plugins.Add(new PlanModePlugin());
        if (Settings.EnablePlanMode && Settings.EnableAutoPlan)
            plugins.Add(new AutoPlanPlugin(Settings.AutoPlanThreshold));
        if (Settings.EnableCodeMode) plugins.Add(new CodeModePlugin());
        if (Settings.EnableTerminals) plugins.Add(new TerminalPlugin());
        if (Settings.EnableLsp) plugins.Add(new LspPlugin());
        if (Settings.EnableWorkflows) plugins.Add(new WorkflowPlugin());
        if (Settings.EnableTeams) plugins.Add(new TeamPlugin());
        if (Settings.EnableE2b && Settings.ResolveE2bApiKey() is { Length: > 0 } e2bKey)
        {
            plugins.Add(MountPlugin.Sync("e2b", [ToolRuntime.ServiceKey], ctx =>
            {
                RemoteSandbox = new RemoteSandboxTool(new Core.RemoteSandbox.E2bSandboxClient(
                    new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                    new Core.RemoteSandbox.E2bOptions
                    {
                        ApiKey = e2bKey,
                        Template = Settings.E2bTemplate,
                        BaseUrl = Settings.E2bBaseUrl,
                    }));
                ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey).Register(RemoteSandbox);
            }));
        }
        if (Settings.EnableHooks && File.Exists(Path.Combine(_home, "hooks.json")))
        {
            plugins.Add(new HooksPlugin(Path.Combine(_home, "hooks.json")));
        }
        if (Settings.EnableAutoTitles)
        {
            plugins.Add(MountPlugin.Sync("sessionTitle", [LlmRuntime.ServiceKey, AgentRuntime.ServiceKey], ctx =>
                Titles = Core.Sessions.SessionTitleService.Mount(ctx)));
        }

        foreach (var thirdParty in LoadThirdPartyPlugins())
            plugins.Add(thirdParty);

        if (Settings.DisabledPlugins.Count > 0)
        {
            var disabled = new HashSet<string>(Settings.DisabledPlugins, StringComparer.OrdinalIgnoreCase);
            plugins.RemoveAll(p =>
            {
                if (!disabled.Contains(p.Name)) return false;
                Console.Error.WriteLine($"[plugins] disabled '{p.Name}' via settings");
                return true;
            });
        }
        return plugins;
    }

    /// <summary>Reopens the most recent persisted sessions so the sidebar, search, and the
    /// home redirect see them after a cold start (bounded; a bad log must not block boot).</summary>
    private async Task ReattachPersistedSessionsAsync()
    {
        try
        {
            var headers = await Sessions.ListPersistedAsync().ConfigureAwait(false);
            foreach (var header in headers.OrderByDescending(h => h.CreatedAt).Take(200))
            {
                try { await Sessions.OpenAsync(header.Id).ConfigureAwait(false); }
                catch { /* skip unreadable session logs */ }
            }
        }
        catch { /* persistence issues must not block startup */ }
    }

    public UserQuestionsService UserQuestions { get; private set; } = null!;
    public JobsRuntime Jobs { get; private set; } = null!;
    public Core.Subagents.SubagentService Subagents { get; private set; } = null!;
    public Core.Compaction.CompactionService Compaction { get; private set; } = null!;
    public Core.Retry.RetryService Retry { get; private set; } = null!;
    public Core.Instructions.ProjectInstructionsService? Instructions { get; private set; }
    public Core.Sessions.SessionTitleService? Titles { get; private set; }
    public Core.TokenMeter.TokenMeterService? Meter { get; private set; }
    public Core.Spill.SpillService? Spills { get; private set; }
    public Core.Guards.RepeatCallGuard? RepeatGuard { get; private set; }
    public Core.Schedule.ScheduleService? Schedules { get; private set; }
    public Core.Mcp.McpClientService? Mcp { get; private set; }
    public Core.Tools.ToolPolicyService ToolPolicy { get; private set; } = null!;
    public Core.Telemetry.UsageTelemetryService? Telemetry { get; private set; }
    public Tools.RemoteSandboxTool? RemoteSandbox { get; private set; }
    public Core.Credentials.CredentialsService Credentials { get; private set; } = null!;
    public Core.Attachments.AttachmentService Attachments { get; private set; } = null!;

    public ApprovalService Approval { get; private set; } = null!;

    private Func<string, (byte[] Data, string MimeType)?> AttachmentResolver() => id =>
    {
        var read = Attachments.ReadAsync(id).GetAwaiter().GetResult();
        return read is null ? null : (read.Data, read.MimeType);
    };

    private LlmAdapter BuildRoute(string provider, string baseUrl, string? apiKey, IReadOnlyList<LlmModelInfo> models)
        => provider == "anthropic"
            ? new AnthropicAdapter(provider, baseUrl, apiKey ?? "", models, StreamingHttp, attachmentResolver: AttachmentResolver())
            : new OpenAiCompatibleAdapter(provider, baseUrl, apiKey ?? "", models, StreamingHttp, attachmentResolver: AttachmentResolver());

    /// <summary>Selects the web_search backend from settings; a keyed backend without a key
    /// falls back to keyless DuckDuckGo (noted on stderr) so web_search keeps working.</summary>
    public static Blazorly.Harness.Tools.IWebProvider BuildWebProvider(HarnessSettings settings)
    {
        if (string.Equals(settings.WebSearchBackend, "tavily", StringComparison.OrdinalIgnoreCase)
            && settings.ResolveTavilyApiKey() is { Length: > 0 } tavilyKey)
            return new Blazorly.Harness.Tools.TavilySearchProvider(tavilyKey);
        if (string.Equals(settings.WebSearchBackend, "brave", StringComparison.OrdinalIgnoreCase)
            && settings.ResolveBraveApiKey() is { Length: > 0 } braveKey)
            return new Blazorly.Harness.Tools.BraveSearchProvider(braveKey);
        if (!string.Equals(settings.WebSearchBackend, "duckduckgo", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(settings.WebSearchBackend))
            Console.Error.WriteLine($"[web] unknown or keyless search backend '{settings.WebSearchBackend}'; falling back to duckduckgo");
        return new Blazorly.Harness.Tools.HttpWebProvider();
    }

    /// <summary>The selectable model list for a route: the live API list once discovered (known ids
    /// keep their catalog metadata — names, windows, effort levels), otherwise the catalog seeds.</summary>
    public IReadOnlyList<LlmModelInfo> RuntimeModels(string provider)
    {
        var catalog = ProviderCatalog.For(provider, Settings.BaseUrl);
        if (Settings.DiscoveredModels.TryGetValue(provider, out var ids) && ids.Count > 0)
        {
            var byId = catalog.GroupBy(m => m.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return [.. ids.Select(id => byId.TryGetValue(id, out var known) ? known : new LlmModelInfo(provider, id, id))];
        }
        return catalog;
    }

    /// <summary>Loads third-party plugins (minus disabled ones) for the boot list.</summary>
    public List<IHarnessPlugin> LoadThirdPartyPlugins()
    {
        var dirs = Settings.PluginDirs.Count > 0 ? Settings.PluginDirs : [Path.Combine(_home, "plugins")];
        var disabled = new HashSet<string>(Settings.DisabledPlugins, StringComparer.OrdinalIgnoreCase);
        var loaded = new List<IHarnessPlugin>();
        foreach (var dir in dirs)
        {
            var full = dir.StartsWith("~/")
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dir[2..])
                : Path.GetFullPath(dir, _home);
            foreach (var found in PluginLoader.LoadFromDirectory(full, Console.Error))
            {
                if (disabled.Contains(found.Plugin.Name))
                {
                    Console.Error.WriteLine($"[plugins] disabled third-party '{found.Plugin.Name}' via settings");
                    continue;
                }
                Console.Error.WriteLine($"[plugins] loaded '{found.Plugin.Name}' from {found.AssemblyPath}");
                loaded.Add(found.Plugin);
            }
        }
        return loaded;
    }

    /// <summary>
    /// Applies &lt;home&gt;/patches.json over loaded settings (absent file = no-op):
    /// <c>{"set": {"camelCaseKey": value}, "disable": ["plugin-name"]}</c>.
    /// Unknown keys, bad values, and unmappable names warn on stderr and are skipped —
    /// a typo'd patch must never fail a boot.
    /// </summary>
    public static void ApplyPatches(HarnessSettings settings, string home)
    {
        var path = Path.Combine(home, "patches.json");
        if (!File.Exists(path)) return;
        JsonObject patch;
        try
        {
            patch = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException("patches.json must be a JSON object");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[patches] ignoring {path}: {ex.Message}");
            return;
        }
        if (patch["set"] is JsonObject set)
        {
            foreach (var (key, value) in set)
                ApplyPatchKey(settings, key, value);
        }
        if (patch["disable"] is JsonArray disable)
        {
            foreach (var entry in disable)
            {
                if (entry?.GetValue<string>() is { } name) DisablePlugin(settings, name);
                else Console.Error.WriteLine($"[patches] ignoring non-string disable entry in {path}");
            }
        }
    }

    private static void ApplyPatchKey(HarnessSettings settings, string key, JsonNode? value)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(settings, PatchJson)!.AsObject();
            var match = node.AsObject().FirstOrDefault(kv =>
                string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                Console.Error.WriteLine($"[patches] unknown settings key '{key}'; skipped");
                return;
            }
            node[match.Key] = value?.DeepClone();
            SettingsFromNode(node, settings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[patches] cannot set '{key}': {ex.Message}; skipped");
        }
    }

    private static readonly JsonSerializerOptions PatchJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void SettingsFromNode(JsonObject node, HarnessSettings settings)
    {
        var patched = JsonSerializer.Deserialize<HarnessSettings>(node.ToJsonString(), PatchJson)
            ?? throw new InvalidOperationException("patch produced empty settings");
        foreach (var property in typeof(HarnessSettings).GetProperties())
        {
            if (property.CanWrite) property.SetValue(settings, property.GetValue(patched));
        }
    }

    /// <summary>Maps a plugin name to its Enable flag (web → EnableWeb); unknown names warn.</summary>
    internal static void DisablePlugin(HarnessSettings settings, string name)
    {
        var flag = "Enable" + string.Concat(name.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        var property = typeof(HarnessSettings).GetProperties()
            .FirstOrDefault(p => p.PropertyType == typeof(bool) && p.CanWrite
                && p.Name.StartsWith("Enable", StringComparison.Ordinal)
                && string.Equals(p.Name, flag, StringComparison.OrdinalIgnoreCase));
        if (property is null)
        {
            Console.Error.WriteLine($"[patches] cannot disable '{name}': no matching Enable flag; skipped");
            return;
        }
        property.SetValue(settings, false);
    }
    public async Task<(IReadOnlyList<string> Models, string? Error)> DiscoverModelsAsync(
        string provider, string? typedBaseUrl = null, string? typedApiKey = null)
    {
        if (string.IsNullOrWhiteSpace(provider)) return ([], "provider is required");
        var custom = Settings.CustomProviders.FirstOrDefault(c => c.Name == provider);
        string baseUrl;
        string apiKey;
        Action<HttpRequestMessage>? configure = null;
        if (custom is not null)
        {
            baseUrl = typedBaseUrl ?? custom.BaseUrl;
            apiKey = typedApiKey
                ?? (custom.ApiKeyEnv is { Length: > 0 } env ? Environment.GetEnvironmentVariable(env) : null)
                ?? custom.ApiKey
                ?? "";
        }
        else
        {
            baseUrl = typedBaseUrl ?? Settings.BaseUrl;
            apiKey = typedApiKey ?? Settings.EffectiveApiKey ?? "";
            if (provider == "anthropic")
            {
                configure = request =>
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                };
            }
        }
        try
        {
            var models = await LlmModelDiscovery.DiscoverAsync(provider, baseUrl, apiKey, StreamingHttp, configure).ConfigureAwait(false);
            var ids = models.Select(m => m.Id).ToList();
            if (custom is not null)
            {
                foreach (var id in ids)
                {
                    if (!custom.Models.Contains(id)) custom.Models.Add(id);
                }
            }
            else
            {
                Settings.DiscoveredModels[provider] = ids;
            }
            SaveSettings();
            ApplyProviderSelection();
            return (ids, null);
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ([], ex.Message);
        }
    }

    private void RegisterRoute(LlmAdapter adapter)
    {
        if (_routeEffects.Remove(adapter.Provider, out var stale)) stale.Dispose();
        _routeEffects[adapter.Provider] = Llm.RegisterAdapter(adapter);
    }

    public void ApplyProviderSelection()
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(Settings.Provider))
        {
            RegisterRoute(BuildRoute(Settings.Provider, Settings.BaseUrl, Settings.EffectiveApiKey,
                RuntimeModels(Settings.Provider)));
            desired.Add(Settings.Provider);
        }
        foreach (var custom in Settings.CustomProviders)
        {
            if (string.IsNullOrWhiteSpace(custom.Name) || string.IsNullOrWhiteSpace(custom.BaseUrl)) continue;
            var key = !string.IsNullOrWhiteSpace(custom.ApiKey) ? custom.ApiKey
                : !string.IsNullOrWhiteSpace(custom.ApiKeyEnv) ? Environment.GetEnvironmentVariable(custom.ApiKeyEnv)
                : null;
            var models = custom.Models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => new LlmModelInfo(custom.Name, m, m))
                .ToList();
            if (models.Count == 0) models = [new LlmModelInfo(custom.Name, "default", $"{custom.BaseUrl} (default model)")];
            RegisterRoute(BuildRoute(custom.Name, custom.BaseUrl, key, models));
            desired.Add(custom.Name);
        }
        // Routes that are no longer configured are unregistered (e.g. a removed custom provider).
        foreach (var provider in _routeEffects.Keys.ToList())
        {
            if (!desired.Contains(provider) && _routeEffects.Remove(provider, out var stale))
            {
                stale.Dispose();
            }
        }
    }

    public void ApplyDefaultSelection()
    {
        Loop.DefaultSelection = new LlmCallConfig { Provider = Settings.Provider, Model = Settings.Model };
        Sandbox.DefaultMode = Settings.SandboxMode;
        if (Compaction is not null)
        {
            Compaction.Options = Compaction.Options with
            {
                ContextWindowTokens = Settings.ContextWindowTokens,
                Threshold = Settings.CompactionThreshold,
            };
        }
        if (Meter is not null)
        {
            Meter.ContextWindowTokens = Settings.ContextWindowTokens;
        }
        Workspaces = new WorkspaceRegistry(_home).EnsureDefault(Settings.WorkspaceRoot);
    }

    public void SaveSettings()
    {
        var path = Path.Combine(_home, "settings.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(Settings, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        }));
    }

    private void LoadSettings()
    {
        var path = Path.Combine(_home, "settings.json");
        if (!File.Exists(path)) return;
        try
        {
            Settings = System.Text.Json.JsonSerializer.Deserialize<HarnessSettings>(File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }) ?? new HarnessSettings();
            ApplyPatches(Settings, _home);
        }
        catch
        {
            Settings = new HarnessSettings();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Context is not null) await Context.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A built-in provider route: display metadata plus defaults for the Settings UI.</summary>
/// <param name="Category">demo | us | china | local | generic — drives the grouped picker.</param>
public sealed record ProviderInfo(
    string Id,
    string Name,
    string Category,
    string DefaultBaseUrl,
    string? ApiKeyEnv = null)
{
    public bool Local => Category == "local";
}

public static class ProviderCatalog
{
    /// <summary>dsh llm-deepseek reasoning.efforts (off/low/high/max, default high).</summary>
    private static readonly string[] DeepSeekEfforts = ["off", "low", "high", "max"];
    /// <summary>OpenAI-style reasoning_effort pass-through for OpenAI-compatible routes.</summary>
    private static readonly string[] OpenAiEfforts = ["minimal", "low", "medium", "high", "xhigh", "max"];

    public static readonly IReadOnlyList<ProviderInfo> All =
    [
        // US-hosted
        new("openai", "OpenAI", "us", "https://api.openai.com/v1", "OPENAI_API_KEY"),
        new("anthropic", "Anthropic", "us", "https://api.anthropic.com", "ANTHROPIC_API_KEY"),
        new("xai", "xAI (Grok)", "us", "https://api.x.ai/v1", "XAI_API_KEY"),
        new("google", "Google (Gemini)", "us", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY"),
        new("mistral", "Mistral AI", "us", "https://api.mistral.ai/v1", "MISTRAL_API_KEY"),
        new("perplexity", "Perplexity", "us", "https://api.perplexity.ai", "PERPLEXITY_API_KEY"),
        new("together", "Together AI", "us", "https://api.together.xyz/v1", "TOGETHER_API_KEY"),
        new("groq", "Groq", "us", "https://api.groq.com/openai/v1", "GROQ_API_KEY"),
        new("fireworks", "Fireworks AI", "us", "https://api.fireworks.ai/inference/v1", "FIREWORKS_API_KEY"),
        new("openrouter", "OpenRouter (aggregator)", "us", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY"),
        new("cerebras", "Cerebras", "us", "https://api.cerebras.ai/v1", "CEREBRAS_API_KEY"),
        new("cohere", "Cohere", "us", "https://api.cohere.ai/compatibility/v1", "COHERE_API_KEY"),
        // China-hosted
        new("deepseek", "DeepSeek", "china", "https://api.deepseek.com", "DEEPSEEK_API_KEY"),
        new("qwen", "Alibaba Qwen (DashScope)", "china", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "DASHSCOPE_API_KEY"),
        new("moonshot", "Moonshot AI (Kimi)", "china", "https://api.moonshot.ai/v1", "MOONSHOT_API_KEY"),
        new("zhipu", "Zhipu AI (GLM)", "china", "https://open.bigmodel.ai/api/paas/v4", "ZHIPU_API_KEY"),
        new("minimax", "MiniMax", "china", "https://api.minimaxi.chat/v1", "MINIMAX_API_KEY"),
        new("doubao", "ByteDance Doubao (Ark)", "china", "https://ark.cn-beijing.volces.com/api/v3", "ARK_API_KEY"),
        new("ernie", "Baidu ERNIE (Qianfan)", "china", "https://qianfan.baidubce.com/v2", "QIANFAN_API_KEY"),
        new("hunyuan", "Tencent Hunyuan", "china", "https://api.hunyuan.cloud.tencent.com/v1", "HUNYUAN_API_KEY"),
        new("stepfun", "StepFun", "china", "https://api.stepfun.com/v1", "STEPFUN_API_KEY"),
        new("yi", "01.AI (Yi)", "china", "https://api.lingyiwanwu.com/v1", "YI_API_KEY"),
        // Local / self-hosted
        new("ollama", "Ollama (local)", "local", "http://localhost:11434/v1"),
        new("lmstudio", "LM Studio (local)", "local", "http://localhost:1234/v1"),
        new("omlx", "oMLX (local, MLX)", "local", "http://localhost:8000/v1"),
        new("unsloth", "Unsloth", "local", "https://api.unsloth.ai/v1", "UNSLOTH_API_KEY"),
        new("openai-compatible", "Custom OpenAI-compatible", "generic", "https://gateway.example.com/v1"),
    ];

    public static readonly IReadOnlyList<string> Providers = [.. All.Select(p => p.Id)];

    public static ProviderInfo? Info(string provider) => All.FirstOrDefault(p => p.Id == provider);

    public static IReadOnlyList<string> Categories => ["us", "china", "local", "generic"];

    public static IReadOnlyList<LlmModelInfo> For(string provider, string baseUrl) => provider switch
    {
        "deepseek" =>
        [
            // Window/output sizes: dsh llm-deepseek DEFAULT_CONTEXT_WINDOW (1M) / DEFAULT_MAX_TOKENS (256K).
            new LlmModelInfo(provider, "deepseek-v4-flash", "DeepSeek V4 Flash (fast)", ContextWindowTokens: 1_000_000, MaxOutputTokens: 256_000,
                SupportsReasoning: true, ReasoningEfforts: DeepSeekEfforts, DefaultEffort: "low"),
            new LlmModelInfo(provider, "deepseek-v4-pro", "DeepSeek V4 Pro", ContextWindowTokens: 1_000_000, MaxOutputTokens: 256_000,
                SupportsReasoning: true, ReasoningEfforts: DeepSeekEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "deepseek-v4-flash-vision-exp", "DeepSeek V4 Flash Vision (experimental)", ContextWindowTokens: 1_000_000, MaxOutputTokens: 256_000,
                SupportsReasoning: true, ReasoningEfforts: DeepSeekEfforts, DefaultEffort: "low"),
        ],
        "anthropic" =>
        [
            new LlmModelInfo(provider, "claude-sonnet-4-5", "Claude Sonnet 4.5", ContextWindowTokens: 200_000, MaxOutputTokens: 65_536, SupportsReasoning: true),
            new LlmModelInfo(provider, "claude-haiku-4-5", "Claude Haiku 4.5", ContextWindowTokens: 200_000, MaxOutputTokens: 65_536, SupportsReasoning: true),
            new LlmModelInfo(provider, "claude-opus-4-1", "Claude Opus 4.1", ContextWindowTokens: 200_000, MaxOutputTokens: 32_000, SupportsReasoning: true),
        ],
        "openai" =>
        [
            new LlmModelInfo(provider, "gpt-4.1", "GPT-4.1", ContextWindowTokens: 1_047_576, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "gpt-4.1-mini", "GPT-4.1 mini", ContextWindowTokens: 1_047_576, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "o4-mini", "o4-mini", ContextWindowTokens: 200_000, MaxOutputTokens: 100_000,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "medium"),
        ],
        "xai" =>
        [
            new LlmModelInfo(provider, "grok-4", "Grok 4", ContextWindowTokens: 256_000, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "grok-4-fast", "Grok 4 Fast", ContextWindowTokens: 2_000_000, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "grok-3", "Grok 3", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "grok-3-mini", "Grok 3 Mini", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "low"),
        ],
        "google" =>
        [
            new LlmModelInfo(provider, "gemini-2.5-pro", "Gemini 2.5 Pro", ContextWindowTokens: 1_047_576, MaxOutputTokens: 65_536,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "medium"),
            new LlmModelInfo(provider, "gemini-2.5-flash", "Gemini 2.5 Flash", ContextWindowTokens: 1_047_576, MaxOutputTokens: 65_536,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "medium"),
            new LlmModelInfo(provider, "gemini-2.0-flash", "Gemini 2.0 Flash", ContextWindowTokens: 1_047_576, MaxOutputTokens: 8_192),
        ],
        "mistral" =>
        [
            new LlmModelInfo(provider, "mistral-large-latest", "Mistral Large", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "mistral-medium-latest", "Mistral Medium", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "codestral-latest", "Codestral", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "magistral-medium-latest", "Magistral Medium (reasoning)", ContextWindowTokens: 40_960, MaxOutputTokens: 40_960,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts),
        ],
        "perplexity" =>
        [
            new LlmModelInfo(provider, "sonar-pro", "Sonar Pro", ContextWindowTokens: 200_000, MaxOutputTokens: 8_192),
            new LlmModelInfo(provider, "sonar", "Sonar", ContextWindowTokens: 127_072, MaxOutputTokens: 8_192),
            new LlmModelInfo(provider, "sonar-reasoning-pro", "Sonar Reasoning Pro", ContextWindowTokens: 127_072, MaxOutputTokens: 8_192,
                SupportsReasoning: true),
            new LlmModelInfo(provider, "sonar-deep-research", "Sonar Deep Research", ContextWindowTokens: 127_072, MaxOutputTokens: 8_192),
        ],
        "together" =>
        [
            new LlmModelInfo(provider, "deepseek-ai/DeepSeek-V3", "DeepSeek V3", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "meta-llama/Llama-3.3-70B-Instruct-Turbo", "Llama 3.3 70B Turbo", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "meta-llama/Meta-Llama-4-Maverick-17B-128E-Instruct-FP8", "Llama 4 Maverick", ContextWindowTokens: 1_047_576, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "Qwen/Qwen2.5-Coder-32B-Instruct-Turbo", "Qwen2.5 Coder 32B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
        ],
        "groq" =>
        [
            new LlmModelInfo(provider, "llama-3.3-70b-versatile", "Llama 3.3 70B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "llama-3.1-8b-instant", "Llama 3.1 8B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "openai/gpt-oss-120b", "GPT-OSS 120B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "qwen/qwen3-32b", "Qwen3 32B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "deepseek-r1-distill-llama-70b", "DeepSeek R1 Distill 70B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768,
                SupportsReasoning: true),
        ],
        "fireworks" =>
        [
            new LlmModelInfo(provider, "accounts/fireworks/models/deepseek-v3", "DeepSeek V3", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "accounts/fireworks/models/kimi-k2-instruct", "Kimi K2", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "accounts/fireworks/models/qwen3-coder-480b-a35b-instruct", "Qwen3 Coder 480B", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "accounts/fireworks/models/llama4-maverick-instruct-basic", "Llama 4 Maverick", ContextWindowTokens: 1_047_576, MaxOutputTokens: 32_768),
        ],
        "openrouter" =>
        [
            new LlmModelInfo(provider, "deepseek/deepseek-chat", "DeepSeek Chat", ContextWindowTokens: 163_840, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5", ContextWindowTokens: 200_000, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "openai/gpt-4.1-mini", "GPT-4.1 mini", ContextWindowTokens: 1_047_576, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "qwen/qwen3-coder", "Qwen3 Coder", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "moonshotai/kimi-k2", "Kimi K2", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
        ],
        "cerebras" =>
        [
            new LlmModelInfo(provider, "llama-3.3-70b", "Llama 3.3 70B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "llama3.1-8b", "Llama 3.1 8B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "qwen-3-32b", "Qwen3 32B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "gpt-oss-120b", "GPT-OSS 120B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
        ],
        "cohere" =>
        [
            new LlmModelInfo(provider, "command-a-03-2025", "Command A", ContextWindowTokens: 262_144, MaxOutputTokens: 8_192),
            new LlmModelInfo(provider, "command-r-plus-08-2024", "Command R+", ContextWindowTokens: 131_072, MaxOutputTokens: 4_096),
            new LlmModelInfo(provider, "command-r7b-12-2024", "Command R7B", ContextWindowTokens: 131_072, MaxOutputTokens: 4_096),
        ],
        "qwen" =>
        [
            new LlmModelInfo(provider, "qwen3-max", "Qwen3 Max", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "qwen3-plus", "Qwen3 Plus", ContextWindowTokens: 131_072, MaxOutputTokens: 16_384),
            new LlmModelInfo(provider, "qwen3-coder-plus", "Qwen3 Coder Plus", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "qwen2.5-coder-32b-instruct", "Qwen2.5 Coder 32B", ContextWindowTokens: 131_072, MaxOutputTokens: 8_192),
        ],
        "moonshot" =>
        [
            new LlmModelInfo(provider, "kimi-k2-0905-preview", "Kimi K2 (0905)", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "kimi-k2-instruct", "Kimi K2 Instruct", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "moonshot-v1-128k", "Moonshot V1 128K", ContextWindowTokens: 131_072, MaxOutputTokens: 8_192),
            new LlmModelInfo(provider, "moonshot-v1-8k", "Moonshot V1 8K", ContextWindowTokens: 8_192, MaxOutputTokens: 8_192),
        ],
        "zhipu" =>
        [
            new LlmModelInfo(provider, "glm-4.6", "GLM-4.6", ContextWindowTokens: 204_800, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "glm-4.5", "GLM-4.5", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "glm-4.5-air", "GLM-4.5 Air", ContextWindowTokens: 131_072, MaxOutputTokens: 16_384),
            new LlmModelInfo(provider, "glm-4-flash", "GLM-4 Flash (free tier)", ContextWindowTokens: 131_072, MaxOutputTokens: 4_096),
        ],
        "minimax" =>
        [
            new LlmModelInfo(provider, "MiniMax-M2", "MiniMax M2", ContextWindowTokens: 204_800, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "MiniMax-M1", "MiniMax M1", ContextWindowTokens: 1_000_000, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "medium"),
            new LlmModelInfo(provider, "abab6.5s-chat", "abab6.5s", ContextWindowTokens: 245_760, MaxOutputTokens: 8_192),
        ],
        "doubao" =>
        [
            new LlmModelInfo(provider, "doubao-seed-1-6", "Doubao Seed 1.6", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "doubao-seed-code-1-6", "Doubao Seed Code 1.6", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "doubao-1-5-pro-256k", "Doubao 1.5 Pro 256K", ContextWindowTokens: 262_144, MaxOutputTokens: 16_384),
            new LlmModelInfo(provider, "doubao-pro-32k", "Doubao Pro 32K", ContextWindowTokens: 32_768, MaxOutputTokens: 8_192),
        ],
        "ernie" =>
        [
            new LlmModelInfo(provider, "ernie-4.5-turbo-128k", "ERNIE 4.5 Turbo 128K", ContextWindowTokens: 131_072, MaxOutputTokens: 16_384,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts),
            new LlmModelInfo(provider, "ernie-4.5-300k-a47b", "ERNIE 4.5 300K", ContextWindowTokens: 307_200, MaxOutputTokens: 16_384,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts),
            new LlmModelInfo(provider, "ernie-x1-turbo-32k", "ERNIE X1 Turbo 32K", ContextWindowTokens: 32_768, MaxOutputTokens: 16_384,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts),
            new LlmModelInfo(provider, "ernie-speed-128k", "ERNIE Speed 128K", ContextWindowTokens: 131_072, MaxOutputTokens: 8_192),
        ],
        "hunyuan" =>
        [
            new LlmModelInfo(provider, "hunyuan-turbos-1t", "Hunyuan Turbos 1T", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "hunyuan-turbo-latest", "Hunyuan Turbo", ContextWindowTokens: 32_768, MaxOutputTokens: 8_192),
            new LlmModelInfo(provider, "hunyuan-standard", "Hunyuan Standard", ContextWindowTokens: 32_768, MaxOutputTokens: 8_192),
        ],
        "stepfun" =>
        [
            new LlmModelInfo(provider, "step-2-16k", "Step 2 16K", ContextWindowTokens: 16_384, MaxOutputTokens: 8_192,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts),
            new LlmModelInfo(provider, "step-2-mini", "Step 2 Mini", ContextWindowTokens: 8_192, MaxOutputTokens: 4_096),
            new LlmModelInfo(provider, "step-1v-8k", "Step 1V 8K", ContextWindowTokens: 8_192, MaxOutputTokens: 4_096),
        ],
        "yi" =>
        [
            new LlmModelInfo(provider, "yi-lightning", "Yi Lightning", ContextWindowTokens: 32_768, MaxOutputTokens: 16_384),
            new LlmModelInfo(provider, "yi-large", "Yi Large", ContextWindowTokens: 32_768, MaxOutputTokens: 16_384),
        ],
        "ollama" =>
        [
            new LlmModelInfo(provider, "llama3.2", "llama3.2 (if pulled)"),
            new LlmModelInfo(provider, "qwen2.5-coder:7b", "qwen2.5-coder:7b (if pulled)"),
            new LlmModelInfo(provider, "deepseek-r1:8b", "deepseek-r1:8b (if pulled)"),
        ],
        "lmstudio" => [], // model ids are loadout-specific; use Discover models
        "omlx" => [],    // serves whatever is in the HF/LM Studio model cache; use Discover models
        "unsloth" =>
        [
            new LlmModelInfo(provider, "gpt-oss-120b", "GPT-OSS 120B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "gpt-oss-20b", "GPT-OSS 20B", ContextWindowTokens: 131_072, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "qwen3-coder-480b-a35b-instruct", "Qwen3 Coder 480B", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768),
        ],
        "openai-compatible" =>
        [
            new LlmModelInfo(provider, "default", $"{baseUrl} (default model)", ReasoningEfforts: OpenAiEfforts),
        ],
        _ => [],
    };

    public static string DefaultModel(string provider) =>
        For(provider, "").Count > 0 ? For(provider, "")[0].Id
        : "default";
}
