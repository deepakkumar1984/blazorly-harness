using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Blazorly.Harness.Cli;

/// <summary>
/// `blazorly update` — self-update from GitHub Releases (or a local dist dir via
/// BLAZORLY_INSTALL_BASE). Verifies the published checksum, swaps ~/.blazorly/app/current
/// atomically on Unix; on Windows the running exe is locked, so a detached script
/// performs the swap after the process exits.
/// </summary>
internal static class SelfUpdate
{
    public static async Task<int> RunAsync(string[] args)
    {
        var repo = Environment.GetEnvironmentVariable("BLAZORLY_REPO") is { Length: > 0 } r
            ? r : "deepakkumar1984/blazorly-harness";
        var @base = Environment.GetEnvironmentVariable("BLAZORLY_INSTALL_BASE");
        if (@base is not null && (Path.IsPathRooted(@base) || @base.StartsWith("./", StringComparison.Ordinal)))
            @base = "file://" + Path.GetFullPath(@base);

        // refuse dev layouts: there is nothing sensible to swap
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "VERSION")))
        {
            Console.Error.WriteLine("this is not an installed build (no VERSION marker beside the binary) — use the install one-liner");
            return 1;
        }

        var rid = Rid();
        var ext = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        var archive = $"blazorly-{rid}.{ext}";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("blazorly-selfupdate");

        string? tag = null;
        if (@base is null)
        {
            // resolve latest via the API (the /latest/download redirect can lag on the CDN)
            try
            {
                var json = await http.GetFromJsonAsync<JsonElement>($"https://api.github.com/repos/{repo}/releases/latest");
                tag = json.GetProperty("tag_name").GetString();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"cannot resolve the latest release: {ex.Message}");
                return 1;
            }
            @base = $"https://github.com/{repo}/releases";
        }

        // GitHub serves assets under releases/download/<tag>/<name> (the /latest/download
        // redirect lags on the CDN right after publishing); a local dist dir has them at its root
        Func<string, string> assetUrl = @base.StartsWith("file://", StringComparison.Ordinal)
            ? name => $"{@base}/{name}"
            : name => tag is null ? $"{@base}/latest/download/{name}" : $"{@base}/download/{tag}/{name}";

        var current = CurrentVersion();
        var target = tag is { Length: > 0 } t && t.StartsWith('v') ? t[1..] : tag;

        var tmp = Path.Combine(Path.GetTempPath(), "blazorly-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            Console.WriteLine($"==> downloading {archive}" + (target is null ? "" : $" ({target})"));
            var bytes = await DownloadAsync(http, assetUrl(archive), tmp);
            if (bytes is null)
            {
                Console.Error.WriteLine($"download failed — is a release published for {rid}?");
                return 1;
            }

            // checksum when the release publishes one (skipped for local dist dirs without sidecars)
            var want = await TryDownloadAsync(http, assetUrl(archive + ".sha256"), tmp);
            if (want is not null)
            {
                var got = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!want.Trim().StartsWith(got, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("checksum mismatch — aborting");
                    return 1;
                }
                Console.WriteLine("==> checksum verified");
            }
            else if (tag is not null)
            {
                Console.WriteLine("==> no checksum published; skipping verification");
            }

            var appDir = Path.Combine(
                Environment.GetEnvironmentVariable("BLAZORLY_INSTALL_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly", "app"));
            var incoming = Path.Combine(appDir, ".incoming");
            if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
            Directory.CreateDirectory(incoming);

            if (ext == "zip")
            {
                ZipFile.ExtractToDirectory(Path.Combine(tmp, archive), incoming);
            }
            else
            {
                await using var gz = new GZipStream(File.OpenRead(Path.Combine(tmp, archive)), CompressionMode.Decompress);
                System.Formats.Tar.TarFile.ExtractToDirectory(gz, incoming, overwriteFiles: true);
            }

            var newVersion = File.Exists(Path.Combine(incoming, "VERSION"))
                ? (await File.ReadAllTextAsync(Path.Combine(incoming, "VERSION"))).Trim() : "?";
            if (current is not null && newVersion == current)
            {
                Console.WriteLine($"==> already up to date ({current})");
                Directory.Delete(incoming, recursive: true);
                return 0;
            }

            var currentDir = Path.Combine(appDir, "current");
            if (OperatingSystem.IsWindows())
            {
                // the running exe locks current\: a detached script swaps once we exit
                var script = Path.Combine(Path.GetTempPath(), "blazorly-swap.cmd");
                await File.WriteAllTextAsync(script,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    $"tasklist /fi \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul && (timeout /t 1 >nul & goto wait)\r\n" +
                    $"rd /s /q \"{currentDir}\" 2>nul\r\n" +
                    $"move \"{incoming}\" \"{currentDir}\" >nul\r\n" +
                    "exit\r\n");
                _ = Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" /min \"{script}\"") { CreateNoWindow = true, UseShellExecute = true });
                Console.WriteLine($"==> blazorly {newVersion} installs in the background once this process exits — reopen blazorly in a few seconds");
                return 0;
            }

            var old = Path.Combine(appDir, ".old");
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            if (Directory.Exists(currentDir)) Directory.Move(currentDir, old);
            Directory.Move(incoming, currentDir);
            Directory.Delete(old, recursive: true);
            Console.WriteLine($"==> updated {current ?? "?"} → {newVersion}");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
        }
    }

    private static string? CurrentVersion() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "VERSION"))
            ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VERSION")).Trim() : null;

    private static string Rid() =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + "-" + (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");

    private static async Task<byte[]?> DownloadAsync(HttpClient http, string url, string dir)
    {
        try
        {
            var bytes = url.StartsWith("file://", StringComparison.Ordinal)
                ? await File.ReadAllBytesAsync(url["file://".Length..])
                : await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(Path.Combine(dir, Path.GetFileName(url)), bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private static async Task<string?> TryDownloadAsync(HttpClient http, string url, string dir)
    {
        try
        {
            var text = url.StartsWith("file://", StringComparison.Ordinal)
                ? await File.ReadAllTextAsync(url["file://".Length..])
                : await http.GetStringAsync(url);
            await File.WriteAllTextAsync(Path.Combine(dir, Path.GetFileName(url)), text);
            return text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }
}
