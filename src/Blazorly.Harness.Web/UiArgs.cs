using System.Reflection;

namespace Blazorly.Harness.Web;

/// <summary>Parsed UI-host arguments. Unknown flags are ignored so editor launchers
/// and future flags never block boot.</summary>
public sealed record UiArgs(int Port = UiArgs.DefaultPort, bool NoOpen = false, bool WantsVersion = false,
    bool PortExplicit = false)
{
    public const int DefaultPort = 5080;

    public static UiArgs Parse(string[] args)
    {
        var port = DefaultPort;
        var noOpen = false;
        var wantsVersion = false;
        var portExplicit = false;
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
                case "--no-open":
                    noOpen = true;
                    break;
                case "--version" or "-v":
                    wantsVersion = true;
                    break;
            }
        }
        return new UiArgs(port, noOpen, wantsVersion, portExplicit);
    }
}

public static class UiVersion
{
    /// <summary>Stamp of the running build (Directory.Build.props / release tag).</summary>
    public static string Text =>
        typeof(UiHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
