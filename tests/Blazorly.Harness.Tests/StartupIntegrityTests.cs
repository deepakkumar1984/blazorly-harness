using System.Net;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SettingsPage = Blazorly.Harness.Web.Components.Pages.Settings;
using HomePage = Blazorly.Harness.Web.Components.Pages.Home;

namespace Blazorly.Harness.Tests;

[Collection("BlazorlyHome")]
public sealed class StartupIntegrityTests : BootstrapperTestBase
{
    [Fact]
    public async Task FreshHome_HasNoProvidersModelsWorkspacesOrSessions_AndRendersSetup()
    {
        await using var boot = new HarnessBootstrapper();
        await boot.StartAsync(default);
        await AssertUnconfigured(boot);
        Assert.False(File.Exists(boot.SettingsFilePath));

        var settings = await Render<SettingsPage>(boot);
        Assert.Contains("no providers configured yet", settings);
        Assert.Contains(boot.SettingsFilePath, WebUtility.HtmlDecode(settings));
        Assert.DoesNotContain("DeepSeek", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key missing", settings, StringComparison.OrdinalIgnoreCase);

        var home = await Render<HomePage>(boot);
        Assert.Contains("Set up your provider", home);
        Assert.DoesNotContain("Your existing chats", home);
        await AssertUnconfigured(boot); // rendering must not populate the store
    }

    [Fact]
    public async Task DeletingAnIsolatedHome_ReturnsToEmptyState_AndLeavesWorkspaceFilesAlone()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"blazorly-integrity-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var sentinel = Path.Combine(workspace, "keep.txt");
        File.WriteAllText(sentinel, "workspace contents");
        try
        {
            await using (var boot = new HarnessBootstrapper())
            {
                await boot.StartAsync(default);
                boot.Settings.SelectProvider("", "ollama", "explicit-local-model");
                boot.ApplyProviderSelection();
                boot.ApplyDefaultSelection();
                var registered = boot.Workspaces.Ensure(workspace);
                var facade = new SessionFacade(boot, new UiEventBroker());
                facade.CreateSession(registered.Id);
                boot.SaveSettings();
                Assert.Single(boot.Llm.ListProviders());
                Assert.Single(boot.Sessions.LiveSessions());
            }

            // Home is the unique test directory created by BootstrapperTestBase.
            Directory.Delete(Home, recursive: true);
            await using var fresh = new HarnessBootstrapper();
            await fresh.StartAsync(default);
            await AssertUnconfigured(fresh);
            Assert.Equal("workspace contents", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingTheActiveProvider_PersistsAnEmptyConfiguration(bool custom)
    {
        await using (var boot = new HarnessBootstrapper())
        {
            await boot.StartAsync(default);
            var id = custom ? "private-gateway" : "ollama";
            if (custom)
                boot.Settings.CustomProviders.Add(new CustomProviderConfig
                {
                    Name = id, BaseUrl = "http://127.0.0.1:1/v1", Models = ["saved-model"], ApiKey = "test-key",
                });
            else
                boot.Settings.DiscoveredModels[id] = ["saved-model"];
            boot.Settings.SelectProvider("", id);
            boot.ApplyProviderSelection();
            boot.ApplyDefaultSelection();
            boot.SaveSettings();
            Assert.Equal(id, Assert.Single(boot.Llm.ListProviders()));

            boot.Settings.RemoveProvider(id);
            boot.ApplyProviderSelection();
            boot.ApplyDefaultSelection();
            boot.SaveSettings();
            await AssertUnconfigured(boot);
        }

        await using var restarted = new HarnessBootstrapper();
        await restarted.StartAsync(default);
        await AssertUnconfigured(restarted);
        Assert.Contains("no providers configured yet", await Render<SettingsPage>(restarted));
    }

    [Fact]
    public async Task EnvironmentKey_AuthenticatesOnlyAnExplicitlyConfiguredProvider()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-environment-key");
            await using var boot = new HarnessBootstrapper();
            await boot.StartAsync(default);
            await AssertUnconfigured(boot);

            boot.Settings.SelectProvider("", "openai", "explicit-model");
            boot.ApplyProviderSelection();
            Assert.Equal("openai", Assert.Single(boot.Llm.ListProviders()));
            Assert.Equal("test-environment-key", boot.Settings.ApiKeyFor("openai"));
            boot.Settings.RemoveProvider("openai");
            boot.ApplyProviderSelection();
            Assert.Empty(boot.Llm.ListProviders());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Theory]
    [InlineData("{ broken json")]
    [InlineData("null")]
    [InlineData("{\"providerKeys\":null}")]
    public async Task UnreadableSettings_AreReportedAndCannotBeSilentlyOverwritten(string contents)
    {
        var path = Path.Combine(Home, "settings.json");
        File.WriteAllText(path, contents);
        await using var boot = new HarnessBootstrapper();
        await boot.StartAsync(default);
        Assert.NotNull(boot.SettingsLoadError);
        await AssertUnconfigured(boot);
        Assert.Throws<InvalidOperationException>(boot.SaveSettings);
        Assert.Equal(contents, File.ReadAllText(path));
        var html = await Render<SettingsPage>(boot);
        Assert.Contains("Settings could not be loaded", html);
        Assert.Contains("role=\"alert\"", html);
    }

    [Fact]
    public async Task EmptyDiscoverySnapshot_RemainsEmpty_AfterRestart()
    {
        await using (var boot = new HarnessBootstrapper())
        {
            await boot.StartAsync(default);
            boot.Settings.SelectProvider("", "deepseek", "previous-model");
            boot.Settings.DiscoveredModels["deepseek"] = [];
            boot.ApplyProviderSelection();
            boot.ApplyDefaultSelection();
            boot.SaveSettings();
            Assert.Empty(boot.RuntimeModels("deepseek"));
            Assert.Empty(boot.Settings.Model);
        }
        await using var restarted = new HarnessBootstrapper();
        await restarted.StartAsync(default);
        Assert.Empty(restarted.RuntimeModels("deepseek"));
        Assert.Empty(restarted.Llm.ListModels("deepseek"));
        Assert.Empty(restarted.Settings.Model);
    }

    [Fact]
    public async Task ExplicitLegacySelection_IsTheOnlyModelOffered_AndSurvivesSwitching()
    {
        File.WriteAllText(Path.Combine(Home, "settings.json"), """{"provider":"deepseek","model":"private-model-id"}""");
        await using var boot = new HarnessBootstrapper();
        await boot.StartAsync(default);
        Assert.Equal("private-model-id", Assert.Single(boot.RuntimeModels("deepseek")).Id);
        boot.Settings.SelectProvider("deepseek", "openai");
        Assert.Empty(boot.Settings.Model);
        Assert.Empty(boot.RuntimeModels("openai"));
        boot.Settings.SelectProvider("openai", "deepseek");
        Assert.Equal("private-model-id", boot.Settings.Model);
    }

    [Fact]
    public async Task NoProvider_PromptFailsWithSetupInstructions_WithoutAnAssistantResult()
    {
        await using var boot = new HarnessBootstrapper();
        await boot.StartAsync(default);
        var agent = boot.Loop.Create();
        agent.Followup(Message.CreateUserText("hello"));
        await agent.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var reason = SessionEventRead.TurnEndReasonOf(agent.Session.Events.Last(e => e.Type == SessionEventTypes.TurnEnd));
        var error = Assert.IsType<TurnEndReason.Error>(reason);
        Assert.Equal(LlmErrorCodes.NoAdapter, error.Code);
        Assert.Contains("No provider configured", error.Message);
        Assert.DoesNotContain(agent.Session.Events, e => e.Type == SessionEventTypes.AssistantMessage);
        Assert.Empty(boot.Llm.ListProviders());
    }

    private static async Task AssertUnconfigured(HarnessBootstrapper boot)
    {
        Assert.Empty(boot.Settings.Provider);
        Assert.Empty(boot.Settings.Model);
        Assert.Empty(boot.Settings.BaseUrl);
        Assert.Empty(boot.Llm.ListProviders());
        Assert.Empty(boot.RuntimeModels("deepseek"));
        Assert.Empty(boot.RuntimeModels("openai"));
        Assert.Empty(boot.Workspaces.List());
        Assert.Empty(boot.Sessions.LiveSessions());
        Assert.Empty(await boot.Sessions.ListPersistedAsync());
    }

    private static async Task<string> Render<T>(HarnessBootstrapper boot) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new SessionFacade(boot, new UiEventBroker()));
        services.AddSingleton<NavigationManager>(new TestNavigation());
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>();
            return component.ToHtmlString();
        });
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/settings");
        protected override void NavigateToCore(string uri, bool forceLoad)
            => throw new InvalidOperationException($"Unexpected navigation to {uri}");
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }
}
