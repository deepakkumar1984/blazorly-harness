using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>Multi-provider routing: several providers keep live routes at once so the
/// session topbar can switch models across all of them.</summary>
public class MultiProviderTests
{
    [Fact]
    public void ApiKeyFor_ResolvesPerProviderWithoutCrossLeak()
    {
        try
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            var settings = new HarnessSettings { Provider = "deepseek", ApiKey = "sk-field" };
            settings.ProviderKeys["xai"] = "sk-xai-stash";

            // The typed field only applies to the active provider.
            Assert.Equal("sk-field", settings.ApiKeyFor("deepseek"));
            Assert.Equal("sk-field", settings.EffectiveApiKey);
            Assert.Equal("sk-xai-stash", settings.ApiKeyFor("xai"));
            // An unrelated provider never inherits another provider's stash.
            Assert.Null(settings.ApiKeyFor("mistral"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void SelectProvider_DoesNotCarryThePreviousRoutesUrlOrKey()
    {
        try
        {
            Environment.SetEnvironmentVariable("ZAI_API_KEY", null);
            const string gateway = "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1";
            // The CLI override path: --provider zai while settings still point at a custom gateway.
            var settings = new HarnessSettings
            {
                Provider = "openai-compatible",
                Model = "deepseek-v4.1-flash",
                ApiKey = "sk-gateway",
                BaseUrl = gateway,
            };
            settings.ProviderKeys["zai"] = "zai-key";

            settings.SelectProvider("openai-compatible", "zai");

            Assert.Equal("zai", settings.Provider);
            Assert.Equal(ProviderCatalog.Info("zai")!.DefaultBaseUrl, settings.BaseUrlFor("zai")); // the gateway stayed with its own route
            Assert.Equal("zai", settings.BaseUrlProvider);
            Assert.Equal(gateway, settings.ProviderBaseUrls["openai-compatible"]);
            Assert.Equal("zai-key", settings.ApiKey);
            Assert.Equal("sk-gateway", settings.ProviderKeys["openai-compatible"]); // stashed, never sent to z.ai
            Assert.Equal(ProviderCatalog.DefaultModel("zai"), settings.Model);

            // Switching back restores the stashed endpoint; an explicit model override wins over the default.
            settings.SelectProvider("zai", "openai-compatible", "glm-5.3");
            Assert.Equal("glm-5.3", settings.Model);
            Assert.Equal(gateway, settings.BaseUrlFor("openai-compatible"));
            Assert.Equal("zai-key", settings.ApiKeyFor("zai"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAI_API_KEY", null);
        }
    }

    [Fact]
    public void SelectProvider_SettingsSwitchNeverCarriesAProviderEndpointToAnotherProvider()
    {
        // Reported bug: switching providers in the Settings dropdown kept the previous provider's
        // custom base URL (any non-catalog URL was treated as a shared gateway), so the new
        // provider's key went to the old provider's host and the turn hung on an endpoint that
        // never answered — even with a valid URL and key typed for the new provider.
        const string mimo = "https://mimo.example.com/v1";
        var ui = new HarnessSettings { Provider = "openai-compatible", ApiKey = "sk-mimo", BaseUrl = mimo };

        ui.SelectProvider("openai-compatible", "deepseek");

        Assert.Equal("deepseek", ui.Provider);
        Assert.Equal(ProviderCatalog.Info("deepseek")!.DefaultBaseUrl, ui.BaseUrlFor("deepseek"));
        Assert.Equal("deepseek", ui.BaseUrlProvider);
        Assert.Equal(mimo, ui.ProviderBaseUrls["openai-compatible"]); // the endpoint stayed with its own route
        Assert.Null(ui.ApiKey); // no key typed for deepseek yet — never mimo's

        // Switching back restores the typed endpoint, so nothing needs re-typing.
        ui.SelectProvider("deepseek", "openai-compatible");
        Assert.Equal(mimo, ui.BaseUrlFor("openai-compatible"));
        Assert.Equal("sk-mimo", ui.ApiKey);
    }

    [Fact]
    public void BaseUrlFor_BackgroundRoutesUseTheProvidersOwnStashedUrl()
    {
        // A provider configured with a custom URL while active, then switched away from, must keep
        // that URL when its route is rebuilt in the background (session topbar switches hit it).
        const string proxy = "https://zai-proxy.internal/v1";
        var settings = new HarnessSettings { Provider = "zai", BaseUrl = proxy };
        settings.SelectProvider("zai", "deepseek"); // stashes the proxy under zai

        Assert.Equal(ProviderCatalog.Info("deepseek")!.DefaultBaseUrl, settings.BaseUrlFor("deepseek"));
        Assert.Equal(proxy, settings.BaseUrlFor("zai")); // not the catalog default
    }

    [Fact]
    public void SelectProvider_SwapsACatalogDefaultUrlAndRestoresLegacySettings()
    {
        var settings = new HarnessSettings
        {
            Provider = "deepseek",
            ApiKey = "sk-ds",
            BaseUrl = ProviderCatalog.Info("deepseek")!.DefaultBaseUrl,
        };

        settings.SelectProvider("deepseek", "zai");
        Assert.Equal(ProviderCatalog.Info("zai")!.DefaultBaseUrl, settings.BaseUrl);
        Assert.Equal("zai", settings.BaseUrlProvider);
        Assert.Equal(ProviderCatalog.Info("deepseek")!.DefaultBaseUrl, settings.ProviderBaseUrls["deepseek"]);

        // Legacy settings.json has no baseUrlProvider: the URL belongs to the active provider.
        var legacy = new HarnessSettings { Provider = "openai-compatible", BaseUrl = "https://gw.internal/v1" };
        Assert.Equal("https://gw.internal/v1", legacy.BaseUrlFor("openai-compatible"));
    }

    [Fact]
    public void ApiKeyFor_EnvFallbacksFollowTheDocumentedRules()
    {
        try
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", "sk-env");
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-ds-env");
            var settings = new HarnessSettings { Provider = "deepseek" };

            Assert.Equal("sk-env", settings.ApiKeyFor("xai")); // catalog env hint
            Assert.Equal("sk-ds-env", settings.ApiKeyFor("deepseek")); // provider-specific env
            Assert.Equal("sk-ds-env", settings.ApiKeyFor("openai")); // documented deepseek→openai legacy fallback
            Assert.Null(settings.ApiKeyFor("anthropic")); // never inherits an unrelated key
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
        }
    }

    [Fact]
    public void DesiredRoutes_ActiveLocalsKeyedCloudAndCustoms()
    {
        try
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            var settings = new HarnessSettings { Provider = "deepseek" };
            settings.ProviderKeys["deepseek"] = "sk-ds";
            settings.ProviderKeys["xai"] = "sk-xai";
            settings.CustomProviders.Add(new CustomProviderConfig { Name = "my-gateway", BaseUrl = "http://localhost:9999/v1" });

            var routes = HarnessBootstrapper.DesiredRouteProviders(settings);

            // Active provider, keyed cloud providers, every local server, custom gateways.
            Assert.Contains("deepseek", routes);
            Assert.Contains("xai", routes);
            foreach (var local in new[] { "ollama", "lmstudio", "omlx", "unsloth" })
                Assert.Contains(local, routes);
            Assert.Contains("my-gateway", routes);
            // Keyless cloud routes and the generic placeholder stay out.
            Assert.DoesNotContain("openai", routes);
            Assert.DoesNotContain("zai", routes);
            Assert.DoesNotContain("openai-compatible", routes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void DesiredRoutes_GenericOnlyWhenActive()
    {
        var settings = new HarnessSettings { Provider = "openai-compatible" };
        Assert.Contains("openai-compatible", HarnessBootstrapper.DesiredRouteProviders(settings));

        settings.Provider = "deepseek";
        Assert.DoesNotContain("openai-compatible", HarnessBootstrapper.DesiredRouteProviders(settings));
    }

    [Fact]
    public void DesiredRoutes_EnvKeyIsEnoughForACloudRoute()
    {
        try
        {
            Environment.SetEnvironmentVariable("ZAI_API_KEY", "sk-zai-env");
            var settings = new HarnessSettings { Provider = "deepseek" };
            Assert.Contains("zai", HarnessBootstrapper.DesiredRouteProviders(settings));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAI_API_KEY", null);
        }
    }
}
