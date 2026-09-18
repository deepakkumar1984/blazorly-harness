using System.Text.Json;
using Blazorly.Harness.Core.Decisions;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Boot-level composition for the decision layer: the seam must never be able to wedge startup,
/// and disabling it must leave the harness indistinguishable from before it existed.
/// </summary>
[Collection("BlazorlyHome")]
public class DecisionBootTests : BootstrapperTestBase
{
    [Fact]
    public async Task RiskGateWithoutDecisionsMounted_StillBoots()
    {
        // A user can put "decisions" in disabledPlugins while enableRiskGate is still true.
        // The gate used to inject the decision service, so removing it deadlocked PluginHost and
        // bricked startup entirely. Boot for real — inspecting the plugin list is not the regression.
        WriteSettings(new { enableRiskGate = true, disabledPlugins = new[] { DecisionPlugin.PluginName } });

        var bootstrapper = new HarnessBootstrapper();
        await bootstrapper.StartAsync(CancellationToken.None);
        try
        {
            Assert.DoesNotContain(DecisionPlugin.PluginName, bootstrapper.AppliedPlugins);
            Assert.DoesNotContain("risk-gate", bootstrapper.AppliedPlugins); // dropped with it, not left inert
            Assert.Contains("toolPolicy", bootstrapper.AppliedPlugins);      // the rest of the harness is fine
        }
        finally
        {
            await bootstrapper.DisposeAsync();
        }
    }

    [Fact]
    public async Task RiskGateEnabled_BootsAndMountsTheGate()
    {
        WriteSettings(new { enableRiskGate = true, riskGateThreshold = 0.4 });

        var bootstrapper = new HarnessBootstrapper();
        await bootstrapper.StartAsync(CancellationToken.None);
        try
        {
            Assert.Contains(DecisionPlugin.PluginName, bootstrapper.AppliedPlugins);
            Assert.Contains("risk-gate", bootstrapper.AppliedPlugins);
            // Keyless, so the gate is mounted but inert: the session preset stays the only authority.
            Assert.False(bootstrapper.Context
                .Get<DecisionService>(DecisionService.ServiceKey).IsEnabled(DecisionSeams.RiskGate));
        }
        finally
        {
            await bootstrapper.DisposeAsync();
        }
    }

    [Fact]
    public void PatchesDisable_FollowsThePluginNameConvention()
    {
        // patches.json maps a plugin name to its Enable* flag; a name that resolves to nothing is
        // silently skipped with a warning, so the plugin name must match the setting it gates.
        // Driven through the public entry point, which is how a user actually reaches it.
        var patchesHome = Path.Combine(Home, "patches-home");
        Directory.CreateDirectory(patchesHome);
        File.WriteAllText(Path.Combine(patchesHome, "patches.json"),
            """{"disable":["system-one","risk-gate"]}""");

        var settings = new HarnessSettings { EnableSystemOne = true, EnableRiskGate = true };
        HarnessBootstrapper.ApplyPatches(settings, patchesHome);

        Assert.False(settings.EnableSystemOne);
        Assert.False(settings.EnableRiskGate);
    }

    /// <summary>Writes this test home's settings.json before the bootstrapper reads it.</summary>
    private void WriteSettings(object settings)
        => File.WriteAllText(Path.Combine(Home, "settings.json"), JsonSerializer.Serialize(settings));

    [Fact]
    public async Task DisabledByDefault_BootsWithTheNoOpModel()
    {
        var bootstrapper = new HarnessBootstrapper();
        await bootstrapper.StartAsync(CancellationToken.None);
        try
        {
            var decisions = bootstrapper.Context.Get<DecisionService>(DecisionService.ServiceKey);
            Assert.Equal("none", decisions.Impl);
            Assert.False(decisions.IsEnabled(DecisionSeams.AutoPlan));
            Assert.False(decisions.IsEnabled(DecisionSeams.RiskGate));
            Assert.Contains(DecisionPlugin.PluginName, bootstrapper.AppliedPlugins);
        }
        finally
        {
            await bootstrapper.DisposeAsync();
        }
    }

    [Fact]
    public async Task EnabledWithoutAKey_StillBootsAsNoOp()
    {
        var bootstrapper = new HarnessBootstrapper();
        bootstrapper.Settings.EnableSystemOne = true; // flag on, no key anywhere
        bootstrapper.Settings.SystemOneApiKey = null;
        bootstrapper.Settings.SystemOneApiKeyEnv = "BLAZORLY_TEST_ABSENT_KEY";
        await bootstrapper.StartAsync(CancellationToken.None);
        try
        {
            var decisions = bootstrapper.Context.Get<DecisionService>(DecisionService.ServiceKey);
            Assert.Equal("none", decisions.Impl); // keyless is off, not half-on
            Assert.False(decisions.IsEnabled(DecisionSeams.AutoPlan));
        }
        finally
        {
            await bootstrapper.DisposeAsync();
        }
    }
}
