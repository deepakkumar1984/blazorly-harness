using Blazorly.Harness.Cli;
using Blazorly.Harness.Web;

namespace Blazorly.Harness.Tests;

public class UiArgsTests
{
    [Fact]
    public void Defaults_NoArgs()
    {
        var args = UiArgs.Parse([]);
        Assert.Equal(UiArgs.DefaultPort, args.Port);
        Assert.False(args.NoOpen);
        Assert.False(args.WantsVersion);
    }

    [Theory]
    [InlineData(new[] { "--port", "7000" }, 7000)]
    [InlineData(new[] { "--port=8080" }, 8080)]
    [InlineData(new[] { "-p", "9090" }, 9090)]
    [InlineData(new[] { "--port", "not-a-number" }, UiArgs.DefaultPort)]
    [InlineData(new[] { "--port", "70000" }, UiArgs.DefaultPort)]
    public void PortParsing(string[] raw, int expected) => Assert.Equal(expected, UiArgs.Parse(raw).Port);

    [Fact]
    public void PortExplicit_OnlyWhenAValidPortWasPassed()
    {
        // An explicit --port must beat ASPNETCORE_URLS (dotnet run injects it from
        // launchSettings); an absent or invalid flag keeps ambient env precedence.
        Assert.True(UiArgs.Parse(["--port", "7000"]).PortExplicit);
        Assert.True(UiArgs.Parse(["--port=8080"]).PortExplicit);
        Assert.True(UiArgs.Parse(["-p", "9090"]).PortExplicit);
        Assert.False(UiArgs.Parse([]).PortExplicit);
        Assert.False(UiArgs.Parse(["--no-open"]).PortExplicit);
        Assert.False(UiArgs.Parse(["--port", "not-a-number"]).PortExplicit);
        Assert.False(UiArgs.Parse(["--port", "70000"]).PortExplicit);
    }

    [Fact]
    public void Flags_Version_And_NoOpen()
    {
        Assert.True(UiArgs.Parse(["--version"]).WantsVersion);
        Assert.True(UiArgs.Parse(["-v"]).WantsVersion);
        Assert.True(UiArgs.Parse(["serve", "--no-open", "--port", "1234"]).NoOpen);
        Assert.False(UiArgs.Parse(["serve"]).NoOpen);
    }

    [Fact]
    public void VersionText_IsStamped()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", UiVersion.Text); // 0.1.0 (+commit hash suffix allowed)
    }
}

public class CliRelaunchTests
{
    [Fact]
    public void StartInfo_UnderTestHost_CarriesDllForTheMuxer()
    {
        // tests run under testhost — not the assembly's own apphost — so relaunches
        // must keep the `dotnet <dll>` form (the packaged `blazorly` apphost relaunches
        // itself directly instead; that path is exercised by the distribution smoke).
        var start = CliRelaunch.StartInfo("/tmp");
        Assert.Equal("/tmp", start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Contains(typeof(Blazorly.Harness.Cli.EvalRunner).Assembly.Location, start.ArgumentList);
    }
}
