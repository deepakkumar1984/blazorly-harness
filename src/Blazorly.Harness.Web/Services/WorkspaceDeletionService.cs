using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Jobs;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Tools;

namespace Blazorly.Harness.Web.Services;

public sealed record WorkspaceDeletionPreview(Workspace Workspace, IReadOnlyList<SessionHeader> Sessions);

/// <summary>Deletes a confirmed, registered workspace and its complete session tree.</summary>
public sealed class WorkspaceDeletionService(HarnessBootstrapper harness, RunSupervisor runs)
{
    private readonly SemaphoreSlim _deleteGate = new(1, 1);

    public async Task<WorkspaceDeletionPreview> PreviewAsync(string id, CancellationToken ct = default)
    {
        var workspace = harness.Workspaces.Get(id) ?? throw new InvalidOperationException("This workspace no longer exists.");
        ValidateDirectory(workspace);
        var headers = (await harness.Sessions.ListPersistedAsync(ct)).Concat(harness.Sessions.LiveSessions().Select(s => s.Header))
            .DistinctBy(s => s.Id).ToArray();
        var selected = headers.Where(h => h.Cwd is { Length: > 0 } cwd && WorkspaceFiles.IsInside(workspace.Root, cwd))
            .Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        bool added;
        do
        {
            added = false;
            foreach (var header in headers)
                if (header.ParentSession is { } parent && selected.Contains(parent)) added |= selected.Add(header.Id);
        } while (added);
        var sessions = headers.Where(h => selected.Contains(h.Id)).OrderByDescending(h => h.DelegationDepth).ToArray();
        if (sessions.Any(h => h.Id is "." or ".." || string.IsNullOrWhiteSpace(h.Id)
            || h.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || h.Id.Contains('/') || h.Id.Contains('\\')))
            throw new InvalidOperationException("A session has an invalid id and cannot be safely deleted.");
        return new(workspace, sessions);
    }

    public async Task DeleteAsync(string id, string confirmedRoot, CancellationToken ct = default)
    {
        await _deleteGate.WaitAsync(ct);
        string? root = null;
        var deleted = false;
        try
        {
            var preview = await PreviewAsync(id, ct);
            root = WorkspaceFiles.NormalizeRoot(preview.Workspace.Root);
            if (!WorkspaceFiles.SameRoot(root, confirmedRoot))
                throw new InvalidOperationException("The workspace folder changed. Open the confirmation again before deleting.");
            harness.Workspaces.SetDeleting(id, true);
            await runs.StopWorkspaceAsync(root, ct);
            await StopSessionsAsync(preview.Sessions, ct);
            // An agent may have spawned a child while its cancellation was settling.
            preview = await PreviewAsync(id, ct);
            await StopSessionsAsync(preview.Sessions, ct);
            ValidateDirectory(preview.Workspace);
            if (Directory.Exists(root)) await Task.Run(() => DeleteDirectoryTree(root, root), ct);

            foreach (var session in preview.Sessions)
            {
                await harness.Agents.RemoveAsync(session.Id);
                await harness.Attachments.DeleteSessionAsync(session.Id, ct);
                await harness.SearchIndex.PruneSessionAsync(session.Id, ct);
                await harness.Sessions.Delete(session.Id);
                harness.Workspaces.Archive(session.Id, false);
            }
            harness.Workspaces.Remove(id);
            deleted = true;
        }
        finally
        {
            if (root is not null) runs.FinishWorkspaceDeletion(root, deleted);
            harness.Workspaces.SetDeleting(id, false);
            _deleteGate.Release();
        }
    }

    private async Task StopSessionsAsync(IReadOnlyList<SessionHeader> sessions, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var agents = harness.Agents.LiveAgents().Where(a => ids.Contains(a.Id)).ToArray();
        foreach (var agent in agents) agent.Cancel(AgentCancelCause.User());
        await Task.WhenAll(agents.Select(a => a.WhenIdleAsync())).WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (harness.Context.TryGet<JobsRuntime>(JobsRuntime.ServiceKey) is { } jobs)
            await jobs.StopForSessionsAsync(ids, ct);
        if (harness.Context.TryGet<TerminalService>(TerminalService.ServiceKey) is { } terminals)
            foreach (var agent in agents)
                foreach (var terminal in terminals.List(agent)) terminals.Close(agent, terminal.SessionId);
    }

    private void ValidateDirectory(Workspace workspace)
    {
        var root = WorkspaceFiles.NormalizeRoot(workspace.Root);
        if (!Path.IsPathFullyQualified(workspace.Root) || Directory.GetParent(root) is null)
            throw new InvalidOperationException("A drive or filesystem root cannot be deleted as a workspace.");
        foreach (var protectedPath in new[] { AppContext.BaseDirectory, harness.DataDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
            if (!string.IsNullOrWhiteSpace(protectedPath) && WorkspaceFiles.IsInside(root, protectedPath))
                throw new InvalidOperationException("This folder contains the harness installation, its data, or your home directory and cannot be deleted here.");
        var nested = harness.Workspaces.List().FirstOrDefault(w => w.Id != workspace.Id && WorkspaceFiles.IsInside(root, w.Root));
        if (nested is not null)
            throw new InvalidOperationException($"This folder contains the registered workspace '{nested.Name}'. Delete that workspace first.");
        // Check every ancestor before destructive traversal; a registered junction must
        // not redirect deletion into a folder other than the one the dialog names.
        for (string? cursor = root; cursor is not null; cursor = Directory.GetParent(cursor)?.FullName)
        {
            if (!Directory.Exists(cursor)) continue;
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A linked workspace folder cannot be deleted here. Select its physical folder instead.");
        }
    }

    private static void DeleteDirectoryTree(string root, string directory)
    {
        if (!WorkspaceFiles.IsInside(root, directory)) throw new InvalidOperationException("Deletion path escapes the confirmed workspace.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!WorkspaceFiles.IsInside(root, entry)) throw new InvalidOperationException("Deletion path escapes the confirmed workspace.");
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (isDirectory) Directory.Delete(entry); else File.Delete(entry);
            }
            else if (isDirectory) DeleteDirectoryTree(root, entry);
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                File.Delete(entry);
            }
        }
        var directoryAttributes = File.GetAttributes(directory);
        if ((directoryAttributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(directory, directoryAttributes & ~FileAttributes.ReadOnly);
        Directory.Delete(directory);
    }
}
