using System.Diagnostics;

namespace Blazorly.Harness.Cli;

/// <summary>
/// Relaunching this CLI as a child process (eval task runners). A self-contained
/// publish relaunches its own apphost — no `dotnet` SDK on the machine; dev/test
/// hosts that run through the muxer keep the `dotnet &lt;dll&gt;` form.
/// </summary>
internal static class CliRelaunch
{
    public static ProcessStartInfo StartInfo(string workingDirectory)
    {
        var expectedName = Path.GetFileNameWithoutExtension(typeof(EvalRunner).Assembly.Location);
        var path = Environment.ProcessPath;
        if (path is not null
            && Path.GetFileNameWithoutExtension(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
        }

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        start.ArgumentList.Add(typeof(EvalRunner).Assembly.Location);
        return start;
    }
}
