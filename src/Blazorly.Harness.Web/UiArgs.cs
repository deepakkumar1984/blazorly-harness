using System.Reflection;

namespace Blazorly.Harness.Web;

/// <summary>Parsed UI-host arguments. Unknown flags are ignored so editor launchers
/// and future flags never block boot.</summary>
public sealed record UiArgs(int Port = UiArgs.DefaultPort, bool NoOpen = false, bool WantsVersion = false,
    bool PortExplicit = false, string Host = UiArgs.DefaultHost, bool HostExplicit = false,
    string? Token = null)
{
    public const int DefaultPort = 5080;
    public const string DefaultHost = "localhost";

    /// <summary>True for binds reachable only from this machine; anything else gets a no-auth warning.</summary>
    public bool IsLoopbackOnly => Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    /// <summary>Kestrel listen URL (IPv6 hosts bracketed).</summary>
    public string ListenUrl => $"http://{BracketedHost}:{Port}";

    /// <summary>URL to print and open: unspecified addresses resolve to localhost.</summary>
    public string OpenUrl => Host is "0.0.0.0" or "::" or "[::]" ? $"http://localhost:{Port}" : ListenUrl;

    private string BracketedHost => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;

    public static UiArgs Parse(string[] args)
    {
        var port = DefaultPort;
        var noOpen = false;
        var wantsVersion = false;
        var portExplicit = false;
        var host = DefaultHost;
        var hostExplicit = false;
        string? token = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" or "-p":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed) && parsed is > 0 and < 65536)
                    {
                        port = parsed;
                        portExplicit = true;
                    }
                    i++;
                    break;
                case var p when p.StartsWith("--port=", StringComparison.Ordinal):
                    if (int.TryParse(p["--port=".Length..], out var eq) && eq is > 0 and < 65536)
                    {
                        port = eq;
                        portExplicit = true;
                    }
                    break;
                case "--host" or "-h":
                    // A following flag is not a host: leave it for its own case instead of
                    // swallowing it (a bare `--host` at the end simply keeps localhost).
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && TakeHost(args[i + 1], ref host))
                    {
                        hostExplicit = true;
                        i++;
                    }
                    break;
                case var h when h.StartsWith("--host=", StringComparison.Ordinal):
                    if (TakeHost(h["--host=".Length..], ref host)) hostExplicit = true;
                    break;
                case "--token":
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && TakeToken(args[i + 1], ref token)) i++;
                    break;
                case var t when t.StartsWith("--token=", StringComparison.Ordinal):
                    TakeToken(t["--token=".Length..], ref token);
                    break;
                case "--no-open":
                    noOpen = true;
                    break;
                case "--version" or "-v":
                    wantsVersion = true;
                    break;
            }
        }
        return new UiArgs(port, noOpen, wantsVersion, portExplicit, host, hostExplicit, token);
    }

    /// <summary>Pinned tokens must survive a query string: no whitespace or separators.</summary>
    private static bool TakeToken(string value, ref string? token)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(c => char.IsWhiteSpace(c) || c is ';' or ',' or '&' or '=')) return false;
        token = value.Trim();
        return true;
    }

    /// <summary>Bare hosts only (ip, name, 0.0.0.0): URLs belong in ASPNETCORE_URLS/--urls.</summary>
    private static bool TakeHost(string value, ref string host)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://")
            || value.Any(c => char.IsWhiteSpace(c) || c is ';' or ',')) return false;
        host = value.Trim();
        return true;
    }
}

public static class UiVersion
{
    /// <summary>Stamp of the running build (Directory.Build.props / release tag).</summary>
    public static string Text =>
        typeof(UiHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
