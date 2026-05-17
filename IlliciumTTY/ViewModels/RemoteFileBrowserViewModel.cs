using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IlliciumTTY.Models;
using IlliciumTTY.Services;

namespace IlliciumTTY.ViewModels;

public sealed partial class RemoteFileBrowserViewModel : ViewModelBase, IDisposable
{
    private const int AutoUploadDebounceMilliseconds = 900;
    private readonly Dictionary<string, RemoteEditSession> _editSessionsByRemotePath = [];
    private readonly object _editSessionsLock = new();
    private readonly Func<CancellationToken> _getCancellationToken;
    private readonly SftpFileService _sftpFileService;

    [ObservableProperty] private string _addressBarText = ".";

    [ObservableProperty] private string? _fileManagerError;

    [ObservableProperty] private string _fileManagerPath = ".";
    private bool _isDisposed;

    [ObservableProperty] private bool _isFileManagerBusy;
    private int _navigationRequestVersion;
    private bool _sortAscending = true;
    private string _sortKey = "name";

    public RemoteFileBrowserViewModel(
        ConnectionNodeViewModel link,
        SftpFileService sftpFileService,
        Func<CancellationToken> getCancellationToken)
    {
        Link = link;
        _sftpFileService = sftpFileService;
        _getCancellationToken = getCancellationToken;

        FileManagerPath = string.IsNullOrWhiteSpace(link.DefaultRemotePath) ? "." : link.DefaultRemotePath!;
        AddressBarText = FileManagerPath;
    }

    public ConnectionNodeViewModel Link { get; }
    public ObservableCollection<RemoteFileItemViewModel> RemoteFiles { get; } = [];
    public bool HasFileManagerError => !string.IsNullOrWhiteSpace(FileManagerError);
    public bool HasLoaded { get; private set; }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        List<RemoteEditSession> sessions;
        lock (_editSessionsLock)
        {
            sessions = _editSessionsByRemotePath.Values.ToList();
            _editSessionsByRemotePath.Clear();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    public event EventHandler<RemoteFileBrowserNavigatedEventArgs>? NavigatedFromFileManager;
    public event EventHandler<string>? OperationMessage;

    partial void OnFileManagerErrorChanged(string? value) => OnPropertyChanged(nameof(HasFileManagerError));

    public async Task EnsureLoadedAsync()
    {
        if (HasLoaded)
        {
            return;
        }

        await NavigateAsync(FileManagerPath, syncTerminal: false);
    }

    public async Task NavigateFromTerminalAsync(string path)
    {
        await NavigateAsync(path, syncTerminal: false);
    }

    public async Task OpenItemAsync(RemoteFileItemViewModel item)
    {
        await OpenRemoteFileItemAsync(item);
    }

    public async Task CreateFileAsync(string name)
    {
        if (!TryNormalizeEntryName(name, out var normalizedName, out var error))
        {
            FileManagerError = error;
            return;
        }

        await RunFileOperationAsync(
            token => _sftpFileService.CreateFileAsync(
                Link.ToLinkModel(),
                CombineRemotePath(FileManagerPath, normalizedName),
                token),
            $"已新建文件：{normalizedName}");
    }

    public async Task CreateDirectoryAsync(string name)
    {
        if (!TryNormalizeEntryName(name, out var normalizedName, out var error))
        {
            FileManagerError = error;
            return;
        }

        await RunFileOperationAsync(
            token => _sftpFileService.CreateDirectoryAsync(
                Link.ToLinkModel(),
                CombineRemotePath(FileManagerPath, normalizedName),
                token),
            $"已新建文件夹：{normalizedName}");
    }

    public async Task DeleteAsync(RemoteFileItemViewModel item)
    {
        if (await RunFileOperationAsync(
                token => _sftpFileService.DeleteAsync(
                    Link.ToLinkModel(),
                    item.Path,
                    item.Type,
                    token),
                $"已删除：{item.Name}"))
        {
            StopTrackingRemoteEdit(item.Path);
        }
    }

    public async Task RenameAsync(RemoteFileItemViewModel item, string newName)
    {
        if (!TryNormalizeEntryName(newName, out var normalizedName, out var error))
        {
            FileManagerError = error;
            return;
        }

        if (string.Equals(item.Name, normalizedName, StringComparison.CurrentCulture))
        {
            return;
        }

        var oldPath = item.Path;
        var newPath = GetSiblingPath(item.Path, normalizedName);
        if (await RunFileOperationAsync(
                token => _sftpFileService.RenameAsync(
                    Link.ToLinkModel(),
                    oldPath,
                    newPath,
                    token),
                $"已重命名：{item.Name} -> {normalizedName}"))
        {
            StopTrackingRemoteEdit(oldPath);
        }
    }

    public async Task ChangePermissionsAsync(RemoteFileItemViewModel item, string modeText)
    {
        if (!TryParseUnixMode(modeText, out var mode, out var normalizedMode))
        {
            FileManagerError = "权限格式无效，请输入 644、755 或 0755 这样的八进制模式";
            return;
        }

        await RunFileOperationAsync(
            token => _sftpFileService.ChangePermissionsAsync(
                Link.ToLinkModel(),
                item.Path,
                mode,
                token),
            $"已修改权限：{item.Name} {normalizedMode}");
    }

    [RelayCommand]
    private async Task RefreshFileManagerAsync()
    {
        await NavigateAsync(FileManagerPath, syncTerminal: false);
    }

    [RelayCommand]
    private async Task CommitAddressBarAsync()
    {
        await NavigateAsync(AddressBarText, syncTerminal: true);
    }

    [RelayCommand]
    private async Task NavigateUpAsync()
    {
        await NavigateAsync(GetParentPath(FileManagerPath), syncTerminal: true);
    }

    [RelayCommand]
    private void SortRemoteFiles(string key)
    {
        if (_sortKey == key)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortKey = key;
            _sortAscending = true;
        }

        SortRemoteFilesInPlace();
    }

    private async Task NavigateAsync(string path, bool syncTerminal)
    {
        var requestVersion = Interlocked.Increment(ref _navigationRequestVersion);
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? "." : path.Trim();
        var cancellationToken = _getCancellationToken();

        IsFileManagerBusy = true;
        FileManagerError = null;

        try
        {
            var files = await _sftpFileService.ListDirectoryAsync(
                Link.ToLinkModel(),
                normalizedPath,
                cancellationToken);

            if (requestVersion != _navigationRequestVersion)
            {
                return;
            }

            RemoteFiles.Clear();
            foreach (var file in files)
            {
                RemoteFiles.Add(new RemoteFileItemViewModel(file, OpenRemoteFileItemAsync));
            }

            SortRemoteFilesInPlace();
            FileManagerPath = normalizedPath;
            AddressBarText = normalizedPath;
            HasLoaded = true;

            if (syncTerminal)
            {
                NavigatedFromFileManager?.Invoke(this, new RemoteFileBrowserNavigatedEventArgs(normalizedPath));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (requestVersion != _navigationRequestVersion)
            {
                return;
            }

            FileManagerError = $"无法打开路径：{normalizedPath}。原因：{ex.Message}";
        }
        finally
        {
            if (requestVersion == _navigationRequestVersion)
            {
                IsFileManagerBusy = false;
            }
        }
    }

    private async Task OpenRemoteFileItemAsync(RemoteFileItemViewModel item)
    {
        if (!item.CanOpen)
        {
            return;
        }

        if (item.Type == RemoteFileType.Directory)
        {
            await NavigateAsync(item.Path, syncTerminal: true);
            return;
        }

        await DownloadAndOpenFileAsync(item);
    }

    private async Task DownloadAndOpenFileAsync(RemoteFileItemViewModel item)
    {
        if (TryGetTrackedLocalPath(item.Path, out var trackedLocalPath) && File.Exists(trackedLocalPath))
        {
            OpenLocalFile(trackedLocalPath);
            PublishMessage($"已打开：{item.Name}");
            return;
        }

        var cancellationToken = _getCancellationToken();
        IsFileManagerBusy = true;
        FileManagerError = null;

        try
        {
            var localPath = GetLocalEditPath(item);
            await _sftpFileService.DownloadFileAsync(
                Link.ToLinkModel(),
                item.Path,
                localPath,
                cancellationToken);

            TrackRemoteEdit(item.Path, localPath);
            OpenLocalFile(localPath);
            PublishMessage($"已打开：{item.Name}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FileManagerError = $"无法打开文件：{item.Name}。原因：{ex.Message}";
        }
        finally
        {
            IsFileManagerBusy = false;
        }
    }

    private async Task<bool> RunFileOperationAsync(
        Func<CancellationToken, Task> operation,
        string successMessage)
    {
        var cancellationToken = _getCancellationToken();
        IsFileManagerBusy = true;
        FileManagerError = null;

        try
        {
            await operation(cancellationToken);
            PublishMessage(successMessage);
            await NavigateAsync(FileManagerPath, syncTerminal: false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            FileManagerError = $"文件操作失败：{ex.Message}";
            return false;
        }
        finally
        {
            IsFileManagerBusy = false;
        }
    }

    private void TrackRemoteEdit(string remotePath, string localPath)
    {
        var session = new RemoteEditSession(remotePath, localPath);
        session.Watcher.Changed += (_, e) => OnLocalEditChanged(session, e.Name);
        session.Watcher.Created += (_, e) => OnLocalEditChanged(session, e.Name);
        session.Watcher.Renamed += (_, e) => OnLocalEditChanged(session, e.Name);
        session.Watcher.EnableRaisingEvents = true;

        RemoteEditSession? existing = null;
        lock (_editSessionsLock)
        {
            if (_editSessionsByRemotePath.Remove(remotePath, out var removed))
            {
                existing = removed;
            }

            _editSessionsByRemotePath[remotePath] = session;
        }

        existing?.Dispose();
    }

    private bool TryGetTrackedLocalPath(string remotePath, out string localPath)
    {
        lock (_editSessionsLock)
        {
            if (_editSessionsByRemotePath.TryGetValue(remotePath, out var session))
            {
                localPath = session.LocalPath;
                return true;
            }
        }

        localPath = string.Empty;
        return false;
    }

    private void StopTrackingRemoteEdit(string remotePath)
    {
        RemoteEditSession? session = null;
        lock (_editSessionsLock)
        {
            if (_editSessionsByRemotePath.Remove(remotePath, out var removed))
            {
                session = removed;
            }
        }

        session?.Dispose();
    }

    private void OnLocalEditChanged(RemoteEditSession session, string? changedName)
    {
        if (_isDisposed ||
            string.IsNullOrWhiteSpace(changedName) ||
            !string.Equals(changedName, Path.GetFileName(session.LocalPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ScheduleUpload(session);
    }

    private void ScheduleUpload(RemoteEditSession session)
    {
        var cancellationToken = _getCancellationToken();
        var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (session.SyncRoot)
        {
            session.PendingUploadCts?.Cancel();
            session.PendingUploadCts?.Dispose();
            session.PendingUploadCts = uploadCts;
        }

        _ = UploadChangedLocalFileAsync(session, uploadCts);
    }

    private async Task UploadChangedLocalFileAsync(RemoteEditSession session, CancellationTokenSource uploadCts)
    {
        var cancellationToken = uploadCts.Token;

        try
        {
            await Task.Delay(AutoUploadDebounceMilliseconds, cancellationToken);

            if (!File.Exists(session.LocalPath))
            {
                return;
            }

            await session.UploadGate.WaitAsync(cancellationToken);
            try
            {
                var lastWriteTimeUtc = File.GetLastWriteTimeUtc(session.LocalPath);
                if (lastWriteTimeUtc <= session.LastUploadedWriteTimeUtc)
                {
                    return;
                }

                await WaitForFileReadyAsync(session.LocalPath, cancellationToken);
                await _sftpFileService.UploadFileAsync(
                    Link.ToLinkModel(),
                    session.LocalPath,
                    session.RemotePath,
                    cancellationToken);

                session.LastUploadedWriteTimeUtc = File.GetLastWriteTimeUtc(session.LocalPath);
                PublishMessage($"已自动上传：{GetRemoteName(session.RemotePath)}");
            }
            finally
            {
                session.UploadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (!_isDisposed)
        {
            SetFileManagerError($"自动上传失败：{GetRemoteName(session.RemotePath)}。原因：{ex.Message}");
        }
        finally
        {
            lock (session.SyncRoot)
            {
                if (ReferenceEquals(session.PendingUploadCts, uploadCts))
                {
                    session.PendingUploadCts = null;
                }
            }

            uploadCts.Dispose();
        }
    }

    private static async Task WaitForFileReadyAsync(string localPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using (File.Open(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                }

                return;
            }
            catch (IOException) when (attempt < 11)
            {
                await Task.Delay(180, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < 11)
            {
                await Task.Delay(180, cancellationToken);
            }
        }
    }

    private void PublishMessage(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            OperationMessage?.Invoke(this, message);
            return;
        }

        Dispatcher.UIThread.Post(() => OperationMessage?.Invoke(this, message));
    }

    private void SetFileManagerError(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            FileManagerError = message;
            return;
        }

        Dispatcher.UIThread.Post(() => FileManagerError = message);
    }

    private void SortRemoteFilesInPlace()
    {
        IEnumerable<RemoteFileItemViewModel> sorted = _sortKey switch
        {
            "size" => RemoteFiles.OrderBy(item => item.Type != RemoteFileType.Directory).ThenBy(item => item.Item.Size),
            "type" => RemoteFiles.OrderBy(item => item.TypeLabel).ThenBy(item => item.Name),
            "modified" => RemoteFiles.OrderBy(item => item.Item.ModifiedAt),
            _ => RemoteFiles.OrderBy(item => item.Type != RemoteFileType.Directory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        if (!_sortAscending)
        {
            sorted = sorted.Reverse();
        }

        var ordered = sorted.ToList();
        RemoteFiles.Clear();
        foreach (var item in ordered)
        {
            RemoteFiles.Add(item);
        }
    }

    private string GetLocalEditPath(RemoteFileItemViewModel item)
    {
        var localRoot = Path.Combine(
            Path.GetTempPath(),
            "IlliciumTTY",
            "remote-edits",
            SanitizeFileName(Link.Id));
        var remotePathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Path)))[..16];
        var localDirectory = Path.Combine(localRoot, remotePathHash);
        return Path.Combine(localDirectory, SanitizeFileName(item.Name));
    }

    private static void OpenLocalFile(string localPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = localPath,
            UseShellExecute = true
        });
    }

    private static bool TryNormalizeEntryName(string name, out string normalizedName, out string? error)
    {
        normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            error = "名称不能为空";
            return false;
        }

        if (normalizedName is "." or ".." ||
            normalizedName.Contains('/') ||
            normalizedName.Contains('\\'))
        {
            error = "名称不能是 . 或 ..，也不能包含路径分隔符";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseUnixMode(string modeText, out short mode, out string normalizedMode)
    {
        normalizedMode = modeText.Trim();
        if (normalizedMode.Length == 4 && normalizedMode[0] == '0')
        {
            normalizedMode = normalizedMode[1..];
        }

        if (normalizedMode.Length != 3 ||
            normalizedMode.Any(character => character is < '0' or > '7'))
        {
            mode = 0;
            return false;
        }

        mode = Convert.ToInt16(normalizedMode, 8);
        return true;
    }

    private static string CombineRemotePath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory == ".")
        {
            return name;
        }

        return directory.EndsWith("/", StringComparison.Ordinal)
            ? directory + name
            : $"{directory}/{name}";
    }

    private static string GetSiblingPath(string path, string newName)
    {
        var trimmed = path.TrimEnd('/');
        var slashIndex = trimmed.LastIndexOf('/');
        if (slashIndex < 0)
        {
            return newName;
        }

        return slashIndex == 0
            ? $"/{newName}"
            : $"{trimmed[..slashIndex]}/{newName}";
    }

    private static string GetParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "." || path == "/")
        {
            return path == "/" ? "/" : "..";
        }

        var trimmed = path.TrimEnd('/');
        var slashIndex = trimmed.LastIndexOf('/');

        if (slashIndex <= 0)
        {
            return ".";
        }

        return trimmed[..slashIndex];
    }

    private static string GetRemoteName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slashIndex = trimmed.LastIndexOf('/');
        return slashIndex < 0 ? trimmed : trimmed[(slashIndex + 1)..];
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length == 0 ? "remote-file".Length : value.Length);
        foreach (var character in value.Length == 0 ? "remote-file" : value)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private sealed class RemoteEditSession : IDisposable
    {
        public RemoteEditSession(string remotePath, string localPath)
        {
            RemotePath = remotePath;
            LocalPath = localPath;
            LastUploadedWriteTimeUtc = File.GetLastWriteTimeUtc(localPath);
            Watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
            {
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };
        }

        public string RemotePath { get; }
        public string LocalPath { get; }
        public FileSystemWatcher Watcher { get; }
        public DateTime LastUploadedWriteTimeUtc { get; set; }
        public CancellationTokenSource? PendingUploadCts { get; set; }
        public SemaphoreSlim UploadGate { get; } = new(1, 1);
        public object SyncRoot { get; } = new();

        public void Dispose()
        {
            lock (SyncRoot)
            {
                PendingUploadCts?.Cancel();
                PendingUploadCts?.Dispose();
                PendingUploadCts = null;
            }

            Watcher.Dispose();
        }
    }
}

public sealed class RemoteFileBrowserNavigatedEventArgs(string path) : EventArgs
{
    public string Path { get; } = path;
}