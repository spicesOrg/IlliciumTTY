using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IlliciumTTY.Models;
using IlliciumTTY.Services;

namespace IlliciumTTY.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int ToastAutoDismissMilliseconds = 5000;
    private const int ToastProgressUpdateMilliseconds = 50;

    private readonly AppConfig _config;
    private readonly ConfigRepository _configRepository = new();
    private readonly SftpFileService _sftpFileService = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Dictionary<string, ConnectionWorkspaceState> _workspacesByLinkId = [];

    [ObservableProperty] private RemoteFileBrowserViewModel? _activeFileBrowser;

    [ObservableProperty] private TerminalSessionViewModel? _activeSession;

    private ConnectionNodeViewModel? _editingNode;

    [ObservableProperty] private ChoiceOption<AuthType>? _editorAuthOption;

    [ObservableProperty] private string _editorDefaultRemotePath = ".";

    [ObservableProperty] private ChoiceOption<GroupType>? _editorGroupOption;

    [ObservableProperty] private string _editorHost = string.Empty;

    [ObservableProperty] private string _editorName = string.Empty;

    [ObservableProperty] private FolderChoiceOption? _editorParentFolder;

    [ObservableProperty] private string _editorPassphrase = string.Empty;

    [ObservableProperty] private string _editorPassword = string.Empty;

    [ObservableProperty] private string _editorPortText = "22";

    [ObservableProperty] private string _editorPrivateKeyPath = string.Empty;

    [ObservableProperty] private string _editorTitle = string.Empty;

    [ObservableProperty] private string _editorUsername = Environment.UserName;

    [ObservableProperty] private bool _isEditorForLink = true;

    [ObservableProperty] private bool _isEditorOpen;

    private bool _isInitializing = true;

    [ObservableProperty] private bool _isMultiTerminalInputSyncEnabled;
    private bool _isShuttingDown;
    private ConnectionNodeViewModel? _pendingDeleteNode;

    [ObservableProperty] private ConnectionNodeViewModel? _selectedNavNode;
    private ConnectionNodeViewModel? _selectedVisualNode;

    private bool _suppressSelectionActivation;

    [ObservableProperty] private SyncMode _syncMode = SyncMode.Bidirectional;

    private string? _syncSource;
    private CancellationTokenSource? _toastDismissCts;

    [ObservableProperty] private string? _toastMessage;

    [ObservableProperty] private double _toastProgress;

    [ObservableProperty] private WorkspaceMode _workspaceMode = WorkspaceMode.Single;

    public MainWindowViewModel()
    {
        GroupOptions =
        [
            new ChoiceOption<GroupType>("收藏链接", GroupType.Favorite),
            new ChoiceOption<GroupType>("临时链接", GroupType.Temporary)
        ];

        AuthOptions =
        [
            new ChoiceOption<AuthType>("密码", AuthType.Password),
            new ChoiceOption<AuthType>("私钥", AuthType.PrivateKey),
            new ChoiceOption<AuthType>("SSH Agent", AuthType.Agent)
        ];

        _config = _configRepository.LoadAsync().GetAwaiter().GetResult();
        SyncMode = _config.DefaultSyncMode;

        LoadNodes(_config.FavoriteNodes, FavoriteNodes);
        ApplyExpandedState(FavoriteNodes);

        SelectedNavNode = FindNode(_config.LastOpenedNodeId)
                          ?? FavoriteNodes.FirstOrDefault();
        _isInitializing = false;
    }

    public ObservableCollection<ConnectionNodeViewModel> FavoriteNodes { get; } = [];
    public ObservableCollection<ConnectionNodeViewModel> TemporaryNodes { get; } = [];
    public ObservableCollection<TerminalSessionViewModel> MultiSessions { get; } = [];
    public ObservableCollection<TerminalSessionViewModel> MultiPreviewSessions { get; } = [];
    public ObservableCollection<FolderChoiceOption> FolderOptions { get; } = [];
    public IReadOnlyList<ChoiceOption<GroupType>> GroupOptions { get; }
    public IReadOnlyList<ChoiceOption<AuthType>> AuthOptions { get; }

    public bool IsSingleMode => WorkspaceMode == WorkspaceMode.Single;
    public bool IsMultiMode => WorkspaceMode == WorkspaceMode.Multi;
    public bool IsMultiModeEmpty => WorkspaceMode == WorkspaceMode.Multi && MultiSessions.Count == 0;
    public bool HasSelection => SelectedNavNode is not null;
    public bool HasToast => !string.IsNullOrWhiteSpace(ToastMessage);
    public bool IsEditorForFolder => !IsEditorForLink;

    public string WorkspaceTitle => SelectedNavNode is null
        ? "未选择连接"
        : WorkspaceMode == WorkspaceMode.Single
            ? SelectedNavNode.Name
            : $"{SelectedNavNode.Name} · 多终端";

    public string WorkspaceSubtitle => ActiveSession is null
        ? "请选择左侧 SSH 链接或文件夹"
        : ActiveSession.HostLabel;

    public string SyncModeLabel => SyncMode switch
    {
        SyncMode.TerminalToFileManager => "同步终端",
        SyncMode.FileManagerToTerminal => "同步文件管理",
        SyncMode.Bidirectional => "双向同步",
        SyncMode.Disabled => "禁用同步",
        _ => "同步"
    };

    public bool IsBidirectionalSyncMode => SyncMode == SyncMode.Bidirectional;
    public bool IsTerminalToFileManagerSyncMode => SyncMode == SyncMode.TerminalToFileManager;
    public bool IsFileManagerToTerminalSyncMode => SyncMode == SyncMode.FileManagerToTerminal;
    public bool IsSyncDisabledMode => SyncMode == SyncMode.Disabled;
    public bool IsMultiTerminalInputSyncDisabled => !IsMultiTerminalInputSyncEnabled;

    public string MultiTerminalInputSyncLabel => IsMultiTerminalInputSyncEnabled
        ? "多终端输入同步：开启"
        : "多终端输入同步：关闭";

    private bool ShouldSyncTerminalToFileManager =>
        SyncMode is SyncMode.TerminalToFileManager or SyncMode.Bidirectional;

    private bool ShouldSyncFileManagerToTerminal =>
        SyncMode is SyncMode.FileManagerToTerminal or SyncMode.Bidirectional;

    public void Shutdown()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _shutdownCts.Cancel();
        IsEditorOpen = false;
        ToastMessage = null;

        foreach (var workspace in _workspacesByLinkId.Values.ToList())
        {
            workspace.FileBrowser.NavigatedFromFileManager -= OnFileBrowserNavigatedFromFileManager;
            workspace.FileBrowser.OperationMessage -= OnFileBrowserOperationMessage;
            workspace.FileBrowser.Dispose();
            workspace.TerminalSession.BeginDisconnect();
        }

        ActiveSession = null;
        ActiveFileBrowser = null;
        MultiSessions.Clear();
        MultiPreviewSessions.Clear();
    }

    partial void OnSelectedNavNodeChanged(ConnectionNodeViewModel? value)
    {
        if (_selectedVisualNode is not null)
        {
            _selectedVisualNode.IsSelected = false;
        }

        _selectedVisualNode = value;
        if (_selectedVisualNode is not null)
        {
            _selectedVisualNode.IsSelected = true;
        }

        OnPropertyChanged(nameof(HasSelection));
        EditSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        _pendingDeleteNode = null;

        if (value is null)
        {
            return;
        }

        if (_suppressSelectionActivation)
        {
            if (!_isInitializing)
            {
                SaveConfig();
            }

            return;
        }

        if (value.IsFolder)
        {
            OpenFolder(value);
        }
        else
        {
            OpenSshLink(value);
        }

        if (!_isInitializing)
        {
            SaveConfig();
        }
    }

    partial void OnWorkspaceModeChanged(WorkspaceMode value)
    {
        OnPropertyChanged(nameof(IsSingleMode));
        OnPropertyChanged(nameof(IsMultiMode));
        OnPropertyChanged(nameof(IsMultiModeEmpty));
        OnPropertyChanged(nameof(WorkspaceTitle));
        RefreshMultiPreviewSessions();
    }

    partial void OnActiveSessionChanged(TerminalSessionViewModel? value)
    {
        foreach (var session in _workspacesByLinkId.Values.Select(workspace => workspace.TerminalSession))
        {
            session.IsActive = ReferenceEquals(session, value);
        }

        OnPropertyChanged(nameof(WorkspaceSubtitle));
        RefreshMultiPreviewSessions();
    }

    partial void OnSyncModeChanged(SyncMode value)
    {
        OnPropertyChanged(nameof(SyncModeLabel));
        OnPropertyChanged(nameof(IsBidirectionalSyncMode));
        OnPropertyChanged(nameof(IsTerminalToFileManagerSyncMode));
        OnPropertyChanged(nameof(IsFileManagerToTerminalSyncMode));
        OnPropertyChanged(nameof(IsSyncDisabledMode));
        if (!_isInitializing)
        {
            SaveConfig();
        }
    }

    partial void OnIsMultiTerminalInputSyncEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMultiTerminalInputSyncDisabled));
        OnPropertyChanged(nameof(MultiTerminalInputSyncLabel));
    }

    partial void OnToastMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasToast));
        RestartToastAutoDismiss(value);
    }

    partial void OnIsEditorForLinkChanged(bool value) => OnPropertyChanged(nameof(IsEditorForFolder));

    partial void OnEditorGroupOptionChanged(ChoiceOption<GroupType>? value)
    {
        RefreshFolderOptions();
    }

    [RelayCommand]
    private void DismissToast()
    {
        ToastMessage = null;
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            ToastMessage = null;
            return;
        }

        if (ToastMessage == message)
        {
            RestartToastAutoDismiss(message);
            return;
        }

        ToastMessage = message;
    }

    private void RestartToastAutoDismiss(string? message)
    {
        CancelToastAutoDismiss();

        if (string.IsNullOrWhiteSpace(message))
        {
            ToastProgress = 0;
            return;
        }

        ToastProgress = 100;
        var dismissCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        _toastDismissCts = dismissCts;
        _ = RunToastAutoDismissAsync(message, dismissCts);
    }

    private void CancelToastAutoDismiss()
    {
        _toastDismissCts?.Cancel();
        _toastDismissCts = null;
    }

    private async Task RunToastAutoDismissAsync(string message, CancellationTokenSource dismissCts)
    {
        var token = dismissCts.Token;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            while (true)
            {
                var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
                var remaining = Math.Max(0, ToastAutoDismissMilliseconds - elapsed);
                ToastProgress = remaining / ToastAutoDismissMilliseconds * 100;

                if (remaining <= 0)
                {
                    break;
                }

                var delay = Math.Max(1, (int)Math.Ceiling(Math.Min(ToastProgressUpdateMilliseconds, remaining)));
                await Task.Delay(delay, token);
            }

            if (!token.IsCancellationRequested && ToastMessage == message)
            {
                ToastMessage = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_toastDismissCts, dismissCts))
            {
                _toastDismissCts = null;
            }

            dismissCts.Dispose();
        }
    }
}