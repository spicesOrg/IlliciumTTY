using System;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IlliciumTTY.Models;
using IlliciumTTY.Services;

namespace IlliciumTTY.ViewModels;

public sealed partial class TerminalSessionViewModel : ViewModelBase
{
    private const int MaxScrollbackLines = 4000;

    private readonly Action<TerminalSessionViewModel> _activate;
    private readonly Action<TerminalSessionViewModel>? _close;
    private readonly Action<TerminalSessionViewModel, string>? _pathChanged;
    private readonly TerminalTextBuffer _terminalBuffer = new(MaxScrollbackLines);
    private readonly object _terminalBufferLock = new();
    private readonly Action<TerminalSessionViewModel, string>? _userInputSubmitted;

    [ObservableProperty] private string? _currentPath;

    [ObservableProperty] private bool _isActive;
    private bool _isTerminalConnecting;

    [ObservableProperty] private string? _lastError;

    [ObservableProperty] private string? _pendingInput;

    [ObservableProperty] private TerminalStatus _status = TerminalStatus.Idle;
    private SshTerminalClient? _terminalClient;
    private CancellationTokenSource? _terminalConnectCts;

    public TerminalSessionViewModel(
        ConnectionNodeViewModel link,
        Action<TerminalSessionViewModel> activate,
        Action<TerminalSessionViewModel>? close = null,
        Action<TerminalSessionViewModel, string>? pathChanged = null,
        Action<TerminalSessionViewModel, string>? userInputSubmitted = null)
    {
        Id = Guid.NewGuid().ToString("N");
        Link = link;
        _activate = activate;
        _close = close;
        _pathChanged = pathChanged;
        _userInputSubmitted = userInputSubmitted;
        CurrentPath = string.IsNullOrWhiteSpace(link.DefaultRemotePath) ? "~" : link.DefaultRemotePath;

        ActivateCommand = new RelayCommand(() => _activate(this));
        ConnectCommand = new RelayCommand(BeginConnect, () => CanConnect);
        DisconnectCommand = new RelayCommand(BeginDisconnect, () => CanDisconnect);
        CloseCommand = new RelayCommand(() => _close?.Invoke(this));
    }

    public string Id { get; }
    public ConnectionNodeViewModel Link { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand CloseCommand { get; }

    public string Name => Link.Name;
    public string HostLabel => $"{Link.Username}@{Link.Host}:{Link.Port}";
    public string ConnectionPreview => $"SSH.NET · {Link.AuthLabel} · {HostLabel}";

    internal string TerminalText
    {
        get { return TerminalSnapshot.Text; }
    }

    internal TerminalTextSnapshot TerminalSnapshot
    {
        get
        {
            lock (_terminalBufferLock)
            {
                return _terminalBuffer.Snapshot;
            }
        }
    }

    public bool CanConnect => Status is TerminalStatus.Idle or TerminalStatus.Disconnected or TerminalStatus.Error;
    public bool CanDisconnect => Status is TerminalStatus.Connecting or TerminalStatus.Connected;
    public bool IsTerminalOverlayVisible => Status != TerminalStatus.Connected;

    public string StatusLabel => Status switch
    {
        TerminalStatus.Idle => "待连接",
        TerminalStatus.Connecting => "连接中",
        TerminalStatus.Connected => "已连接",
        TerminalStatus.Disconnected => "已断开",
        TerminalStatus.Error => "错误",
        _ => "未知"
    };

    public string Summary => Status == TerminalStatus.Error && !string.IsNullOrWhiteSpace(LastError)
        ? LastError
        : $"{StatusLabel} · {CurrentPath ?? "~"}";

    internal event EventHandler? TerminalTextChanged;

    partial void OnStatusChanged(TerminalStatus value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(IsTerminalOverlayVisible));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentPathChanged(string? value) => OnPropertyChanged(nameof(Summary));
    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(Summary));

    public void BeginConnect()
    {
        _activate(this);
        BeginConnectCore();
    }

    public void BeginConnectInBackground()
    {
        BeginConnectCore();
    }

    private void BeginConnectCore()
    {
        if (Status is TerminalStatus.Connected or TerminalStatus.Connecting)
        {
            return;
        }

        OnPropertyChanged(nameof(ConnectionPreview));
        LastError = null;
        Status = TerminalStatus.Connecting;
    }

    public void BeginDisconnect()
    {
        DisconnectTerminalClient();
        Status = TerminalStatus.Disconnected;
    }

    public void MarkConnected()
    {
        LastError = null;
        Status = TerminalStatus.Connected;
    }

    public void MarkDisconnected(int? exitCode = null, string? reason = null)
    {
        LastError = reason ?? FormatExitReason(exitCode);
        Status = exitCode is null or 0 ? TerminalStatus.Disconnected : TerminalStatus.Error;
    }

    public void MarkError(string message)
    {
        LastError = message;
        Status = TerminalStatus.Error;
    }

    internal bool TryBeginTerminalConnect(SshTerminalClient terminalClient, CancellationTokenSource connectCts)
    {
        if (_terminalClient is not null || _isTerminalConnecting)
        {
            return false;
        }

        lock (_terminalBufferLock)
        {
            _terminalBuffer.Clear();
        }

        RaiseTerminalTextChanged();

        _terminalClient = terminalClient;
        _terminalConnectCts = connectCts;
        _isTerminalConnecting = true;

        terminalClient.DataReceived += OnTerminalDataReceived;
        terminalClient.ErrorOccurred += OnTerminalErrorOccurred;
        terminalClient.Closed += OnTerminalClosed;
        return true;
    }

    internal bool CompleteTerminalConnect(SshTerminalClient terminalClient)
    {
        if (!ReferenceEquals(_terminalClient, terminalClient))
        {
            return false;
        }

        _isTerminalConnecting = false;
        DisposeConnectCancellation();
        return true;
    }

    internal bool IsCurrentTerminalClient(SshTerminalClient terminalClient)
    {
        return ReferenceEquals(_terminalClient, terminalClient);
    }

    internal void DisposeTerminalClient(SshTerminalClient terminalClient)
    {
        terminalClient.DataReceived -= OnTerminalDataReceived;
        terminalClient.ErrorOccurred -= OnTerminalErrorOccurred;
        terminalClient.Closed -= OnTerminalClosed;

        if (ReferenceEquals(_terminalClient, terminalClient))
        {
            var wasConnecting = _isTerminalConnecting;
            _terminalClient = null;
            _isTerminalConnecting = false;
            ClearConnectCancellation(dispose: !wasConnecting);
        }

        terminalClient.Dispose();
    }

    internal void WriteTerminalInput(string input)
    {
        _terminalClient?.Write(input.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    internal void SendUserInput(string input)
    {
        WriteTerminalInput(input);
        _userInputSubmitted?.Invoke(this, input);
    }

    internal void ChangeTerminalSize(SshTerminalSize terminalSize)
    {
        lock (_terminalBufferLock)
        {
            _terminalBuffer.Resize((int)terminalSize.Columns, (int)terminalSize.Rows);
        }

        _terminalClient?.ChangeWindowSize(terminalSize);
        RaiseTerminalTextChanged();
    }

    public void QueueCommand(string command)
    {
        QueueInput(command.EndsWith('\n') ? command : command + "\n");
    }

    public void QueueInput(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        PendingInput = (PendingInput ?? string.Empty) + input;
    }

    public void ClearPendingInput()
    {
        PendingInput = null;
    }

    public void ReportCurrentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        CurrentPath = path;
        _pathChanged?.Invoke(this, path);
    }

    private void DisconnectTerminalClient()
    {
        _terminalConnectCts?.Cancel();

        var terminalClient = _terminalClient;
        if (terminalClient is not null)
        {
            DisposeTerminalClient(terminalClient);
        }

        _isTerminalConnecting = false;
    }

    private void OnTerminalDataReceived(object? sender, string data)
    {
        if (!ReferenceEquals(sender, _terminalClient) || string.IsNullOrEmpty(data))
        {
            return;
        }

        string? detectedPath;
        lock (_terminalBufferLock)
        {
            detectedPath = _terminalBuffer.Append(data);
        }

        RaiseTerminalTextChanged();

        if (!string.IsNullOrWhiteSpace(detectedPath))
        {
            RunOnUiThread(() => ReportCurrentPath(detectedPath));
        }
    }

    private void OnTerminalErrorOccurred(object? sender, Exception ex)
    {
        var terminalClient = sender as SshTerminalClient;
        var message = SshConnectionDiagnostics.ExplainException(ex);

        RunOnUiThread(() =>
        {
            if (terminalClient is null || !ReferenceEquals(_terminalClient, terminalClient))
            {
                return;
            }

            DisposeTerminalClient(terminalClient);
            MarkError(message);
        });
    }

    private void OnTerminalClosed(object? sender, EventArgs e)
    {
        var terminalClient = sender as SshTerminalClient;

        RunOnUiThread(() =>
        {
            if (terminalClient is null || !ReferenceEquals(_terminalClient, terminalClient))
            {
                return;
            }

            DisposeTerminalClient(terminalClient);
            MarkDisconnected();
        });
    }

    private void RaiseTerminalTextChanged()
    {
        TerminalTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeConnectCancellation()
    {
        ClearConnectCancellation(dispose: true);
    }

    private void ClearConnectCancellation(bool dispose)
    {
        var connectCts = _terminalConnectCts;
        _terminalConnectCts = null;

        if (dispose)
        {
            connectCts?.Dispose();
        }
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private static string? FormatExitReason(int? exitCode)
    {
        return exitCode switch
        {
            null or 0 => null,
            _ => $"SSH 已断开，退出代码 {exitCode}。"
        };
    }
}