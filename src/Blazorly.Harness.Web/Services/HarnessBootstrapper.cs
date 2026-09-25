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
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    /// <summary>API key stash per provider id so switching providers keeps each key typed once.</summary>
    public Dictionary<string, string> ProviderKeys { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Models loaded live from each provider's /models endpoint (ids plus any sizes the
    /// endpoint publishes), or explicitly entered by the user. An empty list means no models.
    /// Legacy id-only lists still load.</summary>
    public Dictionary<string, List<DiscoveredModelInfo>> DiscoveredModels { get; set; } = new(StringComparer.Ordinal);
    public string BaseUrl { get; set; } = "";
    /// <summary>Provider id <see cref="BaseUrl"/> was entered for; null (legacy settings) means the active provider.</summary>
    public string? BaseUrlProvider { get; set; }
    /// <summary>Endpoint stash per provider id, so switching routes keeps each provider's URL typed once.</summary>
    public Dictionary<string, string> ProviderBaseUrls { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Wire-protocol override per built-in provider id: "openai" (automatic), "responses" or "anthropic".
    /// Custom gateways carry their own <see cref="CustomProviderConfig.ApiType"/> instead.</summary>
    public Dictionary<string, string> ProviderApiTypes { get; set; } = new(StringComparer.Ordinal);
    public string WorkspaceRoot { get; set; } = Directory.GetCurrentDirectory();
    public string SandboxMode { get; set; } = SandboxPolicy.FullAccess;
    /// <summary>
    /// Fail closed when the host cannot confine (no Linux Landlock) instead of degrading an
    /// unconfigured sandbox to full-access.
    /// </summary>
    public bool SandboxFailClosedWhenUnsupported { get; set; }
    public string Persistence { get; set; } = "sqlite"; // sqlite (default; jsonl sessions auto-import) | jsonl
    public long ContextWindowTokens { get; set; } = 262_144;
    public int MaxOutputTokens { get; set; } = 65_536;
    public double CompactionThreshold { get; set; } = 0.9;
    public int CompactionPrunerChars { get; set; } = 4_000;
    public Blazorly.Harness.Core.Retry.RetryPolicyConfig Retry { get; set; } = new();
    /// <summary>First-byte watchdog in seconds: cancel a request whose provider has streamed no
    /// bytes within this window (0 disables). Buffered routes can legitimately sit silent for
    /// minutes at large context — the default is sized for that, not for fast routes.</summary>
    public int FirstByteTimeoutSeconds { get; set; } = 600;
    /// <summary>Retry policy overrides keyed by provider id (e.g. "zai"); routes without an entry use Retry.</summary>
    public Dictionary<string, Blazorly.Harness.Core.Retry.RetryPolicyConfig> RetryProviders { get; set; } = new(StringComparer.Ordinal);
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

    /// <summary>
    /// System One decision model (TypeSafe): unstructured state in, calibrated typed decisions out.
    /// Off by default, and off means *exactly* the previous behaviour — every seam falls through to
    /// the deterministic heuristic it already used, with no network call and nothing new logged.
    /// </summary>
    public bool EnableSystemOne { get; set; } = false;
    public string? SystemOneApiKey { get; set; }
    public string SystemOneApiKeyEnv { get; set; } = "SYSTEMONE_API_KEY";
    public string SystemOneBaseUrl { get; set; } = "https://api.typesafe.ai";
    public string SystemOnePath { get; set; } = "/v1/systemone";
    /// <summary>Model id requested; blank means the service default.</summary>
    public string? SystemOneModel { get; set; }
    /// <summary>Auth header style: "bearer" (Authorization: Bearer …) or "apikey" (X-API-Key: …).</summary>
    public string SystemOneAuthStyle { get; set; } = "bearer";
    /// <summary>
    /// Hard cap for one decision call. These sit before a model call, inside the measured cancel
    /// path, so the default is deliberately tighter than the hook timeout.
    /// </summary>
    public int SystemOneTimeoutMs { get; set; } = 1_500;
    /// <summary>
    /// Seams allowed to consult the model. Empty or ["*"] means every implemented seam.
    /// Known seams: auto-plan, risk-gate, tool-gate, loop, compaction.
    /// </summary>
    public List<string> SystemOneSeams { get; set; } = ["auto-plan", "risk-gate"];

    /// <summary>Park an allowed tool call for human approval at or above this P(risky).</summary>
    public bool EnableRiskGate { get; set; } = false;
    public double RiskGateThreshold { get; set; } = 0.5;

    /// <summary>
    /// Hybrid tool selection. Above ToolGateMaxTools the request's tool schemas are pruned to a
    /// shortlist (core + recently used + lexically relevant) with zero AI calls; with
    /// "tool-gate" in SystemOneSeams the ambiguous tail also gets one System One choice. Off
    /// means every request carries the full tool list, exactly as before.
    /// </summary>
    public bool EnableToolGate { get; set; } = false;
    /// <summary>Filtering engages only when the visible tool count exceeds this.</summary>
    public int ToolGateMaxTools { get; set; } = 28;
    /// <summary>Shortlist target size (core + recent + lexical + model picks, capped).</summary>
    public int ToolGateKeep { get; set; } = 18;
    /// <summary>Fewer lexical hits than this counts as an ambiguous brief for the model tail.</summary>
    public int ToolGateMinLexical { get; set; } = 6;
    /// <summary>Comma-separated always-kept tools; blank keeps the built-in default set.</summary>
    public string? ToolGateCoreTools { get; set; }

    /// <summary>Auto-plan calibration bands; between them the model abstains and the heuristic decides.</summary>
    public double AutoPlanEngageAt { get; set; } = 0.60;
    public double AutoPlanSkipAt { get; set; } = 0.25;

    /// <summary>Settings key first, then the configured environment variable.</summary>
    public string? ResolveSystemOneApiKey()
        => !string.IsNullOrWhiteSpace(SystemOneApiKey) ? SystemOneApiKey : Environment.GetEnvironmentVariable(SystemOneApiKeyEnv);

    /// <summary>True only when the decision layer is on *and* a key resolved.</summary>
    public bool SystemOneReady => EnableSystemOne && ResolveSystemOneApiKey() is { Length: > 0 };

    /// <summary>
    /// Switches the active route: the typed key is stashed for the provider being left and the
    /// entering provider's stashed key restored, and likewise for endpoints — the entering
    /// provider gets its own stashed URL or its catalog default, never the previous route's
    /// URL. Without this, an override such as `blazorly run --provider zai` would keep the
    /// previous host's URL and typed key while labelling every request as the new provider —
    /// the exact cross-route leak <see cref="ApiKeyFor"/> promises never to make. A URL that
    /// must serve several providers belongs in a custom provider, where name, URL and key
    /// travel together.
    /// </summary>
    /// <param name="previousProvider">The provider the typed key and base URL belong to.</param>
    public void SelectProvider(string previousProvider, string provider, string? model = null)
    {
        if (provider == previousProvider)
        {
            Provider = provider;
            if (!string.IsNullOrWhiteSpace(model)) Model = model!;
            return;
        }
        if (!string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(previousProvider))
            ProviderKeys[previousProvider] = ApiKey;
        if (!string.IsNullOrWhiteSpace(previousProvider) && !string.IsNullOrWhiteSpace(Model)
            && !DiscoveredModels.ContainsKey(previousProvider)
            && !CustomProviders.Any(c => c.Name == previousProvider))
            DiscoveredModels[previousProvider] = [new(Model)];

        // Same stashing for endpoints: leave the old route's URL behind, restore the new route's.
        if (!string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(previousProvider)
            && (BaseUrlProvider is null || BaseUrlProvider == previousProvider))
            ProviderBaseUrls[previousProvider] = BaseUrl;
        if (string.IsNullOrWhiteSpace(provider))
        {
            ClearSelection();
            return;
        }
        ApiKey = ProviderKeys.TryGetValue(provider, out var stashed) ? stashed : null;
        // The entering provider's own endpoint: its stashed URL, else its catalog default. The
        // previous provider's URL never follows the selection — carrying it over would pin the
        // new route at a host that may hold the connection without answering (an unbounded
        // hang under the streaming client) and send the new provider's key to the old host.
        var custom = CustomProviders.FirstOrDefault(c => c.Name == provider);
        BaseUrl = custom is not null ? custom.BaseUrl
            : ProviderBaseUrls.TryGetValue(provider, out var stashedUrl) && !string.IsNullOrWhiteSpace(stashedUrl)
            ? stashedUrl
            : ProviderCatalog.Info(provider)?.DefaultBaseUrl ?? "";
        BaseUrlProvider = provider;
        Provider = provider;
        Model = !string.IsNullOrWhiteSpace(model) ? model!
            : custom is not null ? custom.Models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? ""
            : DiscoveredModels.TryGetValue(provider, out var models)
                ? models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Id))?.Id ?? "" : "";
    }

    public void ClearSelection()
    {
        Provider = "";
        Model = "";
        ApiKey = null;
        BaseUrl = "";
        BaseUrlProvider = null;
    }

    /// <summary>Removes the whole configured route, including an active selection.</summary>
    public void RemoveProvider(string provider)
    {
        CustomProviders.RemoveAll(c => c.Name == provider);
        ProviderKeys.Remove(provider);
        ProviderBaseUrls.Remove(provider);
        DiscoveredModels.Remove(provider);
        ProviderApiTypes.Remove(provider);
        RetryProviders.Remove(provider);
        if (Provider == provider) ClearSelection();
    }

    /// <summary>Endpoint for a provider route (active or background): the typed field when
    /// it belongs to them — a null <see cref="BaseUrlProvider"/> means the active provider —
    /// else their own stash, falling back to the catalog default. Background routes never
    /// inherit the active route's URL (route registration resolves the same way).</summary>
    public string BaseUrlFor(string provider)
        => CustomProviders.FirstOrDefault(c => c.Name == provider) is { } custom ? custom.BaseUrl
            : (string.IsNullOrWhiteSpace(BaseUrlProvider) ? Provider : BaseUrlProvider) == provider
                && !string.IsNullOrWhiteSpace(BaseUrl)
            ? BaseUrl
            : ProviderBaseUrls.TryGetValue(provider, out var stashed) && stashed.Length > 0
                ? stashed
                : ProviderCatalog.Info(provider)?.DefaultBaseUrl ?? "";

    /// <summary>Resolved per request, never persisted; never sends one provider's key to another provider's route.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveApiKey => ApiKeyFor(Provider);

    /// <summary>Key resolution for any provider route (active or background). The typed
    /// field only applies to the active provider. Custom routes use their own key or
    /// explicitly named environment variable; built-ins use only their own stash/env.</summary>
    public string? ApiKeyFor(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        if (CustomProviders.FirstOrDefault(c => c.Name == provider) is { } custom)
            return !string.IsNullOrWhiteSpace(custom.ApiKey) ? custom.ApiKey
                : !string.IsNullOrWhiteSpace(custom.ApiKeyEnv) ? Environment.GetEnvironmentVariable(custom.ApiKeyEnv)
                : null;
        if (provider == Provider && !string.IsNullOrWhiteSpace(ApiKey)) return ApiKey;
        if (ProviderKeys.TryGetValue(provider, out var stashed) && !string.IsNullOrWhiteSpace(stashed)) return stashed;
        var catalogEnv = ProviderCatalog.Info(provider)?.ApiKeyEnv;
        if (!string.IsNullOrWhiteSpace(catalogEnv))
        {
            var fromCatalog = Environment.GetEnvironmentVariable(catalogEnv);
            if (!string.IsNullOrWhiteSpace(fromCatalog)) return fromCatalog;
        }
        var providerSpecific = Environment.GetEnvironmentVariable(
            $"{provider.ToUpperInvariant().Replace('-', '_')}_API_KEY"); // DEEPSEEK/OPENAI/ANTHROPIC_API_KEY
        if (!string.IsNullOrWhiteSpace(providerSpecific)) return providerSpecific;
        return null;
    }
}

/// <summary>A model id from a provider's /models endpoint plus any sizes it published
/// (most endpoints are id-only; OpenRouter-style ones include context/output sizes).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(DiscoveredModelInfoConverter))]
public sealed record DiscoveredModelInfo(string Id, long? ContextWindowTokens = null, int? MaxOutputTokens = null)
{
    public static implicit operator DiscoveredModelInfo(string id) => new(id);
}

/// <summary>Reads legacy plain-string ids and full objects; writes strings when id-only
/// so settings files stay tidy unless the endpoint actually published sizes.</summary>
public sealed class DiscoveredModelInfoConverter : System.Text.Json.Serialization.JsonConverter<DiscoveredModelInfo>
{
    public override DiscoveredModelInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new DiscoveredModelInfo(reader.GetString() ?? "");
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"unexpected token {reader.TokenType} for a discovered model");
        string id = "";
        long? window = null;
        int? output = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString();
            if (!reader.Read()) break;
            if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
            {
                id = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
            }
            else if (string.Equals(name, "contextWindowTokens", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "context_window_tokens", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number) && number > 0) window = number;
            }
            else if (string.Equals(name, "maxOutputTokens", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && number > 0) output = number;
            }
            else
            {
                reader.Skip();
            }
        }
        return new DiscoveredModelInfo(id, window, output);
    }

    public override void Write(Utf8JsonWriter writer, DiscoveredModelInfo value, JsonSerializerOptions options)
    {
        if (value.ContextWindowTokens is null && value.MaxOutputTokens is null)
        {
            writer.WriteStringValue(value.Id);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        if (value.ContextWindowTokens is { } window) writer.WriteNumber("contextWindowTokens", window);
        if (value.MaxOutputTokens is { } output) writer.WriteNumber("maxOutputTokens", output);
        writer.WriteEndObject();
    }
}

/// <summary>An extra provider route configured from the Settings UI (OpenAI-compatible by default).</summary>
public sealed class CustomProviderConfig
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? ApiKeyEnv { get; set; }
    /// <summary>Wire protocol: "openai" (automatic, default), "responses" or "anthropic".</summary>
    public string ApiType { get; set; } = "openai";
    public List<string> Models { get; set; } = [];
    /// <summary>Optional API/user metadata for the explicitly configured model ids.</summary>
    public List<DiscoveredModelInfo> ModelMetadata { get; set; } = [];

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
    private ISessionPersistence? _ownedPersistence;
    private bool _disposed;
    private readonly string _home;
    public string DataDirectory => _home;
    public string SettingsFilePath => Path.Combine(_home, "settings.json");
    public string? SettingsLoadError { get; private set; }

    /// <summary>One long-lived client for streaming adapter requests (no request-level timeout; the caller's token governs).</summary>
    internal static readonly HttpClient StreamingHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public HarnessBootstrapper()
    {
        // BLAZORLY_HOME isolates the whole harness home (settings, sessions, spills, …);
        // used by tests and by users who want a portable home.
        _home = Path.GetFullPath(Environment.GetEnvironmentVariable("BLAZORLY_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly"));
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
        var tracker = new FsObservationTracker();
        Sandbox = new SandboxPolicy
        {
            DefaultMode = Settings.SandboxMode,
            AllowUnconfinedFallback = !Settings.SandboxFailClosedWhenUnsupported,
        };
        SystemPromptService? prompt = null;

        var plugins = new List<IHarnessPlugin>
        {
            MountPlugin.Sync("llm", [], ctx =>
            {
                Llm = LlmRuntime.Mount(ctx);
                Llm.FirstByteTimeout = Settings.FirstByteTimeoutSeconds > 0
                    ? TimeSpan.FromSeconds(Settings.FirstByteTimeoutSeconds)
                    : null;
            }),
            MountPlugin.Sync("systemPrompt", [], ctx => prompt = SystemPromptService.Mount(ctx)),
            MountPlugin.Sync("tools", [SystemPromptService.ServiceKey],
                ctx => Tools = ToolRuntime.Mount(ctx, prompt!)),
            MountPlugin.Sync("sessions", [], ctx =>
            {
                _ownedPersistence = Settings.Persistence == "jsonl"
                    ? new JsonlSessionPersistence(Path.Combine(_home, "sessions"))
                    : PersistenceMigrator.EnsureSqliteAsync(
                            Path.Combine(_home, "sessions.db"), Path.Combine(_home, "sessions"),
                            message => Console.Out.WriteLine(message))
                        .GetAwaiter().GetResult().Store;
                Sessions = SessionStore.Mount(ctx, _ownedPersistence);
            }),
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
            Retry = Core.Retry.RetryService.Mount(ctx, new Core.Retry.RetryOptions
            {
                Default = Settings.Retry,
                Providers = Settings.RetryProviders,
            })));
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

        // Decision seam (System One). Built eagerly so the same service instance can be handed to
        // the plugins that consume it, independent of boot order. With the feature off — or on but
        // keyless — this mounts the no-op model and every seam behaves exactly as before.
        var skipAtBoot = new HashSet<string>(Settings.DisabledPlugins, StringComparer.OrdinalIgnoreCase);
        var decisionsEnabled = !skipAtBoot.Contains(Core.Decisions.DecisionPlugin.PluginName);
        var decisions = new Core.Decisions.DecisionPlugin(
            BuildDecisionModel(Settings),
            new Core.Decisions.DecisionOptions
            {
                Enabled = Settings.SystemOneReady,
                Seams = Settings.SystemOneSeams is { Count: 0 } ? ["*"] : Settings.SystemOneSeams,
            });
        if (decisionsEnabled) plugins.Add(decisions);
        // The gate is meaningless without the seam, and mounting it anyway would leave an inert
        // plugin in the composition — drop both together.
        if (Settings.EnableRiskGate && decisionsEnabled)
        {
            plugins.Add(new Core.Decisions.RiskGatePlugin(decisions.Service,
                new Core.Decisions.RiskGateOptions { Threshold = Settings.RiskGateThreshold }));
        }
        // The tool gate is independent of System One: without the seam it is the pure-code filter.
        if (Settings.EnableToolGate && !skipAtBoot.Contains(Core.Decisions.ToolGatePlugin.PluginName))
        {
            var core = (Settings.ToolGateCoreTools ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            plugins.Add(new Core.Decisions.ToolGatePlugin(
                new Core.Decisions.ToolGateOptions
                {
                    Enabled = true,
                    MaxTools = Settings.ToolGateMaxTools > 0 ? Settings.ToolGateMaxTools : 28,
                    Keep = Settings.ToolGateKeep > 0 ? Settings.ToolGateKeep : 18,
                    MinLexical = Settings.ToolGateMinLexical > 0 ? Settings.ToolGateMinLexical : 6,
                    CoreTools = core.Length > 0 ? core : new Core.Decisions.ToolGateOptions().CoreTools,
                },
                decisionsEnabled ? decisions.Service : null));
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
            plugins.Add(new AutoPlanPlugin(Settings.AutoPlanThreshold, decisions.Service,
                new AutoPlanDecisionOptions
                {
                    EngageAt = Settings.AutoPlanEngageAt,
                    SkipAt = Settings.AutoPlanSkipAt,
                }));
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
            await ReconcileOrphanedDelegationsAsync().ConfigureAwait(false);
        }
        catch { /* persistence issues must not block startup */ }
    }

    /// <summary>
    /// Boot backstop for delegation rows orphaned by the previous process: background
    /// monitors are in-memory tasks, so any child that settled after a restart (or whose
    /// monitor died with the old process) would otherwise read "running" forever. Each
    /// session is guarded individually; healing never blocks startup.
    /// </summary>
    private async Task ReconcileOrphanedDelegationsAsync()
    {
        var subagents = Subagents;
        if (subagents is null) return;
        foreach (var session in Sessions.LiveSessions())
        {
            try { await subagents.ReconcileAsync(session.Id).ConfigureAwait(false); }
            catch { /* one session's healing must not block the rest */ }
        }
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

    /// <summary>Effective wire protocol for a route: a custom gateway's own API type,
    /// else a per-provider override, else the catalog default. Legacy "openai" settings on
    /// providers already migrated to Responses keep using Responses.</summary>
    public static string ResolveApiType(HarnessSettings settings, string provider)
    {
        var apiType = settings.CustomProviders.FirstOrDefault(c => c.Name == provider) is { } custom
            ? ProviderCatalog.NormalizeApiType(custom.ApiType)
            : settings.ProviderApiTypes.TryGetValue(provider, out var overrideType)
                ? ProviderCatalog.NormalizeApiType(overrideType)
                : ProviderCatalog.Info(provider)?.DefaultApiType ?? "openai";
        return apiType == "openai" && ProviderCatalog.UsesResponsesApi(provider) ? "responses" : apiType;
    }

    /// <summary>Effective wire protocol for a route under the live settings.</summary>
    public string ApiTypeFor(string provider) => ResolveApiType(Settings, provider);

    private LlmAdapter BuildRoute(string provider, string baseUrl, string? apiKey, IReadOnlyList<LlmModelInfo> models)
        => ResolveApiType(Settings, provider) switch
        {
            "anthropic" => new AnthropicAdapter(provider, baseUrl, apiKey ?? "", models, StreamingHttp, attachmentResolver: AttachmentResolver()),
            "responses" => new ResponsesApiAdapter(provider, baseUrl, apiKey ?? "", models, StreamingHttp,
                attachmentResolver: AttachmentResolver(), requireApiKey: ProviderCatalog.RequiresApiKey(provider)),
            _ => new OpenAiCompatibleAdapter(provider, baseUrl, apiKey ?? "", models, StreamingHttp,
                attachmentResolver: AttachmentResolver(), requireApiKey: ProviderCatalog.RequiresApiKey(provider)),
        };

    /// <summary>Selects the web_search backend from settings; a keyed backend without a key
    /// falls back to keyless DuckDuckGo (noted on stderr) so web_search keeps working.</summary>
    /// <summary>
    /// The decision model for this configuration: a System One client when the feature is on and a
    /// key resolved, otherwise the no-op model. Returning the no-op rather than null is what makes
    /// "disabled" and "never existed" the same code path for every consumer.
    /// </summary>
    public static Core.Decisions.IDecisionModel BuildDecisionModel(HarnessSettings settings)
        => settings.SystemOneReady
            ? new Core.Decisions.SystemOneClient(SystemOneOptionsOf(settings, settings.ResolveSystemOneApiKey()!))
            : Core.Decisions.NoOpDecisionModel.Instance;

    /// <summary>Wire options from settings; shared by the boot composition and <c>decisions probe</c>.</summary>
    public static Core.Decisions.SystemOneOptions SystemOneOptionsOf(HarnessSettings settings, string apiKey) => new()
    {
        ApiKey = apiKey,
        BaseUrl = string.IsNullOrWhiteSpace(settings.SystemOneBaseUrl)
            ? "https://api.typesafe.ai"
            : settings.SystemOneBaseUrl,
        Path = string.IsNullOrWhiteSpace(settings.SystemOnePath) ? "/v1/systemone" : settings.SystemOnePath,
        Model = string.IsNullOrWhiteSpace(settings.SystemOneModel) ? null : settings.SystemOneModel,
        AuthStyle = settings.SystemOneAuthStyle,
        TimeoutMs = settings.SystemOneTimeoutMs > 0 ? settings.SystemOneTimeoutMs : 1_500,
    };

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

    /// <summary>Only explicitly configured model ids are selectable. Catalog metadata can
    /// describe those ids, but cannot create available models.</summary>
    public static IReadOnlyList<LlmModelInfo> CustomRouteModels(CustomProviderConfig custom)
    {
        return DescribeModels(custom.Name, custom.Models.Select(id =>
            custom.ModelMetadata.FirstOrDefault(m => m.Id == id) ?? new DiscoveredModelInfo(id)));
    }

    /// <summary>The selectable model list comes from saved discovery or manually entered ids.
    /// An empty saved list is authoritative. Legacy explicit model selections remain usable
    /// until a list is saved; a clean installation has no models.</summary>
    public IReadOnlyList<LlmModelInfo> RuntimeModels(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return [];
        if (Settings.CustomProviders.FirstOrDefault(c => c.Name == provider) is { } custom)
            return CustomRouteModels(custom);
        if (Settings.DiscoveredModels.TryGetValue(provider, out var found))
            return DescribeModels(provider, found);
        return provider == Settings.Provider && !string.IsNullOrWhiteSpace(Settings.Model)
            ? DescribeModels(provider, [new(Settings.Model)]) : [];
    }

    private static IReadOnlyList<LlmModelInfo> DescribeModels(string provider, IEnumerable<DiscoveredModelInfo> entries)
    {
        var catalog = ProviderCatalog.For(provider, "").GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return entries.Where(m => !string.IsNullOrWhiteSpace(m.Id)).DistinctBy(m => m.Id, StringComparer.Ordinal)
            .Select(entry => (catalog.GetValueOrDefault(entry.Id) ?? new LlmModelInfo(provider, entry.Id, entry.Id)) with
            {
                ContextWindowTokens = entry.ContextWindowTokens ?? catalog.GetValueOrDefault(entry.Id)?.ContextWindowTokens,
                MaxOutputTokens = entry.MaxOutputTokens ?? catalog.GetValueOrDefault(entry.Id)?.MaxOutputTokens,
            }).ToList();
    }

    /// <summary>Output cap for a model: the settings default, clamped to the model's own
    /// ceiling when the catalog or API metadata states one (an over-large max_tokens 400s).</summary>
    public static int ResolveMaxOutputTokens(HarnessSettings settings, IReadOnlyList<LlmModelInfo> models, string? modelId)
    {
        var cap = settings.MaxOutputTokens;
        var known = string.IsNullOrEmpty(modelId)
            ? null
            : models.FirstOrDefault(m => m.Id == modelId)?.MaxOutputTokens;
        return known is > 0 ? Math.Min(cap, known.Value) : cap;
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
        string provider, string? typedBaseUrl = null, string? typedApiKey = null, TimeSpan? timeout = null, CancellationToken ct = default,
        string? apiType = null)
    {
        if (string.IsNullOrWhiteSpace(provider)) return ([], "provider is required");
        var custom = Settings.CustomProviders.FirstOrDefault(c => c.Name == provider);
        var baseUrl = typedBaseUrl ?? Settings.BaseUrlFor(provider);
        var apiKey = typedApiKey ?? Settings.ApiKeyFor(provider) ?? "";
        // Anthropic-model endpoints take x-api-key auth (built-in Anthropic, an overridden
        // built-in, or a custom gateway on the Anthropic wire); the modal can preview an
        // unsaved API type via the override.
        var effectiveApiType = ProviderCatalog.NormalizeApiType(apiType ?? ResolveApiType(Settings, provider));
        var (models, fetchError) = await FetchModelListAsync(provider, baseUrl, apiKey, effectiveApiType, timeout, ct).ConfigureAwait(false);
        if (fetchError is not null) return ([], fetchError);
        if (models is null) return ([], null); // caller cancelled: silent, no list and no error
        var ids = models.Select(m => m.Id).ToList();
        var entries = models.Select(m => new DiscoveredModelInfo(m.Id, m.ContextWindowTokens, m.MaxOutputTokens)).ToList();
        if (custom is not null)
        {
            custom.Models = ids;
            custom.ModelMetadata = entries;
        }
        else
        {
            Settings.DiscoveredModels[provider] = entries;
        }
        ApplyProviderSelection();
        ApplyDefaultSelection();
        SaveSettings();
        return (ids, null);
    }

    /// <summary>Fetches a provider's model list without persisting anything, for the Add/Edit
    /// provider modal's List-models preview (typed URL/key/API type, unsaved by design).
    /// Null list + null error means the caller cancelled.</summary>
    public async Task<(IReadOnlyList<LlmModelInfo>? Models, string? Error)> PreviewModelsAsync(
        string provider, string baseUrl, string apiKey, string? apiType = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) return ([], "provider is required");
        var effectiveApiType = ProviderCatalog.NormalizeApiType(apiType ?? ResolveApiType(Settings, provider));
        return await FetchModelListAsync(provider, baseUrl, apiKey, effectiveApiType, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>One GET /models (or /v1/models) round-trip with URL validation, timeout and
    /// error mapping. Null list + null error means the caller cancelled.</summary>
    private static async Task<(IReadOnlyList<LlmModelInfo>? Models, string? Error)> FetchModelListAsync(
        string provider, string baseUrl, string apiKey, string effectiveApiType, TimeSpan? timeout, CancellationToken ct)
    {
        // A pasted URL with whitespace or no scheme must fail with a message, not with an
        // unhandled UriFormatException/InvalidOperationException on the caller's context.
        baseUrl = (baseUrl ?? "").Trim();
        if (baseUrl.Length == 0)
            return ([], "enter the endpoint's base URL first (for example https://gateway.example.com/v1)");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ([], $"'{baseUrl}' is not an absolute http(s) URL — include the scheme, e.g. https://host/v1");
        Action<HttpRequestMessage>? configure = null;
        if (effectiveApiType == "anthropic")
        {
            var anthropicKey = apiKey;
            configure = request =>
            {
                request.Headers.TryAddWithoutValidation("x-api-key", anthropicKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            };
        }
        try
        {
            // Discovery rides the streaming client, whose timeout is infinite (long generations);
            // without a cap of its own, one stalled GET /models wedges the Settings button on
            // "Loading…" for the whole wait. Metadata must answer fast or fail with a message.
            // The caller's token (the UI Cancel button) links in beside the timeout.
            using var discoveryCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
            discoveryCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            var models = await LlmModelDiscovery.DiscoverAsync(provider, baseUrl, apiKey, StreamingHttp, configure, discoveryCts.Token,
                anthropicModelsPath: effectiveApiType == "anthropic").ConfigureAwait(false);
            return (models, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, null); // caller cancelled: silent, no list and no error
        }
        catch (OperationCanceledException)
        {
            return ([], $"model list request timed out after {(int)(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s — {baseUrl} accepted the connection but never answered. "
                + "Check the base URL and key, or the provider may be queueing metadata requests.");
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ([], ex.Message);
        }
        catch (Exception ex)
        {
            return ([], $"model list request failed: {ex.Message}");
        }
    }

    private void RegisterRoute(LlmAdapter adapter)
    {
        if (_routeEffects.Remove(adapter.Provider, out var stale)) stale.Dispose();
        _routeEffects[adapter.Provider] = Llm.RegisterAdapter(adapter);
    }

    /// <summary>Only explicitly configured providers become routes. An environment key can
    /// authenticate a configured route but does not configure one or prove model availability.</summary>
    public static IReadOnlyList<string> DesiredRouteProviders(HarnessSettings settings)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id)) ids.Add(id);
        }
        Add(settings.Provider);
        foreach (var id in settings.ProviderKeys.Keys) Add(id);
        foreach (var id in settings.ProviderBaseUrls.Keys) Add(id);
        foreach (var id in settings.ProviderApiTypes.Keys) Add(id);
        foreach (var id in settings.DiscoveredModels.Keys) Add(id);
        foreach (var custom in settings.CustomProviders)
            if (!string.IsNullOrWhiteSpace(custom.BaseUrl)) Add(custom.Name);
        return ids;
    }

    public void ApplyProviderSelection()
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in DesiredRouteProviders(Settings))
        {
            var baseUrl = Settings.BaseUrlFor(id);
            if (string.IsNullOrWhiteSpace(baseUrl)) continue;
            RegisterRoute(BuildRoute(id, baseUrl, Settings.ApiKeyFor(id), RuntimeModels(id)));
            desired.Add(id);
        }
        var models = RuntimeModels(Settings.Provider);
        if (!models.Any(m => m.Id == Settings.Model)) Settings.Model = models.FirstOrDefault()?.Id ?? "";
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
        Sandbox.AllowUnconfinedFallback = !Settings.SandboxFailClosedWhenUnsupported;
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
        // Retry policy is live-editable: a longer rate-limit wait applies to the next failure,
        // not the next restart.
        if (Retry is not null)
        {
            Retry.Options = new Core.Retry.RetryOptions
            {
                Default = Settings.Retry,
                Providers = Settings.RetryProviders,
            };
        }
        // Workspaces are registered explicitly by the user or the CLI. Startup must
        // never turn the harness installation/working directory into a workspace.
        Workspaces ??= new WorkspaceRegistry(_home);
    }

    public void SaveSettings()
    {
        if (SettingsLoadError is not null) throw new InvalidOperationException(SettingsLoadError);
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var temporaryPath = Path.Combine(_home, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
                Settings = JsonSerializer.Deserialize<HarnessSettings>(File.ReadAllText(SettingsFilePath),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                    ?? throw new JsonException("settings must be an object");
            if (Settings.Provider is null || Settings.Model is null || Settings.BaseUrl is null
                || Settings.ProviderKeys is null || Settings.ProviderBaseUrls is null
                || Settings.ProviderApiTypes is null || Settings.DiscoveredModels is null
                || Settings.CustomProviders is null || Settings.DiscoveredModels.Values.Any(m => m is null)
                || Settings.CustomProviders.Any(c => c is null || c.Models is null || c.ModelMetadata is null))
                throw new JsonException("provider settings cannot be null");
            MigrateLegacySettings(Settings);
            ApplyPatches(Settings, _home);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Settings = new HarnessSettings();
            SettingsLoadError = $"Settings could not be loaded from {SettingsFilePath} ({ex.GetType().Name}). "
                + "The existing file was left unchanged. Repair or move it and restart before saving settings.";
            Console.Error.WriteLine(SettingsLoadError);
        }
    }

    /// <summary>Renames retired provider ids so saved settings keep working across catalog changes.</summary>
    public static void MigrateLegacySettings(HarnessSettings settings)
    {
        // "zhipu" → "zai": same API under the Z.ai brand, now on the documented api.z.ai host
        // (the old default open.bigmodel.ai never resolved; open.bigmodel.cn is the China host).
        if (settings.Provider == "zhipu")
        {
            settings.Provider = "zai";
            if (settings.BaseUrl.TrimEnd('/') == "https://open.bigmodel.ai/api/paas/v4")
                settings.BaseUrl = ProviderCatalog.Info("zai")!.DefaultBaseUrl;
        }
        if (settings.ProviderKeys.Remove("zhipu", out var zhipuKey) && !settings.ProviderKeys.ContainsKey("zai"))
            settings.ProviderKeys["zai"] = zhipuKey;
        if (settings.DiscoveredModels.Remove("zhipu", out var zhipuModels) && !settings.DiscoveredModels.ContainsKey("zai"))
            settings.DiscoveredModels["zai"] = zhipuModels;

        // zai-coding moved from the chat-completions wire (/api/coding/paas/v4) to the Responses
        // wire (/api/v1): both buffer the whole completion server-side on the coding plan — minutes
        // of silence at large context — while /api/v1 streams reasoning deltas incrementally
        // (verified live). Rewrite a stashed legacy URL so existing installs move with it.
        const string legacyCodingUrl = "https://api.z.ai/api/coding/paas/v4";
        if (settings.BaseUrlProvider == "zai-coding" && settings.BaseUrl.TrimEnd('/') == legacyCodingUrl)
            settings.BaseUrl = ProviderCatalog.Info("zai-coding")!.DefaultBaseUrl;
        if (settings.ProviderBaseUrls.TryGetValue("zai-coding", out var stashed) && stashed.TrimEnd('/') == legacyCodingUrl)
            settings.ProviderBaseUrls["zai-coding"] = ProviderCatalog.Info("zai-coding")!.DefaultBaseUrl;

        MigrateRetiredCompatibleSlot(settings);
    }

    /// <summary>Moves a retired generic slot onto the catalog provider it actually points at:
    /// when the slot's URL equals a built-in default (a Mimo endpoint configured before the
    /// Mimo entry existed), the selection, key stash and discovered list move to that id.
    /// Anything else stays on the hidden legacy id and keeps routing until the user
    /// switches away — never silently repointed. Idempotent.</summary>
    private static void MigrateRetiredCompatibleSlot(HarnessSettings settings)
    {
        const string retired = "openai-compatible";
        string? slotUrl = null;
        if (settings.Provider == retired && settings.BaseUrlProvider is null or retired)
            slotUrl = settings.BaseUrl;
        else if (settings.ProviderBaseUrls.TryGetValue(retired, out var stashedUrl))
            slotUrl = stashedUrl;
        if (string.IsNullOrWhiteSpace(slotUrl)) return;
        var match = ProviderCatalog.All.FirstOrDefault(p =>
            p.DefaultBaseUrl.TrimEnd('/').Equals(slotUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (match is null) return;
        if (settings.ProviderKeys.Remove(retired, out var key) && !settings.ProviderKeys.ContainsKey(match.Id))
            settings.ProviderKeys[match.Id] = key;
        List<DiscoveredModelInfo>? moved = null;
        if (settings.DiscoveredModels.Remove(retired, out moved))
        {
            if (settings.DiscoveredModels.TryGetValue(match.Id, out var existing))
            {
                var seen = new HashSet<string>(existing.Select(m => m.Id), StringComparer.Ordinal);
                foreach (var entry in moved)
                    if (seen.Add(entry.Id)) existing.Add(entry);
            }
            else
            {
                settings.DiscoveredModels[match.Id] = moved;
            }
        }
        if (settings.ProviderBaseUrls.Remove(retired, out var url))
            settings.ProviderBaseUrls.TryAdd(match.Id, url);
        if (settings.Provider != retired) return;
        settings.Provider = match.Id;
        if (settings.BaseUrlProvider == retired) settings.BaseUrlProvider = match.Id;
        // Preserve an explicitly saved model on the same endpoint. Only retire the old
        // synthetic placeholder if it was never part of a saved model list.
        if (settings.Model == "default" && !(moved?.Any(m => m.Id == "default") ?? false))
            settings.Model = "";
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Agents is not null)
                foreach (var agent in Agents.LiveAgents()) await agent.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (Context is not null) await Context.DisposeAsync().ConfigureAwait(false);
            if (_ownedPersistence is { } persistence)
            {
                try { await persistence.FlushAllAsync().ConfigureAwait(false); }
                finally
                {
                    if (persistence is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    else if (persistence is IDisposable disposable) disposable.Dispose();
                    _ownedPersistence = null;
                }
            }
        }
    }
}

/// <summary>A built-in provider route: display metadata plus defaults for the Settings UI.</summary>
/// <param name="Category">cloud | local — drives the grouped picker ("generic" survives on the legacy record only).</param>
/// <param name="DefaultApiType">"openai" (OpenAI-compatible) or "anthropic" (Anthropic Messages API).</param>
public sealed record ProviderInfo(
    string Id,
    string Name,
    string Category,
    string DefaultBaseUrl,
    string? ApiKeyEnv = null,
    string DefaultApiType = "openai")
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
        // Cloud (hosted APIs)
        new("openai", "OpenAI", "cloud", "https://api.openai.com/v1", "OPENAI_API_KEY", "responses"),
        new("anthropic", "Anthropic", "cloud", "https://api.anthropic.com", "ANTHROPIC_API_KEY", "anthropic"),
        new("xai", "xAI (Grok)", "cloud", "https://api.x.ai/v1", "XAI_API_KEY", "responses"),
        new("google", "Google (Gemini)", "cloud", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY"),
        new("mistral", "Mistral AI", "cloud", "https://api.mistral.ai/v1", "MISTRAL_API_KEY"),
        new("perplexity", "Perplexity", "cloud", "https://api.perplexity.ai", "PERPLEXITY_API_KEY"),
        new("together", "Together AI", "cloud", "https://api.together.ai/v1", "TOGETHER_API_KEY"),
        new("groq", "Groq", "cloud", "https://api.groq.com/openai/v1", "GROQ_API_KEY"),
        new("fireworks", "Fireworks AI", "cloud", "https://api.fireworks.ai/inference/v1", "FIREWORKS_API_KEY"),
        new("openrouter", "OpenRouter (aggregator)", "cloud", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY"),
        new("cerebras", "Cerebras", "cloud", "https://api.cerebras.ai/v1", "CEREBRAS_API_KEY"),
        new("cohere", "Cohere", "cloud", "https://api.cohere.ai/compatibility/v1", "COHERE_API_KEY"),
        new("deepseek", "DeepSeek", "cloud", "https://api.deepseek.com", "DEEPSEEK_API_KEY"),
        new("qwen", "Alibaba Qwen (DashScope)", "cloud", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "DASHSCOPE_API_KEY"),
        // Qwen also sells subscription token plans on a dedicated MaaS endpoint (same split as
        // zai/zai-coding): a plan key against the pay-as-you-go DashScope route burns no plan quota.
        new("qwen-token-plan", "Alibaba Qwen (Token Plan)", "cloud", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", "QWEN_TOKEN_PLAN_API_KEY"),
        new("moonshot", "Moonshot AI (Kimi)", "cloud", "https://api.moonshot.ai/v1", "MOONSHOT_API_KEY"),
        // Z.ai ships two routes with separate keys/quotas: pay-as-you-go API and the GLM Coding Plan
        // subscription. The coding plan speaks three wires — Anthropic Messages (/api/anthropic),
        // chat completions (/api/coding/paas/v4) and Responses (/api/v1) — but only /api/v1 streams
        // reasoning incrementally (verified live); the other two buffer the whole completion
        // server-side, which is minutes of silence at large context. Same key on all three.
        new("zai", "Z.ai API (GLM)", "cloud", "https://api.z.ai/api/paas/v4", "ZAI_API_KEY"),
        new("zai-coding", "Z.ai Coding Plan (GLM)", "cloud", "https://api.z.ai/api/v1", "ZAI_CODING_API_KEY", "responses"),
        // The coding plan is sold per region: intl (api.z.ai) and China (open.bigmodel.cn) take
        // separate subscriptions and keys. The CN host is measurably faster at cold-start prefill
        // for large contexts — the same route Pi's zai-coding-cn provider targets.
        new("zai-coding-cn", "Z.ai Coding Plan CN (GLM)", "cloud", "https://open.bigmodel.cn/api/coding/paas/v4", "ZAI_CODING_CN_API_KEY"),
        new("minimax", "MiniMax", "cloud", "https://api.minimax.io/v1", "MINIMAX_API_KEY"),
        // Xiaomi MiMo subscription token plan; the dedicated SGP endpoint is the plan surface.
        new("mimo", "Xiaomi MiMo (Token Plan)", "cloud", "https://token-plan-sgp.xiaomimimo.com/v1", "MIMO_API_KEY"),
        new("doubao", "ByteDance Doubao (Ark)", "cloud", "https://ark.cn-beijing.volces.com/api/v3", "ARK_API_KEY"),
        new("ernie", "Baidu ERNIE (Qianfan)", "cloud", "https://qianfan.baidubce.com/v2", "QIANFAN_API_KEY"),
        new("hunyuan", "Tencent Hunyuan", "cloud", "https://api.hunyuan.cloud.tencent.com/v1", "HUNYUAN_API_KEY"),
        new("stepfun", "StepFun", "cloud", "https://api.stepfun.com/v1", "STEPFUN_API_KEY"),
        // Local / self-hosted
        new("ollama", "Ollama (local)", "local", "http://localhost:11434/v1"),
        new("lmstudio", "LM Studio (local)", "local", "http://localhost:1234/v1"),
        new("omlx", "oMLX (local, MLX)", "local", "http://localhost:8000/v1"),
        // Unsloth Studio serves an OpenAI-compatible API from the local app; auth uses a key from its UI.
        new("unsloth", "Unsloth Studio (local)", "local", "http://localhost:8888/v1", "UNSLOTH_API_KEY"),
    ];

    /// <summary>The retired generic slot: no longer offered for new selections (the Custom
    /// providers tab is the path for extra endpoints), but existing configs keep routing
    /// until the user switches away — resolving metadata keeps them working verbatim.</summary>
    private static readonly ProviderInfo LegacyCompatibleSlot =
        new("openai-compatible", "Custom OpenAI-compatible (legacy)", "generic", "https://gateway.example.com/v1");

    public static readonly IReadOnlyList<string> Providers = [.. All.Select(p => p.Id)];

    public static ProviderInfo? Info(string provider) =>
        All.FirstOrDefault(p => p.Id == provider)
        ?? (provider == LegacyCompatibleSlot.Id ? LegacyCompatibleSlot : null);

    /// <summary>Only cloud routes demand a key up front; local servers and open gateways
    /// stream keyless and let the server reject them if it actually wants auth.</summary>
    public static bool RequiresApiKey(string provider) => Info(provider)?.Category == "cloud";

    /// <summary>
    /// Whether a provider defaults to Responses for every model. Automatic compatible routes
    /// choose a protocol per model; explicit API types can select Responses on any gateway.
    /// </summary>
    public static bool UsesResponsesApi(string provider) => Info(provider)?.DefaultApiType == "responses";

    /// <summary>Normalizes a stored API type; unknown values retain automatic OpenAI compatibility.</summary>
    public static string NormalizeApiType(string? apiType)
        => apiType?.Trim().ToLowerInvariant() switch
        {
            "anthropic" => "anthropic",
            "responses" => "responses",
            _ => "openai",
        };

    /// <summary>Short display label for an API type value.</summary>
    public static string ApiTypeLabel(string? apiType)
        => NormalizeApiType(apiType) switch
        {
            "anthropic" => "Anthropic",
            "responses" => "OpenAI Responses",
            _ => "OpenAI-compatible",
        };

    public static IReadOnlyList<string> Categories => ["cloud", "local"];

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
        "qwen" or "qwen-token-plan" =>
        [
            new LlmModelInfo(provider, "qwen3-max", "Qwen3 Max", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768),
            new LlmModelInfo(provider, "qwen3-plus", "Qwen3 Plus", ContextWindowTokens: 131_072, MaxOutputTokens: 16_384),
            new LlmModelInfo(provider, "qwen3-coder-plus", "Qwen3 Coder Plus", ContextWindowTokens: 262_144, MaxOutputTokens: 32_768,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "qwen2.5-coder-32b-instruct", "Qwen2.5 Coder 32B", ContextWindowTokens: 131_072, MaxOutputTokens: 8_192),
        ],
        "moonshot" =>
        [
            new LlmModelInfo(provider, "kimi-k3", "Kimi K3", ContextWindowTokens: 1_048_576,
                SupportsReasoning: true, ReasoningEfforts: ["low", "high", "max"], DefaultEffort: "max"),
            new LlmModelInfo(provider, "kimi-k2.7-code", "Kimi K2.7 Code", ContextWindowTokens: 262_144,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "kimi-k2.6", "Kimi K2.6", ContextWindowTokens: 262_144),
        ],
        "zai" or "zai-coding" or "zai-coding-cn" =>
        [
            new LlmModelInfo(provider, "glm-5.3", "GLM-5.3", ContextWindowTokens: 1_048_576, MaxOutputTokens: 131_072,
                SupportsReasoning: true, ReasoningEfforts: ["low", "high", "max"], DefaultEffort: "max"),
            new LlmModelInfo(provider, "glm-5.3-flash", "GLM-5.3 Flash", ContextWindowTokens: 1_048_576, MaxOutputTokens: 131_072,
                SupportsReasoning: true, ReasoningEfforts: ["low", "high", "max"], DefaultEffort: "max"),
            new LlmModelInfo(provider, "glm-5.1", "GLM-5.1", ContextWindowTokens: 204_800, MaxOutputTokens: 131_072,
                SupportsReasoning: true, ReasoningEfforts: ["low", "high", "max"], DefaultEffort: "high"),
        ],
        "minimax" =>
        [
            new LlmModelInfo(provider, "MiniMax-M3", "MiniMax M3", ContextWindowTokens: 1_048_576,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "MiniMax-M2.7", "MiniMax M2.7", ContextWindowTokens: 204_800,
                SupportsReasoning: true, ReasoningEfforts: OpenAiEfforts, DefaultEffort: "medium"),
        ],
        "mimo" =>
        [
            // From the token-plan endpoint's live /models listing; the asr/tts variants it also
            // publishes are not chat models. Context/output caps unpublished — the token meter
            // falls through to per-request declarations.
            new LlmModelInfo(provider, "mimo-v2.5-pro", "MiMo 2.5 Pro"),
            new LlmModelInfo(provider, "mimo-v2.5", "MiMo 2.5"),
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
        // Retired slot: seeds for configs still on the legacy id until they switch away.
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
