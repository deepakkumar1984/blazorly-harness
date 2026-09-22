namespace Blazorly.Harness.Web.Services;

public sealed record WorkspaceFileRequest(string Root, string RelativePath, long Version);
public sealed record WorkspaceChatRequest(string SessionId, long Version);
public sealed record WorkspaceTab(string Key, string Title, string? SessionId = null, string? RelativePath = null, bool Dirty = false)
{
    public bool IsChat => SessionId is not null;
    public static string ChatKey(string sessionId) => "chat:" + sessionId;
    public static string FileKey(string path) => "file:" + path;
}

/// <summary>Navigation shared by the sidebar and editor in one browser circuit.</summary>
public sealed class WorkspaceUiState
{
    public event Action? Changed;
    public WorkspaceFileRequest? FileRequest { get; private set; }
    public WorkspaceChatRequest? ChatRequest { get; private set; }
    public string? ActiveFile { get; private set; }
    public string? Root { get; private set; }
    public string? ActiveTabKey { get; private set; }
    public long SelectionVersion { get; private set; }
    public IReadOnlyList<WorkspaceTab> Tabs => _tabs;
    public WorkspaceTab? ActiveTab => _tabs.FirstOrDefault(t => t.Key == ActiveTabKey);
    public string SidebarTab { get; private set; } = "chats";
    private readonly List<WorkspaceTab> _tabs = [];
    private string? _deletedRoot;
    private long _version;

    public void EnterWorkspace(string? root)
    {
        if (!WasDeleted(root) && (Root == root || WorkspaceFiles.SameRoot(Root, root))) return;
        Root = root;
        _tabs.Clear();
        ActiveTabKey = ActiveFile = null;
        if (FileRequest is { } request && !WorkspaceFiles.SameRoot(root, request.Root)) FileRequest = null;
        _deletedRoot = null;
        SelectionVersion++;
    }

    public void RequestChat(string sessionId)
    {
        ChatRequest = new(sessionId, ++_version);
        SidebarTab = "chats";
        Changed?.Invoke();
    }

    public void OpenChat(string? root, string sessionId, string title)
    {
        EnterWorkspace(root);
        var tab = new WorkspaceTab(WorkspaceTab.ChatKey(sessionId), title, SessionId: sessionId);
        Upsert(tab);
        ActiveTabKey = tab.Key;
        SelectionVersion++;
        Changed?.Invoke();
    }

    public void UpdateChatTitle(string sessionId, string title)
    {
        var index = _tabs.FindIndex(t => t.SessionId == sessionId);
        if (index < 0 || _tabs[index].Title == title) return;
        _tabs[index] = _tabs[index] with { Title = title };
        Changed?.Invoke();
    }

    public void ShowFiles()
    {
        SidebarTab = "files";
        Changed?.Invoke();
    }

    public void ShowChats()
    {
        SidebarTab = "chats";
        Changed?.Invoke();
    }

    public void OpenFile(string root, string relativePath)
    {
        FileRequest = new(root, relativePath, ++_version);
        var key = WorkspaceTab.FileKey(relativePath);
        if (_tabs.Any(t => t.Key == key) && WorkspaceFiles.SameRoot(root, Root))
        {
            ActiveTabKey = key;
            SelectionVersion++;
        }
        SidebarTab = "files";
        Changed?.Invoke();
    }

    public void SelectFile(string? relativePath)
    {
        if (ActiveFile == relativePath) return;
        ActiveFile = relativePath;
        Changed?.Invoke();
    }

    public void FileOpened(string relativePath, bool dirty)
    {
        var tab = new WorkspaceTab(WorkspaceTab.FileKey(relativePath), Path.GetFileName(relativePath), RelativePath: relativePath, Dirty: dirty);
        Upsert(tab);
        ActiveFile = relativePath;
        ActiveTabKey = tab.Key;
        SelectionVersion++;
        Changed?.Invoke();
    }

    public void SetFileDirty(string relativePath, bool dirty)
    {
        var index = _tabs.FindIndex(t => t.RelativePath == relativePath);
        if (index < 0 || _tabs[index].Dirty == dirty) return;
        _tabs[index] = _tabs[index] with { Dirty = dirty };
        Changed?.Invoke();
    }

    public void CloseTab(string key)
    {
        var index = _tabs.FindIndex(t => t.Key == key);
        if (index < 0) return;
        var wasActive = ActiveTabKey == key;
        _tabs.RemoveAt(index);
        if (wasActive)
        {
            var next = _tabs.ElementAtOrDefault(Math.Min(index, _tabs.Count - 1));
            ActiveTabKey = next?.Key;
            if (next?.RelativePath is { } path && Root is { } root)
            {
                ActiveFile = path;
                FileRequest = new(root, path, ++_version);
            }
            SelectionVersion++;
        }
        if (!_tabs.Any(t => t.RelativePath == ActiveFile)) ActiveFile = null;
        Changed?.Invoke();
    }

    public void CloseAllTabs()
    {
        _tabs.Clear();
        ActiveTabKey = ActiveFile = null;
        FileRequest = null;
        SelectionVersion++;
        Changed?.Invoke();
    }

    public void WorkspaceDeleted(string root)
    {
        if (!WorkspaceFiles.SameRoot(root, Root)) return;
        _deletedRoot = root;
        CloseAllTabs();
    }

    public bool WasDeleted(string? root) => WorkspaceFiles.SameRoot(root, _deletedRoot);

    private void Upsert(WorkspaceTab tab)
    {
        var index = _tabs.FindIndex(t => t.Key == tab.Key);
        if (index < 0) _tabs.Add(tab);
        else _tabs[index] = tab;
    }
}
