using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v0.9.1", "0.9.1")]
    [InlineData("0.9.1", "0.9.1")]
    [InlineData("  v1.2  ", "1.2.0")]
    [InlineData("0.0.0-dev", "0.0.0-pre")]
    [InlineData("2.0.1+sha.abc", "2.0.1")]
    public void Normalize_ParsesCommonStamps(string raw, string canonical)
    {
        Assert.True(AppVersion.TryParse(raw, out var parsed));
        Assert.Equal(canonical, parsed.Canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4.5")]
    public void Normalize_RejectsNonVersions(string? raw)
    {
        Assert.False(AppVersion.TryParse(raw, out _));
    }

    [Theory]
    // latest, current, expected
    [InlineData("v0.9.1", "v0.9.0", true)]
    [InlineData("v0.10.0", "v0.9.1", true)]
    [InlineData("v1.0.0", "v0.9.1", true)]
    [InlineData("v0.9.1", "v0.9.1", false)]
    [InlineData("v0.9.0", "v0.9.1", false)]
    [InlineData("v0.9.1", "0.0.0-dev", true)]
    [InlineData("v0.9.1", "not-a-version", false)]
    [InlineData(null, "v0.9.1", false)]
    public void IsUpdateAvailable_ComparesNumerically(string? latest, string? current, bool expected)
    {
        Assert.Equal(expected, AppVersion.IsUpdateAvailable(current, latest));
    }

    [Fact]
    public void ParseLatestTag_ReadsTagName()
    {
        Assert.Equal("v0.9.1", UpdateService.ParseLatestTag("""{"tag_name":"v0.9.1","name":"x"}"""));
    }

    [Fact]
    public void ParseLatestTag_ReturnsNullWhenMissing()
    {
        Assert.Null(UpdateService.ParseLatestTag("""{"name":"x"}"""));
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("v0.1.0", true)]
    [InlineData("0.1.0+sha.abc", true)]
    [InlineData("0.9.1", false)]
    [InlineData("0.0.0-dev", false)]
    [InlineData("not-a-version", false)]
    public void IsPlaceholder_DetectsUnstampedBuildDefault(string? raw, bool expected)
    {
        Assert.Equal(expected, AppVersion.IsPlaceholder(raw));
    }

    [Fact]
    public void ResolveCurrent_MarkerWinsOverEverything()
    {
        Assert.Equal("0.9.1", AppVersion.ResolveCurrent("  0.9.1\n", "0.1.0", () => "v0.9.1"));
    }

    [Fact]
    public void ResolveCurrent_PlaceholderDefersToValidGitTag()
    {
        Assert.Equal("v0.9.1", AppVersion.ResolveCurrent(null, "0.1.0", () => "v0.9.1"));
    }

    [Fact]
    public void ResolveCurrent_PlaceholderKeepsAssemblyWhenGitIsMissingOrGarbage()
    {
        Assert.Equal("0.1.0", AppVersion.ResolveCurrent(null, "0.1.0", () => null));
        Assert.Equal("0.1.0", AppVersion.ResolveCurrent(null, "0.1.0", () => "not-a-version"));
        Assert.Equal("0.1.0", AppVersion.ResolveCurrent(null, "0.1.0", () => throw new InvalidOperationException()));
    }

    [Fact]
    public void ResolveCurrent_StampedAssemblyIgnoresGit()
    {
        Assert.Equal("0.9.0", AppVersion.ResolveCurrent(null, "0.9.0", () => "v0.9.1"));
    }
}
