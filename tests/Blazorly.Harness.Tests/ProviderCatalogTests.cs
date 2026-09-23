using System.Text.Json;
using Blazorly.Harness.Llm;
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
        // No "generic" group: the generic slot is retired (Custom providers tab is the path).
        Assert.Equal(["cloud", "local"], ProviderCatalog.Categories);
        Assert.All(ProviderCatalog.All, p =>
            Assert.Contains(p.Category, ProviderCatalog.Categories));
        // Country groupings are gone; every hosted route is one cloud bucket.
        Assert.DoesNotContain(ProviderCatalog.All, p => p.Category is "us" or "china");
    }

    [Fact]
    public void Catalog_ZaiSplitsStandardApiAndCodingPlan()
    {
        // Two routes, both documented hosts on api.z.ai, distinct keys — a coding-plan key
        // against the standard endpoint does not consume plan quota. The coding plan runs on
        // the Responses wire (/api/v1): it is the only one of its three protocols that streams
        // reasoning incrementally (verified live; the Anthropic and chat-completions routes
        // buffer the whole completion server-side).
        var api = ProviderCatalog.Info("zai")!;
        var coding = ProviderCatalog.Info("zai-coding")!;
        Assert.Equal("https://api.z.ai/api/paas/v4", api.DefaultBaseUrl);
        Assert.Equal("https://api.z.ai/api/v1", coding.DefaultBaseUrl);
        Assert.Equal("ZAI_API_KEY", api.ApiKeyEnv);
        Assert.Equal("ZAI_CODING_API_KEY", coding.ApiKeyEnv);
        Assert.False(api.Local);
        Assert.False(coding.Local);
        Assert.True(ProviderCatalog.UsesResponsesApi("zai-coding"));
        Assert.False(ProviderCatalog.UsesResponsesApi("zai"));
        Assert.NotEmpty(ProviderCatalog.For("zai", ""));
        Assert.NotEmpty(ProviderCatalog.For("zai-coding", ""));

        // The CN-host coding plan is a separate subscription (own key) on the China gateway —
        // the route Pi's zai-coding-cn provider targets, chat-completions wire.
        var cn = ProviderCatalog.Info("zai-coding-cn")!;
        Assert.Equal("https://open.bigmodel.cn/api/coding/paas/v4", cn.DefaultBaseUrl);
        Assert.Equal("ZAI_CODING_CN_API_KEY", cn.ApiKeyEnv);
        Assert.False(ProviderCatalog.UsesResponsesApi("zai-coding-cn"));
        Assert.NotEmpty(ProviderCatalog.For("zai-coding-cn", ""));
    }

    [Fact]
    public void Catalog_QwenSplitsStandardApiAndTokenPlan()
    {
        // Two routes, distinct endpoints and keys — a token-plan key against the pay-as-you-go
        // DashScope route does not consume plan quota (same split as zai/zai-coding).
        var api = ProviderCatalog.Info("qwen")!;
        var plan = ProviderCatalog.Info("qwen-token-plan")!;
        Assert.Equal("https://dashscope-intl.aliyuncs.com/compatible-mode/v1", api.DefaultBaseUrl);
        Assert.Equal("https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", plan.DefaultBaseUrl);
        Assert.Equal("DASHSCOPE_API_KEY", api.ApiKeyEnv);
        Assert.Equal("QWEN_TOKEN_PLAN_API_KEY", plan.ApiKeyEnv);
        Assert.False(api.Local);
        Assert.False(plan.Local);
        // Both routes serve the same Qwen model line.
        Assert.NotEmpty(ProviderCatalog.For("qwen", ""));
        Assert.Equal(ProviderCatalog.For("qwen", "").Count, ProviderCatalog.For("qwen-token-plan", "").Count);
    }

    [Fact]
    public void Catalog_XiaomiMimoTokenPlanRoute()
    {
        var mimo = ProviderCatalog.Info("mimo")!;
        Assert.Equal("https://token-plan-sgp.xiaomimimo.com/v1", mimo.DefaultBaseUrl);
        Assert.Equal("MIMO_API_KEY", mimo.ApiKeyEnv);
        Assert.False(mimo.Local);
        // Seeds come from the endpoint's live /models listing (chat models only).
        Assert.Contains(ProviderCatalog.For("mimo", ""), m => m.Id == "mimo-v2.5-pro");
    }

    [Fact]
    public void Catalog_DeepSeekWindowsMatchDsh()
    {
        var flash = ProviderCatalog.For("deepseek", "").First(m => m.Id == "deepseek-v4-flash");
        Assert.Equal(1_000_000, flash.ContextWindowTokens);
        Assert.Equal(256_000, flash.MaxOutputTokens);
    }

    [Fact]
    public void RequiresApiKey_OnlyCloudRoutes()
    {
        // Cloud providers fail fast with a clear message when no key is configured…
        foreach (var id in new[] { "openai", "anthropic", "deepseek", "zai", "zai-coding", "minimax", "qwen-token-plan", "mimo" })
            Assert.True(ProviderCatalog.RequiresApiKey(id), id);
        // …local servers and open gateways stream keyless; custom route names are unknown to the catalog.
        foreach (var id in new[] { "ollama", "lmstudio", "omlx", "unsloth", "openai-compatible", "my-gateway" })
            Assert.False(ProviderCatalog.RequiresApiKey(id), id);
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
        Assert.Equal(["glm-4.6"], settings.DiscoveredModels["zai"].Select(m => m.Id));
        Assert.False(settings.ProviderKeys.ContainsKey("zhipu"));
    }

    [Fact]
    public void MigrateLegacySettings_ZaiCodingMovesToTheResponsesWire()
    {
        // The coding plan's chat-completions URL is retired: only the Responses wire streams
        // reasoning incrementally. A stashed legacy URL moves with the catalog default.
        var settings = new HarnessSettings
        {
            Provider = "zai-coding",
            BaseUrl = "https://api.z.ai/api/coding/paas/v4",
            BaseUrlProvider = "zai-coding",
        };
        settings.ProviderBaseUrls["zai-coding"] = "https://api.z.ai/api/coding/paas/v4/";

        HarnessBootstrapper.MigrateLegacySettings(settings);

        Assert.Equal("https://api.z.ai/api/v1", settings.BaseUrl);
        Assert.Equal("https://api.z.ai/api/v1", settings.ProviderBaseUrls["zai-coding"]);
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

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void DiscoveredModels_LegacyStringListsStillLoad()
    {
        var settings = JsonSerializer.Deserialize<HarnessSettings>(
            """{"provider":"deepseek","discoveredModels":{"deepseek":["a","b"]}}""", CamelCase)!;
        Assert.Equal(["a", "b"], settings.DiscoveredModels["deepseek"].Select(m => m.Id));
        Assert.All(settings.DiscoveredModels["deepseek"], m => Assert.Null(m.ContextWindowTokens));
    }

    [Fact]
    public void DiscoveredModels_IdOnlyEntriesSerializeBackAsStrings()
    {
        var settings = new HarnessSettings();
        settings.DiscoveredModels["deepseek"] = ["a", "b"];
        var json = JsonSerializer.Serialize(settings, CamelCase);
        Assert.Contains("\"discoveredModels\":{\"deepseek\":[\"a\",\"b\"]}", json);
    }

    [Fact]
    public void DiscoveredModels_SizedEntriesRoundTripAsObjects()
    {
        var settings = new HarnessSettings();
        settings.DiscoveredModels["deepseek"] = [new DiscoveredModelInfo("m", 1_000_000, 32_768)];
        var reloaded = JsonSerializer.Deserialize<HarnessSettings>(JsonSerializer.Serialize(settings, CamelCase), CamelCase)!;
        var entry = Assert.Single(reloaded.DiscoveredModels["deepseek"]);
        Assert.Equal("m", entry.Id);
        Assert.Equal(1_000_000, entry.ContextWindowTokens);
        Assert.Equal(32_768, entry.MaxOutputTokens);
    }

    [Fact]
    public void ResolveMaxOutputTokens_UsesSettingWhenModelIsUnknown()
    {
        var settings = new HarnessSettings { MaxOutputTokens = 65_536 };
        Assert.Equal(65_536, HarnessBootstrapper.ResolveMaxOutputTokens(settings, [], "mystery"));
        Assert.Equal(65_536, HarnessBootstrapper.ResolveMaxOutputTokens(settings, [], null));
    }

    [Fact]
    public void ResolveMaxOutputTokens_ClampsToKnownModelCeiling()
    {
        var settings = new HarnessSettings { MaxOutputTokens = 65_536 };
        var models = new[] { new LlmModelInfo("p", "small", "small", MaxOutputTokens: 8_192) };
        Assert.Equal(8_192, HarnessBootstrapper.ResolveMaxOutputTokens(settings, models, "small"));
        Assert.Equal(65_536, HarnessBootstrapper.ResolveMaxOutputTokens(settings, models, "other"));
    }

    [Fact]
    public void CompatibleSlot_RetiredFromCatalogButStillResolves()
    {
        Assert.DoesNotContain("openai-compatible", ProviderCatalog.Providers);
        Assert.DoesNotContain(ProviderCatalog.All, p => p.Id == "openai-compatible");
        // Existing configs keep routing until they switch away.
        var legacy = ProviderCatalog.Info("openai-compatible");
        Assert.NotNull(legacy);
        Assert.Equal("https://gateway.example.com/v1", legacy.DefaultBaseUrl);
        Assert.False(ProviderCatalog.RequiresApiKey("openai-compatible"));
    }

    [Fact]
    public void MigrateLegacySettings_CompatibleSlotMatchingCatalogUrl_MovesOntoProvider()
    {
        // A Mimo endpoint configured before the Mimo entry existed moves over intact.
        var settings = new HarnessSettings
        {
            Provider = "openai-compatible",
            Model = "mimo-v2.5",
            ApiKey = "tp-live",
            BaseUrl = "https://token-plan-sgp.xiaomimimo.com/v1",
            BaseUrlProvider = "openai-compatible",
        };
        settings.ProviderKeys["openai-compatible"] = "tp-stashed";
        settings.ProviderBaseUrls["openai-compatible"] = "https://token-plan-sgp.xiaomimimo.com/v1";
        settings.DiscoveredModels["openai-compatible"] = ["mimo-v2.5", "mimo-v2.6-pro"];

        HarnessBootstrapper.MigrateLegacySettings(settings);

        Assert.Equal("mimo", settings.Provider);
        Assert.Equal("mimo", settings.BaseUrlProvider);
        Assert.Equal("mimo-v2.5", settings.Model); // still listed, so kept
        Assert.Equal("tp-live", settings.ApiKey);
        Assert.Equal("tp-stashed", settings.ProviderKeys["mimo"]);
        Assert.Equal(["mimo-v2.5", "mimo-v2.6-pro"], settings.DiscoveredModels["mimo"].Select(m => m.Id));
        Assert.Equal("https://token-plan-sgp.xiaomimimo.com/v1", settings.ProviderBaseUrls["mimo"]);
        Assert.False(settings.ProviderKeys.ContainsKey("openai-compatible"));
        Assert.False(settings.DiscoveredModels.ContainsKey("openai-compatible"));

        // Second run is a no-op.
        HarnessBootstrapper.MigrateLegacySettings(settings);
        Assert.Equal("mimo", settings.Provider);
    }

    [Fact]
    public void MigrateLegacySettings_CompatibleSlotStaleModel_FallsBackToCatalogDefault()
    {
        var settings = new HarnessSettings
        {
            Provider = "openai-compatible",
            Model = "default", // the retired slot's seed: not a real id on the new route
            BaseUrl = "https://token-plan-sgp.xiaomimimo.com/v1",
            BaseUrlProvider = "openai-compatible",
        };

        HarnessBootstrapper.MigrateLegacySettings(settings);

        Assert.Equal("mimo", settings.Provider);
        Assert.Equal(ProviderCatalog.DefaultModel("mimo"), settings.Model);
    }

    [Fact]
    public void MigrateLegacySettings_CompatibleSlotUnknownUrl_StaysLegacy()
    {
        var settings = new HarnessSettings
        {
            Provider = "openai-compatible",
            Model = "gw-model",
            ApiKey = "sk-gw",
            BaseUrl = "https://gw.internal/v1",
            BaseUrlProvider = "openai-compatible",
        };
        settings.ProviderKeys["openai-compatible"] = "sk-gw";

        HarnessBootstrapper.MigrateLegacySettings(settings);

        Assert.Equal("openai-compatible", settings.Provider);
        Assert.Equal("gw-model", settings.Model);
        Assert.Equal("sk-gw", settings.ProviderKeys["openai-compatible"]);
    }

    [Fact]
    public void MigrateLegacySettings_CompatibleSlotStashOnly_MovesWithoutSwitching()
    {
        // Inactive slot stash pointing at a catalog default moves over; active route untouched.
        var settings = new HarnessSettings { Provider = "deepseek", BaseUrl = "https://api.deepseek.com" };
        settings.ProviderKeys["openai-compatible"] = "tp-stashed";
        settings.ProviderBaseUrls["openai-compatible"] = "https://token-plan-sgp.xiaomimimo.com/v1";
        settings.DiscoveredModels["openai-compatible"] = ["mimo-v2.5"];

        HarnessBootstrapper.MigrateLegacySettings(settings);

        Assert.Equal("deepseek", settings.Provider);
        Assert.Equal("tp-stashed", settings.ProviderKeys["mimo"]);
        Assert.Equal(["mimo-v2.5"], settings.DiscoveredModels["mimo"].Select(m => m.Id));
        Assert.False(settings.ProviderKeys.ContainsKey("openai-compatible"));
    }

    [Fact]
    public void CustomRouteModels_ConfiguredIdsOrDefaultPlaceholder()
    {
        var listed = HarnessBootstrapper.CustomRouteModels(new CustomProviderConfig
        {
            Name = "mygw",
            BaseUrl = "https://gw.internal/v1",
            Models = ["a", "b"],
        });
        Assert.Equal(["a", "b"], [.. listed.Select(m => m.Id)]);
        Assert.All(listed, m => Assert.Equal("mygw", m.Provider));

        var empty = HarnessBootstrapper.CustomRouteModels(new CustomProviderConfig
        {
            Name = "mygw",
            BaseUrl = "https://gw.internal/v1",
        });
        var only = Assert.Single(empty);
        Assert.Equal("default", only.Id);
        Assert.Equal("mygw", only.Provider);
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
