using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IlliciumTTY.Models;
using IlliciumTTY.Services;

namespace IlliciumTTY.ViewModels;

public partial class MainWindowViewModel
{
    public void ReconnectNode(ConnectionNodeViewModel? node)
    {
        if (node?.IsSshLink != true)
        {
            return;
        }

        SelectWithoutActivation(node);
        WorkspaceMode = WorkspaceMode.Single;
        MultiSessions.Clear();
        RefreshMultiPreviewSessions();
        var workspace = GetOrCreateWorkspace(node);
        ActiveSession = workspace.TerminalSession;
        ActiveFileBrowser = workspace.FileBrowser;

        if (ActiveSession.Status is TerminalStatus.Connecting or TerminalStatus.Connected)
        {
            ActiveSession.BeginDisconnect();
        }

        ActiveSession.BeginConnect();
        _ = workspace.FileBrowser.EnsureLoadedAsync();

        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(IsMultiModeEmpty));
    }

    [RelayCommand]
    private void ToggleSyncMode()
    {
        SyncMode = SyncMode switch
        {
            SyncMode.Bidirectional => SyncMode.TerminalToFileManager,
            SyncMode.TerminalToFileManager => SyncMode.FileManagerToTerminal,
            SyncMode.FileManagerToTerminal => SyncMode.Disabled,
            _ => SyncMode.Bidirectional
        };
    }

    [RelayCommand]
    private void ToggleMultiTerminalInputSync()
    {
        IsMultiTerminalInputSyncEnabled = !IsMultiTerminalInputSyncEnabled;
    }

    private void OpenSshLink(ConnectionNodeViewModel link)
    {
        WorkspaceMode = WorkspaceMode.Single;
        MultiSessions.Clear();
        RefreshMultiPreviewSessions();
        var workspace = GetOrCreateWorkspace(link);
        ActiveSession = workspace.TerminalSession;
        ActiveFileBrowser = workspace.FileBrowser;
        ActiveSession.BeginConnect();
        _ = workspace.FileBrowser.EnsureLoadedAsync();

        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(IsMultiModeEmpty));
    }

    private void OpenFolder(ConnectionNodeViewModel folder)
    {
        WorkspaceMode = WorkspaceMode.Multi;
        ActiveFileBrowser = null;
        MultiSessions.Clear();

        foreach (var link in EnumerateLinks(folder.Children))
        {
            var session = GetOrCreateWorkspace(link).TerminalSession;
            MultiSessions.Add(session);
        }

        ActiveSession = MultiSessions.FirstOrDefault();
        foreach (var session in MultiSessions)
        {
            session.BeginConnectInBackground();
        }

        RefreshMultiPreviewSessions();
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(IsMultiModeEmpty));
    }

    private ConnectionWorkspaceState GetOrCreateWorkspace(ConnectionNodeViewModel link)
    {
        if (_workspacesByLinkId.TryGetValue(link.Id, out var workspace))
        {
            return workspace;
        }

        var session = new TerminalSessionViewModel(
            link,
            ActivateSession,
            CloseSession,
            OnTerminalPathChanged,
            OnTerminalUserInput);
        var fileBrowser = new RemoteFileBrowserViewModel(
            link,
            _sftpFileService,
            () => _shutdownCts.Token);
        fileBrowser.NavigatedFromFileManager += OnFileBrowserNavigatedFromFileManager;
        fileBrowser.OperationMessage += OnFileBrowserOperationMessage;

        workspace = new ConnectionWorkspaceState(link, session, fileBrowser);
        _workspacesByLinkId[link.Id] = workspace;
        return workspace;
    }

    private void ActivateSession(TerminalSessionViewModel session)
    {
        ActiveSession = session;
        if (WorkspaceMode == WorkspaceMode.Single &&
            _workspacesByLinkId.TryGetValue(session.Link.Id, out var workspace))
        {
            ActiveFileBrowser = workspace.FileBrowser;
        }
    }

    private void CloseSession(TerminalSessionViewModel session)
    {
        session.BeginDisconnect();
        MultiSessions.Remove(session);
        if (ReferenceEquals(ActiveSession, session))
        {
            ActiveSession = MultiSessions.FirstOrDefault();
        }

        RefreshMultiPreviewSessions();
        OnPropertyChanged(nameof(IsMultiModeEmpty));
    }

    private void RefreshMultiPreviewSessions()
    {
        MultiPreviewSessions.Clear();
        if (WorkspaceMode != WorkspaceMode.Multi)
        {
            return;
        }

        foreach (var session in MultiSessions.Where(session => !ReferenceEquals(session, ActiveSession)))
        {
            MultiPreviewSessions.Add(session);
        }
    }

    private void OnTerminalUserInput(TerminalSessionViewModel source, string input)
    {
        if (!IsMultiTerminalInputSyncEnabled ||
            WorkspaceMode != WorkspaceMode.Multi ||
            !ReferenceEquals(source, ActiveSession) ||
            string.IsNullOrEmpty(input))
        {
            return;
        }

        foreach (var session in MultiSessions)
        {
            if (ReferenceEquals(session, source))
            {
                continue;
            }

            if (session.Status == TerminalStatus.Connected)
            {
                session.WriteTerminalInput(input);
            }
            else if (session.Status == TerminalStatus.Connecting)
            {
                session.QueueInput(input);
            }
        }
    }

    private void OnTerminalPathChanged(TerminalSessionViewModel session, string path)
    {
        if (WorkspaceMode != WorkspaceMode.Single ||
            !ReferenceEquals(session, ActiveSession) ||
            ActiveFileBrowser is null ||
            !ShouldSyncTerminalToFileManager ||
            _syncSource is not null)
        {
            return;
        }

        _ = NavigateFromTerminalAsync(ActiveFileBrowser, path);
    }

    private async Task NavigateFromTerminalAsync(RemoteFileBrowserViewModel fileBrowser, string path)
    {
        try
        {
            _syncSource = "terminal";
            await fileBrowser.NavigateFromTerminalAsync(path);
        }
        finally
        {
            _syncSource = null;
        }
    }

    private void OnFileBrowserNavigatedFromFileManager(object? sender, RemoteFileBrowserNavigatedEventArgs e)
    {
        if (sender is not RemoteFileBrowserViewModel fileBrowser ||
            WorkspaceMode != WorkspaceMode.Single ||
            !ReferenceEquals(fileBrowser, ActiveFileBrowser) ||
            ActiveSession?.Status != TerminalStatus.Connected ||
            !ShouldSyncFileManagerToTerminal ||
            _syncSource is not null)
        {
            return;
        }

        try
        {
            _syncSource = "fileManager";
            ActiveSession.QueueCommand(SshCommandBuilder.BuildCdCommand(e.Path));
        }
        finally
        {
            _syncSource = null;
        }
    }

    private void OnFileBrowserOperationMessage(object? sender, string message)
    {
        ShowToast(message);
    }
}