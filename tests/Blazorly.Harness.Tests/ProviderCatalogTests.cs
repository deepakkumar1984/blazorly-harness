using System.Text.Json;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>Provider catalog expansion: built-in routes, metadata sanity, and per-provider key resolution.</summary>
public class ProviderCatalogTests
{
    [Fact]
    public void All_ProviderIdsAreUnique()
    {
        var ids = ProviderCatalog.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Providers_ListMatchesCatalog()
    {
        Assert.Equal([.. ProviderCatalog.All.Select(p => p.Id)], [.. ProviderCatalog.Providers]);
    }

    [Fact]
    public void Categories_CoverEveryProvider()
    {
        var known = new HashSet<string>(ProviderCatalog.Categories);
        Assert.All(ProviderCatalog.All, p => Assert.Contains(p.Category, known));
    }

    [Fact]
    public void For_ModelEntriesCarryOwnProviderIdAndUniqueModelIds()
    {
        foreach (var provider in ProviderCatalog.Providers)
        {
            var models = ProviderCatalog.For(provider, "http://localhost/v1");
            Assert.All(models, m => Assert.Equal(provider, m.Provider));
            Assert.Equal(models.Count, models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void For_DeclaredWindowsArePositive()
    {
        foreach (var provider in ProviderCatalog.Providers)
        {
            foreach (var model in ProviderCatalog.For(provider, "http://localhost/v1"))
            {
                if (model.ContextWindowTokens is { } window) Assert.True(window > 0, $"{provider}/{model.Id}: {window}");
                if (model.MaxOutputTokens is { } output) Assert.True(output > 0, $"{provider}/{model.Id}: {output}");
            }
        }
    }

    [Fact]
    public void DefaultModel_IsACatalogModelOrPlaceholder()
    {
        foreach (var provider in ProviderCatalog.Providers)
        {
            var model = ProviderCatalog.DefaultModel(provider);
            var known = ProviderCatalog.For(provider, "").Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            Assert.True(known.Contains(model) || model is "default", $"{provider}: {model}");
        }
    }

    [Fact]
    public void Catalog_CoversRequestedCompanies()
    {
        var expected = new[]
        {
            // Cloud
            "openai", "anthropic", "xai", "google", "mistral", "perplexity", "together", "groq",
            "fireworks", "openrouter", "cerebras", "cohere",
            "deepseek", "qwen", "moonshot", "zai", "zai-coding", "minimax", "doubao", "ernie", "hunyuan", "stepfun",
            // Local
            "ollama", "lmstudio", "omlx", "unsloth",
        };
        foreach (var id in expected)
        {
            Assert.Contains(id, ProviderCatalog.Providers);
            Assert.NotEmpty(ProviderCatalog.Info(id)!.Name);
        }
        // 01.AI retired the public Yi API (Sept 2026); the route must not come back silently.
        Assert.DoesNotContain("yi", ProviderCatalog.Providers);
    }

    [Fact]
    public void Catalog_GroupsCloudVersusLocal_WithoutRegionSplit()
    {
        Assert.Equal(["cloud", "local", "generic"], ProviderCatalog.Categories);
        Assert.All(ProviderCatalog.All, p =>
            Assert.Contains(p.Category, ProviderCatalog.Categories));
        // Country groupings are gone; every hosted route is one cloud bucket.
        Assert.DoesNotContain(ProviderCatalog.All, p => p.Category is "us" or "china");
    }

    [Fact]
    public void Catalog_ZaiSplitsStandardApiAndCodingPlan()
    {
        // Two routes, both documented hosts on api.z.ai, distinct keys — a coding-plan key
        // against the standard endpoint does not consume plan quota.
        var api = ProviderCatalog.Info("zai")!;
        var coding = ProviderCatalog.Info("zai-coding")!;
        Assert.Equal("https://api.z.ai/api/paas/v4", api.DefaultBaseUrl);
        Assert.Equal("https://api.z.ai/api/coding/paas/v4", coding.DefaultBaseUrl);
        Assert.Equal("ZAI_API_KEY", api.ApiKeyEnv);
        Assert.Equal("ZAI_CODING_API_KEY", coding.ApiKeyEnv);
        Assert.False(api.Local);
        Assert.False(coding.Local);
        Assert.NotEmpty(ProviderCatalog.For("zai", ""));
        Assert.NotEmpty(ProviderCatalog.For("zai-coding", ""));
    }

    [Fact]
    public void Catalog_DeepSeekWindowsMatchDsh()
    {
        var flash = ProviderCatalog.For("deepseek", "").First(m => m.Id == "deepseek-v4-flash");
        Assert.Equal(1_000_000, flash.ContextWindowTokens);
        Assert.Equal(256_000, flash.MaxOutputTokens);
    }

    [Fact]
    public void Catalog_LocalRoutersPointAtLocalDefaults()
    {
        Assert.Equal("http://localhost:11434/v1", ProviderCatalog.Info("ollama")!.DefaultBaseUrl);
        Assert.Equal("http://localhost:1234/v1", ProviderCatalog.Info("lmstudio")!.DefaultBaseUrl);
        Assert.Equal("http://localhost:8000/v1", ProviderCatalog.Info("omlx")!.DefaultBaseUrl);
        // Unsloth Studio serves OpenAI-compatible inference from the local app (port 8888),
        // gated by an sk-unsloth key generated in its UI.
        Assert.Equal("http://localhost:8888/v1", ProviderCatalog.Info("unsloth")!.DefaultBaseUrl);
        Assert.True(ProviderCatalog.Info("unsloth")!.Local);
        Assert.Equal("UNSLOTH_API_KEY", ProviderCatalog.Info("unsloth")!.ApiKeyEnv);
        Assert.True(ProviderCatalog.Info("ollama")!.Local);
        Assert.Null(ProviderCatalog.Info("ollama")!.ApiKeyEnv);
    }

    [Fact]
    public void MigrateLegacySettings_ZhipuBecomesZai()
    {
        var settings = new HarnessSettings
        {
            Provider = "zhipu",
            BaseUrl = "https://open.bigmodel.ai/api/paas/v4",
        };
        settings.ProviderKeys["zhipu"] = "sk-zhipu";
        settings.DiscoveredModels["zhipu"] = ["glm-4.6"];

        HarnessBootstrapper.MigrateLegacySettings(settings);

        Assert.Equal("zai", settings.Provider);
        Assert.Equal("https://api.z.ai/api/paas/v4", settings.BaseUrl);
        Assert.Equal("sk-zhipu", settings.ProviderKeys["zai"]);
        Assert.Equal(["glm-4.6"], settings.DiscoveredModels["zai"]);
        Assert.False(settings.ProviderKeys.ContainsKey("zhipu"));
    }

    [Fact]
    public void MigrateLegacySettings_KeepsCustomZhipuBaseUrl()
    {
        // A user pinned to the China host (open.bigmodel.cn) keeps their URL; only the
        // retired never-resolved default is rewritten.
        var settings = new HarnessSettings
        {
            Provider = "zhipu",
            BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
        };

        HarnessBootstrapper.MigrateLegacySettings(settings);

        Assert.Equal("zai", settings.Provider);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", settings.BaseUrl);
    }

    [Fact]
    public void MigrateLegacySettings_LeavesOtherProvidersAlone()
    {
        var settings = new HarnessSettings { Provider = "deepseek", BaseUrl = "https://api.deepseek.com" };
        settings.ProviderKeys["deepseek"] = "sk-ds";

        HarnessBootstrapper.MigrateLegacySettings(settings);

        Assert.Equal("deepseek", settings.Provider);
        Assert.Equal("sk-ds", settings.ProviderKeys["deepseek"]);
    }

    [Fact]
    public void HarnessSettings_ProviderKeysRoundTripThroughJson()
    {
        var settings = new HarnessSettings { Provider = "xai", ApiKey = null };
        settings.ProviderKeys["deepseek"] = "sk-ds";
        settings.ProviderKeys["xai"] = "sk-xai";

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        Assert.Contains("\"providerKeys\"", json);

        var back = JsonSerializer.Deserialize<HarnessSettings>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        })!;
        Assert.Equal("sk-ds", back.ProviderKeys["deepseek"]);
        Assert.Equal("sk-xai", back.EffectiveApiKey);
    }

    [Fact]
    public void EffectiveApiKey_FieldWinsOverStashAndEnv()
    {
        try
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", "sk-env");
            var settings = new HarnessSettings { Provider = "xai" };
            settings.ProviderKeys["xai"] = "sk-stash";
            Assert.Equal("sk-stash", settings.EffectiveApiKey);

            settings.ApiKey = "sk-field";
            Assert.Equal("sk-field", settings.EffectiveApiKey);

            settings.ApiKey = null;
            settings.ProviderKeys.Remove("xai");
            Assert.Equal("sk-env", settings.EffectiveApiKey); // catalog hint: XAI_API_KEY
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", null);
        }
    }
}
