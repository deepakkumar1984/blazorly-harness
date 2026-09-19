using System.Net;
using Blazorly.Harness.Cli;
using Blazorly.Harness.Web;
using Microsoft.AspNetCore.Http;

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

    [Theory]
    [InlineData(new[] { "--host", "0.0.0.0" }, "0.0.0.0")]
    [InlineData(new[] { "--host=192.168.1.10" }, "192.168.1.10")]
    [InlineData(new[] { "-h", "127.0.0.1" }, "127.0.0.1")]
    [InlineData(new[] { "--host", "http://x/" }, UiArgs.DefaultHost)]
    [InlineData(new[] { "--host" }, UiArgs.DefaultHost)]
    [InlineData(new[] { "--host=" }, UiArgs.DefaultHost)]
    public void HostParsing(string[] raw, string expected) => Assert.Equal(expected, UiArgs.Parse(raw).Host);

    [Fact]
    public void HostExplicit_OnlyWhenAValidHostWasPassed()
    {
        Assert.True(UiArgs.Parse(["--host", "0.0.0.0"]).HostExplicit);
        Assert.True(UiArgs.Parse(["--host=example"]).HostExplicit);
        Assert.True(UiArgs.Parse(["-h", "127.0.0.1"]).HostExplicit);
        Assert.False(UiArgs.Parse([]).HostExplicit);
        Assert.False(UiArgs.Parse(["--host"]).HostExplicit);
        Assert.False(UiArgs.Parse(["--host", "http://x/"]).HostExplicit);
        // a following flag is not a host: --host yields, -p still parses
        var both = UiArgs.Parse(["--host", "-p", "9000"]);
        Assert.False(both.HostExplicit);
        Assert.True(both.PortExplicit);
        Assert.Equal(9000, both.Port);
    }

    [Fact]
    public void ListenAndOpenUrls_HandleWildcardAndIpv6()
    {
        Assert.Equal("http://localhost:5080", UiArgs.Parse([]).ListenUrl);
        Assert.Equal("http://0.0.0.0:5080", UiArgs.Parse(["--host", "0.0.0.0"]).ListenUrl);
        Assert.Equal("http://localhost:5080", UiArgs.Parse(["--host", "0.0.0.0"]).OpenUrl);
        Assert.Equal("http://[::]:6010", UiArgs.Parse(["--host", "::", "--port", "6010"]).ListenUrl);
        Assert.Equal("http://localhost:6010", UiArgs.Parse(["--host", "::", "--port", "6010"]).OpenUrl);
        Assert.True(UiArgs.Parse([]).IsLoopbackOnly);
        Assert.True(UiArgs.Parse(["--host", "127.0.0.1"]).IsLoopbackOnly);
        Assert.False(UiArgs.Parse(["--host", "0.0.0.0"]).IsLoopbackOnly);
        Assert.False(UiArgs.Parse(["--host", "192.168.1.10"]).IsLoopbackOnly);
    }

    [Theory]
    [InlineData(new[] { "--token", "s3cret" }, "s3cret")]
    [InlineData(new[] { "--token=pinned-123" }, "pinned-123")]
    [InlineData(new[] { "--token" }, null)]
    [InlineData(new[] { "--token", "has space" }, null)]
    [InlineData(new[] { "--token=a&b" }, null)]
    public void TokenParsing(string[] raw, string? expected) => Assert.Equal(expected, UiArgs.Parse(raw).Token);

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

public class UiAccessGateTests
{
    private static DefaultHttpContext Context(string? remoteIp = null)
    {
        var context = new DefaultHttpContext();
        if (remoteIp is not null) context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }

    [Fact]
    public void Generate_MintsUniqueUrlSafeTokens()
    {
        var first = UiAccessGate.Generate();
        var second = UiAccessGate.Generate();
        Assert.NotEqual(first, second);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
    }

    [Fact]
    public void Loopback_BypassesWithoutToken()
    {
        var gate = new UiAccessGate(null);
        Assert.True(gate.IsAuthorized(Context("127.0.0.1")));
        Assert.True(gate.IsAuthorized(Context("::1")));
    }

    [Fact]
    public void Remote_WithoutToken_IsRejected()
    {
        var gate = new UiAccessGate(null);
        Assert.False(gate.IsAuthorized(Context("192.168.1.10")));
        Assert.False(gate.IsAuthorized(Context())); // unknown remote fails closed
    }

    [Fact]
    public void Remote_QueryCookieAndBearer_AcceptTheToken()
    {
        var gate = new UiAccessGate("s3cret");
        Assert.True(gate.Pinned);

        var query = Context("192.168.1.10");
        query.Request.QueryString = new QueryString("?token=s3cret");
        Assert.True(gate.IsAuthorized(query));

        var wrong = Context("192.168.1.10");
        wrong.Request.QueryString = new QueryString("?token=nope");
        Assert.False(gate.IsAuthorized(wrong));

        var cookie = Context("192.168.1.10");
        cookie.Request.Headers.Cookie = $"{UiAccessGate.CookieName}=s3cret";
        Assert.True(gate.IsAuthorized(cookie));

        var bearer = Context("192.168.1.10");
        bearer.Request.Headers.Authorization = "Bearer s3cret";
        Assert.True(gate.IsAuthorized(bearer));
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
