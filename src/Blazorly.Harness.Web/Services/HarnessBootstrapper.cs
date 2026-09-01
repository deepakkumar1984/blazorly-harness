using System.Collections.Concurrent;
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
    public string Provider { get; set; } = "replay";
    public string Model { get; set; } = "demo";
    public string? ApiKey { get; set; }
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
    public bool EnableAskUser { get; set; } = true;
    public bool EnableSessionQuery { get; set; } = true;
    public bool EnableProjectInstructions { get; set; } = true;
    public bool EnableAutoTitles { get; set; } = true;
    public bool EnableSpill { get; set; } = true;
    public int SpillThresholdChars { get; set; } = 20_000;
    public bool EnableSchedule { get; set; } = true;
    public bool EnableMcp { get; set; } = true;

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
    public AgentRuntime Agents { get; private set; } = null!;
    public ToolRuntime Tools { get; private set; } = null!;
    public LlmRuntime Llm { get; private set; } = null!;
    public SandboxPolicy Sandbox { get; private set; } = null!;
    public WorkspaceRegistry Workspaces { get; private set; } = null!;
    public HarnessSettings Settings { get; private set; } = new();
    public ReplayAdapter Replay { get; private set; } = null!;

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
    {
        LoadSettings();
        Context = HarnessContext.CreateRoot();
        Context.Events.OnListenerError = (_, ex) => Console.Error.WriteLine($"[harness] listener error: {ex.Message}");

        Llm = LlmRuntime.Mount(Context);
        var prompt = SystemPromptService.Mount(Context);
        Tools = ToolRuntime.Mount(Context, prompt);
        Sandbox = new SandboxPolicy { DefaultMode = Settings.SandboxMode };
        Core.Sessions.ISessionPersistence persistence = Settings.Persistence == "sqlite"
            ? new SqliteSessionPersistence(Path.Combine(_home, "sessions.db"))
            : new JsonlSessionPersistence(Path.Combine(_home, "sessions"));
        Sessions = SessionStore.Mount(Context, persistence);
        Agents = AgentRuntime.Mount(Context);
        var approval = ApprovalService.Mount(Context);
        Approval = approval;
        UserQuestions = UserQuestionsService.Mount(Context);
        Loop = AgentLoopService.Mount(Context);
        Loop.RegisterDefaultPrompt();

        // The replay provider is always mounted: keyless demo and tests.
        Replay = new ReplayAdapter(DemoScript.Respond);
        Llm.RegisterAdapter(Replay);

        ApplyProviderSelection();

        new BuiltInToolsPlugin(new FsObservationTracker(), Sandbox).Apply(Context);

        // --- capability plugins (each optional via settings) ---
        Jobs = JobsRuntime.Mount(Context);
        Subagents = Core.Subagents.SubagentService.Mount(Context);
        ToolPolicy = Core.Tools.ToolPolicyService.Mount(Context);
        if (Settings.TelemetryEnabled)
        {
            Telemetry = Core.Telemetry.UsageTelemetryService.Mount(Context, Path.Combine(_home, "telemetry.json"), enabled: true);
        }
        Compaction = Core.Compaction.CompactionService.Mount(Context, new Core.Compaction.CompactionOptions
        {
            ContextWindowTokens = Settings.ContextWindowTokens,
            Threshold = Settings.CompactionThreshold,
            PrunerChars = Settings.CompactionPrunerChars,
        });
        // Order matters: compaction owns context-overflow recovery; the retry policy handles
        // everything else before the driver's built-in default.
        Retry = Core.Retry.RetryService.Mount(Context, new Core.Retry.RetryOptions { Default = Settings.Retry });
        Credentials = Core.Credentials.CredentialsService.Mount(Context, Path.Combine(_home, "credentials.json"));
        Attachments = Core.Attachments.AttachmentService.Mount(Context, Path.Combine(_home, "attachments"));
        if (Settings.EnableProjectInstructions)
        {
            Instructions = Core.Instructions.ProjectInstructionsService.Mount(Context, _home);
        }
        Meter = Core.TokenMeter.TokenMeterService.Mount(Context);
        Meter.ContextWindowTokens = Settings.ContextWindowTokens;
        if (Settings.EnableSpill)
        {
            Spills = Core.Spill.SpillService.Mount(Context, Path.Combine(_home, "spills"),
                new Core.Spill.SpillOptions { ThresholdChars = Settings.SpillThresholdChars });
        }
        RepeatGuard = Core.Guards.RepeatCallGuard.Mount(Context);
        if (Settings.EnableSchedule)
        {
            Schedules = Core.Schedule.ScheduleService.Mount(Context);
        }
        if (Settings.EnableMcp)
        {
            Mcp = Core.Mcp.McpClientService.Mount(Context,
                new Core.Mcp.McpOptions { ConfigPath = Path.Combine(_home, "mcp.json") });
        }

        new JobsPlugin().Apply(Context);
        if (Settings.EnableAskUser) new AskUserPlugin().Apply(Context);
        new SubagentToolsPlugin().Apply(Context);
        if (Settings.EnableWeb) new WebPlugin().Apply(Context);
        if (Settings.EnableSkills) new SkillPlugin().Apply(Context);
        if (Settings.EnableSessionQuery) new SessionQueryPlugin().Apply(Context);
        if (Settings.EnableGoals) new GoalPlugin().Apply(Context);
        if (Settings.EnablePlanMode) new PlanModePlugin().Apply(Context);
        if (Settings.EnableCodeMode) new CodeModePlugin().Apply(Context);
        if (Settings.EnableTerminals) new TerminalPlugin().Apply(Context);
        if (Settings.EnableLsp) new LspPlugin().Apply(Context);
        if (Settings.EnableWorkflows) new WorkflowPlugin().Apply(Context);
        if (Settings.EnableTeams) new TeamPlugin().Apply(Context);
        if (Settings.EnableE2b && Settings.ResolveE2bApiKey() is { Length: > 0 } e2bKey)
        {
            RemoteSandbox = new RemoteSandboxTool(new Core.RemoteSandbox.E2bSandboxClient(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                new Core.RemoteSandbox.E2bOptions
                {
                    ApiKey = e2bKey,
                    Template = Settings.E2bTemplate,
                    BaseUrl = Settings.E2bBaseUrl,
                }));
            Context.Get<ToolRuntime>(ToolRuntime.ServiceKey).Register(RemoteSandbox);
        }
        if (Settings.EnableHooks && File.Exists(Path.Combine(_home, "hooks.json")))
        {
            new HooksPlugin(Path.Combine(_home, "hooks.json")).Apply(Context);
        }
        if (Settings.EnableAutoTitles)
        {
            Titles = Core.Sessions.SessionTitleService.Mount(Context);
        }

        ApplyDefaultSelection();
        return ReattachPersistedSessionsAsync();
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

    private void RegisterRoute(LlmAdapter adapter)
    {
        if (_routeEffects.Remove(adapter.Provider, out var stale)) stale.Dispose();
        _routeEffects[adapter.Provider] = Llm.RegisterAdapter(adapter);
    }

    public void ApplyProviderSelection()
    {
        var desired = new HashSet<string>(StringComparer.Ordinal);
        if (Settings.Provider != "replay")
        {
            RegisterRoute(BuildRoute(Settings.Provider, Settings.BaseUrl, Settings.EffectiveApiKey,
                ProviderCatalog.For(Settings.Provider, Settings.BaseUrl)));
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

public static class ProviderCatalog
{
    /// <summary>dsh llm-deepseek reasoning.efforts (off/low/high/max, default high).</summary>
    private static readonly string[] DeepSeekEfforts = ["off", "low", "high", "max"];
    /// <summary>OpenAI-style reasoning_effort pass-through for OpenAI-compatible routes.</summary>
    private static readonly string[] OpenAiEfforts = ["minimal", "low", "medium", "high", "xhigh", "max"];

    public static IReadOnlyList<LlmModelInfo> For(string provider, string baseUrl) => provider switch
    {
        "deepseek" =>
        [
            new LlmModelInfo(provider, "deepseek-v4-flash", "DeepSeek V4 Flash (fast)", ContextWindowTokens: 131_072, MaxOutputTokens: 8_192,
                SupportsReasoning: true, ReasoningEfforts: DeepSeekEfforts, DefaultEffort: "low"),
            new LlmModelInfo(provider, "deepseek-v4-pro", "DeepSeek V4 Pro", ContextWindowTokens: 131_072, MaxOutputTokens: 8_192,
                SupportsReasoning: true, ReasoningEfforts: DeepSeekEfforts, DefaultEffort: "high"),
            new LlmModelInfo(provider, "deepseek-v4-flash-vision-exp", "DeepSeek V4 Flash Vision (experimental)", ContextWindowTokens: 131_072, MaxOutputTokens: 8_192,
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
        "openai-compatible" =>
        [
            new LlmModelInfo(provider, "default", $"{baseUrl} (default model)", ReasoningEfforts: OpenAiEfforts),
        ],
        _ => [],
    };

    public static string DefaultModel(string provider) => provider switch
    {
        "replay" => "demo",
        "deepseek" => "deepseek-v4-flash",
        "openai" => "gpt-4.1-mini",
        "anthropic" => "claude-sonnet-4-5",
        _ => "default",
    };

    public static readonly IReadOnlyList<string> Providers = ["replay", "deepseek", "openai", "anthropic", "openai-compatible"];
}

/// <summary>The keyless demo script: a small deterministic agent run over the real pipeline.</summary>
public static class DemoScript
{
    private static readonly ConcurrentDictionary<string, int> Step = new(StringComparer.Ordinal);

    public static IReadOnlyList<StreamChunk> Respond(GenerateOptions options)
    {
        if (options.Purpose == "session-title")
        {
            return ReplayScript.Text("Blazorly demo run");
        }
        var hasToolResults = options.Messages.SelectMany(m => m.Content).OfType<Llm.ToolResultBlock>().Any();
        var step = Step.AddOrUpdate(options.SessionId ?? "anon", 1, (_, current) => current + 1);
        if (!hasToolResults && step == 1)
        {
            return ReplayScript.ToolCalls(
                ("bash", new { command = "sleep 2.5 && echo \"hello from blazorly harness\" && date", description = "Greet and print the date" }),
                ("todo_write", new { todos = new object[]
                    {
                        new { content = "Run the demo greeting", status = "completed" },
                        new { content = "Summarize the result", status = "in_progress" },
                    } }));
        }
        var bashOutput = options.Messages
            .SelectMany(m => m.Content).OfType<Llm.ToolResultBlock>()
            .SelectMany(b => b.Content).OfType<TextBlock>()
            .Select(t => t.Text).FirstOrDefault() ?? "";
        var summary = bashOutput.Contains("hello from blazorly harness")
            ? "The demo run completed: I executed `bash` (you can expand the card above to inspect it) and updated the todo list. This provider is `replay` — configure a real provider in Settings to use a live model."
            : "The demo run completed.";
        return ReplayScript.Text(summary);
    }
}
