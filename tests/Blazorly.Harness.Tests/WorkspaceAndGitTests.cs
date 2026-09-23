using System.Diagnostics;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>File-tree operations and git changeset parsing/round-trips.</summary>
public class WorkspaceAndGitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-wg-" + Guid.NewGuid().ToString("N")[..8]);

    public static string? GitSkipReason()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git", Arguments = "--version",
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            process.Start();
            return process.WaitForExit(10_000) && process.ExitCode == 0 ? null : "git is not runnable on this machine";
        }
        catch
        {
            return "git is not installed on this machine";
        }
    }

    [Fact]
    public void CreateRenameDelete_RoundTrip()
    {
        Directory.CreateDirectory(_root);
        var created = WorkspaceFiles.CreateFile(_root, "docs/notes.txt");
        Assert.Equal("docs/notes.txt", created);
        Assert.True(File.Exists(Path.Combine(_root, "docs/notes.txt")));
        Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.CreateFile(_root, "docs/notes.txt"));

        var dir = WorkspaceFiles.CreateDirectory(_root, "src/app");
        Assert.Equal("src/app", dir);
        Assert.True(Directory.Exists(Path.Combine(_root, "src/app")));

        var renamed = WorkspaceFiles.Rename(_root, "docs/notes.txt", "todo.txt");
        Assert.Equal("docs/todo.txt", renamed);
        Assert.False(File.Exists(Path.Combine(_root, "docs/notes.txt")));
        Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Rename(_root, "docs/todo.txt", "a/b.txt"));

        WorkspaceFiles.Delete(_root, "docs/todo.txt");
        Assert.False(File.Exists(Path.Combine(_root, "docs/todo.txt")));
        WorkspaceFiles.Delete(_root, "src");
        Assert.False(Directory.Exists(Path.Combine(_root, "src")));
    }

    [Fact]
    public void Delete_RefusesRootGitAndEscapes()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Delete(_root, ""));
        Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Delete(_root, ".git"));
        Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Delete(_root, "../outside"));
        Assert.Throws<InvalidOperationException>(() => WorkspaceFiles.Rename(_root, ".git", "git2"));
    }

    [Fact]
    public void ParsePorcelain_SplitsStagedUnstagedUntracked()
    {
        var status = GitService.ParsePorcelain("## main...origin/main [ahead 2, behind 1]\nM  staged.txt\n M unstaged.txt\nMM both.txt\nA  added.txt\n?? new.txt\n!! ignored.txt\n");
        Assert.Equal("main", status.Branch);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.Equal(["staged.txt", "both.txt", "added.txt"], status.Staged.Select(c => c.Path));
        Assert.Equal(["unstaged.txt", "both.txt"], status.Unstaged.Select(c => c.Path));
        Assert.Equal(["new.txt"], status.Untracked.Select(c => c.Path));
    }

    [Fact]
    public void ParsePorcelain_RenameOperatesOnNewPath()
    {
        var status = GitService.ParsePorcelain("## master\nR  old.txt -> new.txt\n");
        var change = Assert.Single(status.Staged);
        Assert.Equal("new.txt", change.Path);
        Assert.Equal("old.txt → new.txt", change.Display);
    }

    [Fact]
    public void ParsePorcelain_FreshRepoBranch()
    {
        var status = GitService.ParsePorcelain("## No commits yet on main\nA  first.txt\n");
        Assert.Equal("main", status.Branch);
        Assert.Equal(["first.txt"], status.Staged.Select(c => c.Path));
    }

    [Fact]
    public void Git_RoundTripsStatusStageCommit()
    {
        if (GitSkipReason() is not null) return;
        Directory.CreateDirectory(_root);
        Git("init");
        Git("config user.email t@t.t");
        Git("config user.name t");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        Git("add .");
        Git("commit -m init");

        Assert.True(GitService.IsRepository(_root));
        File.AppendAllText(Path.Combine(_root, "a.txt"), "more");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");

        var dirty = GitService.Status(_root);
        Assert.Equal(["a.txt"], dirty.Unstaged.Select(c => c.Path));
        Assert.Equal(["b.txt"], dirty.Untracked.Select(c => c.Path));
        Assert.Empty(dirty.Staged);

        GitService.Stage(_root, ["a.txt", "b.txt"]);
        Assert.Equal(2, GitService.Status(_root).Staged.Count);

        GitService.Unstage(_root, ["a.txt"]);
        var partial = GitService.Status(_root);
        Assert.Equal(["b.txt"], partial.Staged.Select(c => c.Path));
        Assert.Equal(["a.txt"], partial.Unstaged.Select(c => c.Path));

        GitService.Stage(_root, ["a.txt"]);
        var receipt = GitService.Commit(_root, "second");
        Assert.Contains("second", receipt);
        Assert.Equal(0, GitService.Status(_root).Total);
    }

    [Fact]
    public void Git_NonRepository_ThrowsFriendly()
    {
        if (GitSkipReason() is not null) return;
        Directory.CreateDirectory(_root);
        Assert.False(GitService.IsRepository(_root));
        Assert.Throws<GitNotRepositoryException>(() => GitService.Status(_root));
    }

    [Fact]
    public void Git_PushPullSync_RoundTrip()
    {
        if (GitSkipReason() is not null) return;
        Directory.CreateDirectory(_root);
        Git("init");
        Git("config user.email t@t.t");
        Git("config user.name t");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        Git("add .");
        Git("commit -m init");

        var remote = Path.Combine(Path.GetTempPath(), "blazorly-remote-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(remote);
        Git("init --bare", remote);
        try
        {
            Git($"remote add origin {remote}");

            GitService.Push(_root);
            var tracked = GitService.Status(_root);
            Assert.Equal(0, tracked.Ahead);
            Assert.Equal(0, tracked.Behind);

            File.AppendAllText(Path.Combine(_root, "a.txt"), "more");
            GitService.Stage(_root, ["a.txt"]);
            GitService.Commit(_root, "second");
            Assert.Equal(1, GitService.Status(_root).Ahead);

            GitService.Sync(_root);
            Assert.Equal(0, GitService.Status(_root).Ahead);
            Assert.Contains("up to date", GitService.Pull(_root), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(remote, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Git_PushWithoutRemote_ThrowsFriendly()
    {
        if (GitSkipReason() is not null) return;
        Directory.CreateDirectory(_root);
        Git("init");
        Git("config user.email t@t.t");
        Git("config user.name t");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        Git("add .");
        Git("commit -m init");
        Assert.Throws<InvalidOperationException>(() => GitService.Push(_root));
    }

    private void Git(string args, string? cwd = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git", Arguments = args, WorkingDirectory = cwd ?? _root,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        process.Start();
        if (!process.WaitForExit(30_000) || process.ExitCode != 0)
            throw new InvalidOperationException($"fixture git {args} failed");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
