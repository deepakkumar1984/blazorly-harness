namespace Blazorly.Harness.Web.Services;

/// <summary>Version of the running build. Installed builds (dist archives,
/// install.sh/ps1, `blazorly update`) carry a VERSION file beside the binary;
/// release builds carry the tag in the assembly informational version; plain
/// `dotnet build/run` dev builds carry the Directory.Build.props placeholder
/// (0.1.0), which resolves from the local git tags instead.</summary>
public static class AppVersion
{
    private static readonly Lazy<string?> GitTag = new(ProbeGitTag);

    /// <summary>Raw stamp, e.g. "0.9.1" or "0.0.0-dev".</summary>
    public static string Current
    {
        get
        {
            string? marker = null;
            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, "VERSION");
                if (File.Exists(file)) marker = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return ResolveCurrent(marker, UiVersion.Text, () => GitTag.Value);
        }
    }

    /// <summary>Resolution chain, factored out for tests: VERSION marker wins;
    /// the 0.1.0 build-placeholder defers to the git tag when it parses;
    /// otherwise the assembly stamp stands.</summary>
    public static string ResolveCurrent(string? markerText, string assemblyVersion, Func<string?> gitTag)
    {
        if (!string.IsNullOrWhiteSpace(markerText)) return markerText.Trim();
        if (!string.IsNullOrWhiteSpace(assemblyVersion) && IsPlaceholder(assemblyVersion))
        {
            string? tag = null;
            try { tag = gitTag?.Invoke(); } catch { tag = null; }
            if (!string.IsNullOrWhiteSpace(tag) && TryParse(tag, out _)) return tag.Trim();
        }
        return assemblyVersion;
    }

    /// <summary>True when the stamp is just the Directory.Build.props default
    /// (0.1.0 plus optional build metadata), i.e. no real version was stamped.</summary>
    public static bool IsPlaceholder(string? raw)
    {
        if (!TryParse(raw, out var parsed) || parsed.Prerelease) return false;
        for (var i = 0; i < Math.Max(parsed.Parts.Length, 3); i++)
        {
            var part = i < parsed.Parts.Length ? parsed.Parts[i] : 0;
            var expected = i == 1 ? 1 : 0;
            if (part != expected) return false;
        }
        return true;
    }

    /// <summary>Latest tag reachable from HEAD (e.g. "v0.9.1"), or null when git
    /// is unavailable, slow, or the checkout has no tags. Cached per process.</summary>
    private static string? ProbeGitTag()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo("git", "describe --tags --abbrev=0")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch { } return null; }
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>True when this looks like an installed build that can self-update.</summary>
    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "VERSION"));

    /// <summary>Display form: "v0.9.1". Falls back to the raw stamp when it is not parseable.</summary>
    public static string Display
    {
        get
        {
            var raw = Current;
            return TryParse(raw, out var parsed) ? "v" + parsed.Canonical : raw;
        }
    }

    /// <summary>True when <paramref name="latest"/> is a newer release than <paramref name="current"/>.</summary>
    public static bool IsUpdateAvailable(string? current, string? latest)
    {
        if (!TryParse(current, out var cur) || !TryParse(latest, out var lat)) return false;
        return Compare(lat, cur) > 0;
    }

    public sealed record ParsedVersion(int[] Parts, bool Prerelease)
    {
        public string Canonical
        {
            get
            {
                // Trim trailing zeros to 3 parts minimum display ("1" -> "1.0.0").
                var parts = Parts.ToList();
                while (parts.Count < 3) parts.Add(0);
                var core = string.Join(".", parts);
                return Prerelease ? core + "-pre" : core;
            }
        }
    }

    public static bool TryParse(string? raw, out ParsedVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        // Strip build metadata; a '-' suffix marks a pre-release (e.g. 0.0.0-dev).
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var prerelease = false;
        var dash = s.IndexOf('-');
        if (dash >= 0) { prerelease = true; s = s[..dash]; }
        var chunks = s.Split('.');
        if (chunks.Length is 0 or > 4) return false;
        var parts = new int[chunks.Length];
        for (var i = 0; i < chunks.Length; i++)
        {
            if (!int.TryParse(chunks[i], out var n) || n < 0) return false;
            parts[i] = n;
        }
        version = new ParsedVersion(parts, prerelease);
        return true;
    }

    private static int Compare(ParsedVersion a, ParsedVersion b)
    {
        var width = Math.Max(a.Parts.Length, b.Parts.Length);
        for (var i = 0; i < width; i++)
        {
            var x = i < a.Parts.Length ? a.Parts[i] : 0;
            var y = i < b.Parts.Length ? b.Parts[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (a.Prerelease == b.Prerelease) return 0;
        return a.Prerelease ? -1 : 1;
    }
}
