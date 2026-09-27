using System.Diagnostics;
using System.Text.Json;

namespace Blazorly.Harness.Web.Services;

/// <summary>Update state for the Settings UI and the sidebar badge. Check hits the
/// GitHub Releases API; install reuses the same flow as `blazorly update` by
/// spawning the installed launcher, so the checksum + swap logic stays in one place.</summary>
public sealed class UpdateService(IHttpClientFactory http)
{
    public enum State
    {
        Unknown,
        Checking,
        UpToDate,
        Available,
        Updating,
        ReadyToRestart,
        Failed,
        Unsupported,
    }

    public State Current { get; private set; } = State.Unknown;
    public string? LatestVersion { get; private set; }
    public string? Message { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }
    public string UpdateLog { get; private set; } = "";

    /// <summary>Fired on every state change so the badge and Settings tab refresh.</summary>
    public event Action? Changed;

    /// <summary>One-click update only works for installed builds on Unix: on Windows
    /// the running binary locks its own directory, and dev layouts have nothing to swap.</summary>
    public string? UpdateBlockedReason
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return "One-click update is not available on Windows while the app is running — stop the app and run `blazorly update` instead.";
            if (!AppVersion.IsInstalled)
                return "This is a development build (no VERSION marker beside the binary) — update via the install one-liner instead.";
            if (Environment.ProcessPath is null)
                return "Cannot locate the installed launcher.";
            return null;
        }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (Current is State.Checking or State.Updating) return;
        Set(State.Checking, null);
        try
        {
            var repo = Environment.GetEnvironmentVariable("BLAZORLY_REPO") is { Length: > 0 } r
                ? r : "deepakkumar1984/blazorly-harness";
            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("blazorly-update-check");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                Set(State.Failed, $"GitHub API returned {(int)res.StatusCode} — try again later.");
                return;
            }
            var tag = ParseLatestTag(await res.Content.ReadAsStringAsync(ct));
            if (tag is null)
            {
                Set(State.Failed, "Could not read the latest release tag.");
                return;
            }
            LatestVersion = tag;
            CheckedAt = DateTimeOffset.Now;
            Set(AppVersion.IsUpdateAvailable(AppVersion.Current, tag) ? State.Available : State.UpToDate,
                AppVersion.IsUpdateAvailable(AppVersion.Current, tag)
                    ? $"{AppVersion.Display} → {tag} is available."
                    : $"Up to date ({AppVersion.Display}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Set(State.Failed, $"Update check failed: {ex.Message}");
        }
    }

    public async Task StartUpdateAsync(CancellationToken ct = default)
    {
        if (Current is State.Updating) return;
        if (UpdateBlockedReason is { } reason)
        {
            Set(State.Unsupported, reason);
            return;
        }
        Set(State.Updating, $"Installing {LatestVersion ?? "latest"} — the UI stays up; restart when it finishes.");
        UpdateLog = "";
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, "update")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Set(State.Failed, "Could not start the updater.");
                return;
            }
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            var errors = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            UpdateLog = Tail(output + errors, 40);
            if (proc.ExitCode == 0)
                Set(State.ReadyToRestart, $"Installed {LatestVersion ?? "the update"} — restart the app to pick it up.");
            else
                Set(State.Failed, $"Updater exited with code {proc.ExitCode}. See the log below.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TaskCanceledException)
        {
            Set(State.Failed, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>Reads tag_name out of a releases/latest payload; null when absent.</summary>
    public static string? ParseLatestTag(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("tag_name", out var tag)
            ? tag.GetString() : null;
    }

    private void Set(State state, string? message)
    {
        Current = state;
        Message = message;
        Changed?.Invoke();
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n');
        return string.Join('\n', all[^Math.Min(lines, all.Length)..]).Trim();
    }
}
