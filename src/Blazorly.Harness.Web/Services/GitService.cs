using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Blazorly.Harness.Web.Services;

/// <summary>A changed path with the repository-relative path used for git operations.</summary>
public sealed record GitChange(string Path, string Display);

/// <summary>Working-tree state: branch plus staged, unstaged, and untracked paths.</summary>
public sealed record GitStatus(string Branch, int Ahead, int Behind,
    IReadOnlyList<GitChange> Staged, IReadOnlyList<GitChange> Unstaged, IReadOnlyList<GitChange> Untracked)
{
    public int Total => Staged.Count + Unstaged.Count + Untracked.Count;
}

public sealed class GitNotRepositoryException(string root)
    : InvalidOperationException($"'{root}' is not inside a git working tree.");

/// <summary>Minimal source-control seam over the git CLI: status, stage, unstage, commit.
// All commands run with an argument vector (no shell) inside the workspace root.</summary>
public static partial class GitService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public static bool IsRepository(string root)
    {
        try { return Run(root, ["rev-parse", "--git-dir"]).ExitCode == 0; }
        catch (InvalidOperationException) { return false; }
    }

    public static GitStatus Status(string root)
    {
        var result = Run(root, ["-c", "core.quotepath=off", "status", "--porcelain=v1", "-b", "--untracked-files=all"]);
        if (result.ExitCode != 0)
        {
            if (result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                throw new GitNotRepositoryException(root);
            throw new InvalidOperationException(FirstLines(result.Error, "git status failed."));
        }
        return ParsePorcelain(result.Output);
    }

    public static void Stage(string root, IEnumerable<string> paths)
    {
        var args = new List<string> { "add", "--" };
        args.AddRange(paths);
        var result = Run(root, args);
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstLines(result.Error, "git add failed."));
    }

    public static void Unstage(string root, IEnumerable<string> paths)
    {
        var args = new List<string> { "reset", "-q", "HEAD", "--" };
        args.AddRange(paths);
        var result = Run(root, args);
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstLines(result.Error, "git reset failed."));
    }

    public static string Commit(string root, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Write a commit message first.");
        var result = Run(root, ["commit", "-m", message.Trim()]);
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstLines(result.Error, "git commit failed."));
        return FirstLines(result.Output, "Committed.");
    }

    /// <summary>Pushes the current branch, setting the upstream on first push.</summary>
    public static string Push(string root)
    {
        var branch = CurrentBranch(root);
        var hasUpstream = Run(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"]).ExitCode == 0;
        var args = hasUpstream
            ? (IReadOnlyList<string>)["push"]
            : ["push", "-u", "origin", branch];
        var result = Run(root, args);
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstLines(result.Error, "git push failed."));
        return FirstLines(result.Error + "\n" + result.Output, "Pushed.");
    }

    /// <summary>Fast-forward pulls; refuses when the branches diverged (rebase locally instead).</summary>
    public static string Pull(string root)
    {
        var result = Run(root, ["pull", "--ff-only"]);
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstLines(result.Error, "git pull failed."));
        return FirstLines(result.Output, "Already up to date.");
    }

    /// <summary>Pulls fast-forward, then pushes — the sync button.</summary>
    public static string Sync(string root)
    {
        var pulled = Pull(root);
        var pushed = Push(root);
        return FirstLines(pulled + "\n" + pushed, "Synced.");
    }

    private static string CurrentBranch(string root)
    {
        var result = Run(root, ["branch", "--show-current"]);
        var branch = result.ExitCode == 0 ? result.Output.Trim() : "";
        if (branch.Length == 0) throw new InvalidOperationException("Detached HEAD: check out a branch first.");
        return branch;
    }

    /// <summary>Pure porcelain parser: branch/ahead/behind plus staged, unstaged, untracked lists.</summary>
    public static GitStatus ParsePorcelain(string output)
    {
        var branch = "";
        var ahead = 0;
        var behind = 0;
        var staged = new List<GitChange>();
        var unstaged = new List<GitChange>();
        var untracked = new List<GitChange>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                (branch, ahead, behind) = ParseBranchLine(line[3..]);
            }
            else if (line.Length > 3)
            {
                var x = line[0];
                var y = line[1];
                var path = line[3..];
                if (x == '?' && y == '?') untracked.Add(new GitChange(path, path));
                else if (x == '!' && y == '!') { /* ignored */ }
                else
                {
                    var change = ToChange(path);
                    if (x is not (' ' or '?')) staged.Add(change);
                    if (y is not (' ' or '?')) unstaged.Add(change);
                }
            }
        }
        return new GitStatus(branch, ahead, behind, staged, unstaged, untracked);
    }

    private static GitChange ToChange(string path)
    {
        // Renames/copies arrive as "old -> new": operate on the new path, display both.
        var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
        return arrow < 0
            ? new GitChange(path, path)
            : new GitChange(path[(arrow + 4)..], $"{path[..arrow]} → {path[(arrow + 4)..]}");
    }

    private static (string Branch, int Ahead, int Behind) ParseBranchLine(string line)
    {
        var branch = line;
        var ahead = 0;
        var behind = 0;
        var tracking = branch.IndexOf("...", StringComparison.Ordinal);
        if (tracking >= 0)
        {
            var bracket = branch.IndexOf('[', tracking);
            if (bracket >= 0)
            {
                foreach (Match m in AheadBehindRegex().Matches(branch[bracket..]))
                {
                    if (m.Groups["label"].Value == "ahead") ahead = int.Parse(m.Groups["n"].Value);
                    else behind = int.Parse(m.Groups["n"].Value);
                }
                branch = branch[..bracket].TrimEnd();
            }
            branch = branch[..tracking];
        }
        const string noCommits = "No commits yet on ";
        if (branch.StartsWith(noCommits, StringComparison.Ordinal)) branch = branch[noCommits.Length..];
        return (branch, ahead, behind);
    }

    [GeneratedRegex(@"(?<label>ahead|behind) (?<n>\d+)")]
    private static partial Regex AheadBehindRegex();

    private static string FirstLines(string text, string fallback)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? fallback : string.Join('\n', lines.Take(3));
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);

    private static GitResult Run(string root, IReadOnlyList<string> args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Never block on an interactive credential prompt inside the server process.
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException("git is not installed or not on PATH.");
        }
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(CommandTimeout))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }
            throw new InvalidOperationException("git did not respond in time.");
        }
        process.WaitForExit();
        return new GitResult(process.ExitCode, output.ToString(), error.ToString());
    }
}
