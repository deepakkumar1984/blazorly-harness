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
            // US
            "openai", "anthropic", "xai", "google", "mistral", "perplexity", "together", "groq",
            "fireworks", "openrouter", "cerebras", "cohere",
            // China
            "deepseek", "qwen", "moonshot", "zhipu", "minimax", "doubao", "ernie", "hunyuan", "stepfun", "yi",
            // Local / hosted-open
            "ollama", "lmstudio", "omlx", "unsloth",
        };
        foreach (var id in expected)
        {
            Assert.Contains(id, ProviderCatalog.Providers);
            Assert.NotEmpty(ProviderCatalog.Info(id)!.Name);
        }
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
        Assert.True(ProviderCatalog.Info("ollama")!.Local);
        Assert.Null(ProviderCatalog.Info("ollama")!.ApiKeyEnv);
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
