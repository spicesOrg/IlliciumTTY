using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IlliciumTTY.Models;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace IlliciumTTY.Services;

public sealed class SftpFileService
{
    public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(
        ConnectionNodeModel link,
        string path,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateConnectedClient(link);

            var targetPath = string.IsNullOrWhiteSpace(path) ? "." : path;
            var items = client.ListDirectory(targetPath)
                .Where(file => file.Name is not "." and not "..")
                .Select(file => ToRemoteFileItem(file))
                .OrderByDescending(item => item.Type == RemoteFileType.Directory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            client.Disconnect();
            return (IReadOnlyList<RemoteFileItem>)items;
        }, cancellationToken);
    }

    public Task CreateDirectoryAsync(
        ConnectionNodeModel link,
        string path,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateConnectedClient(link);
            if (client.Exists(path))
            {
                throw new IOException($"远程路径已存在：{path}");
            }

            client.CreateDirectory(path);
            client.Disconnect();
        }, cancellationToken);
    }

    public Task CreateFileAsync(
        ConnectionNodeModel link,
        string path,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateConnectedClient(link);
            if (client.Exists(path))
            {
                throw new IOException($"远程路径已存在：{path}");
            }

            using (client.Create(path))
            {
            }

            client.Disconnect();
        }, cancellationToken);
    }

    public Task DeleteAsync(
        ConnectionNodeModel link,
        string path,
        RemoteFileType type,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateConnectedClient(link);
            if (type == RemoteFileType.Directory)
            {
                DeleteDirectoryRecursive(client, path, cancellationToken);
            }
            else
            {
                client.DeleteFile(path);
            }

            client.Disconnect();
        }, cancellationToken);
    }

    public Task RenameAsync(
        ConnectionNodeModel link,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateConnectedClient(link);
            if (client.Exists(newPath))
            {
                throw new IOException($"远程路径已存在：{newPath}");
            }

            client.RenameFile(oldPath, newPath);
            client.Disconnect();
        }, cancellationToken);
    }

    public Task ChangePermissionsAsync(
        ConnectionNodeModel link,
        string path,
        short mode,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateConnectedClient(link);
            client.ChangePermissions(path, mode);
            client.Disconnect();
        }, cancellationToken);
    }

    public Task DownloadFileAsync(
        ConnectionNodeModel link,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localDirectory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(localDirectory))
            {
                Directory.CreateDirectory(localDirectory);
            }

            using var client = CreateConnectedClient(link);
            using var output = File.Create(localPath);
            client.DownloadFile(remotePath, output);
            client.Disconnect();
        }, cancellationToken);
    }

    public Task UploadFileAsync(
        ConnectionNodeModel link,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateConnectedClient(link);
            using var input = File.Open(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            client.UploadFile(input, remotePath, true);
            client.Disconnect();
        }, cancellationToken);
    }

    private static SftpClient CreateConnectedClient(ConnectionNodeModel link)
    {
        var client = new SftpClient(SshConnectionInfoFactory.Create(link))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };
        client.Connect();
        return client;
    }

    private static void DeleteDirectoryRecursive(
        SftpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        foreach (var file in client.ListDirectory(path).Where(file => file.Name is not "." and not ".."))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.IsDirectory && !file.IsSymbolicLink)
            {
                DeleteDirectoryRecursive(client, file.FullName, cancellationToken);
            }
            else
            {
                client.DeleteFile(file.FullName);
            }
        }

        client.DeleteDirectory(path);
    }

    private static RemoteFileItem ToRemoteFileItem(ISftpFile file)
    {
        return new RemoteFileItem
        {
            Name = file.Name,
            Path = file.FullName,
            Type = file.IsDirectory
                ? RemoteFileType.Directory
                : file.IsSymbolicLink
                    ? RemoteFileType.Symlink
                    : file.IsRegularFile
                        ? RemoteFileType.File
                        : RemoteFileType.Unknown,
            Size = file.Length,
            ModifiedAt = file.LastWriteTimeUtc,
            Permissions = FormatPermissions(file),
            Owner = file.Attributes.UserId.ToString(),
            Group = file.Attributes.GroupId.ToString()
        };
    }

    private static string FormatPermissions(ISftpFile file)
    {
        var attributes = file.Attributes;
        var typePrefix = file.IsDirectory ? 'd' : file.IsSymbolicLink ? 'l' : '-';

        return string.Concat(
            typePrefix,
            attributes.OwnerCanRead ? "r" : "-",
            attributes.OwnerCanWrite ? "w" : "-",
            attributes.OwnerCanExecute ? "x" : "-",
            attributes.GroupCanRead ? "r" : "-",
            attributes.GroupCanWrite ? "w" : "-",
            attributes.GroupCanExecute ? "x" : "-",
            attributes.OthersCanRead ? "r" : "-",
            attributes.OthersCanWrite ? "w" : "-",
            attributes.OthersCanExecute ? "x" : "-");
    }
}