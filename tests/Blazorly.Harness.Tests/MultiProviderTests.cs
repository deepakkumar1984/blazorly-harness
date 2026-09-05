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
