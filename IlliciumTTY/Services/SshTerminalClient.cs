using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IlliciumTTY.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace IlliciumTTY.Services;

public readonly record struct SshTerminalSize(uint Columns, uint Rows, uint Width, uint Height);

public sealed class SshTerminalClient : IDisposable
{
    private const int ShellBufferSize = 4096;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly object _syncRoot = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private SshClient? _client;
    private bool _disposed;
    private ShellStream? _shellStream;

    public void Dispose()
    {
        ShellStream? shellStream;
        SshClient? client;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            shellStream = _shellStream;
            client = _client;
            _shellStream = null;
            _client = null;
        }

        if (shellStream is not null)
        {
            shellStream.DataReceived -= OnDataReceived;
            shellStream.ErrorOccurred -= OnErrorOccurred;
            shellStream.Closed -= OnClosed;
            try
            {
                shellStream.Dispose();
            }
            catch
            {
            }
        }

        if (client is null)
        {
            return;
        }

        try
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
        catch
        {
        }

        client.Dispose();
    }

    public event EventHandler<string>? DataReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? Closed;

    public async Task ConnectAsync(
        ConnectionNodeModel link,
        SshTerminalSize terminalSize,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var connectionInfo = SshConnectionInfoFactory.Create(link);
        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = KeepAliveInterval
        };
        ShellStream? shellStream = null;

        try
        {
            await client.ConnectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            shellStream = client.CreateShellStream(
                "xterm-256color",
                terminalSize.Columns,
                terminalSize.Rows,
                terminalSize.Width,
                terminalSize.Height,
                ShellBufferSize);

            shellStream.DataReceived += OnDataReceived;
            shellStream.ErrorOccurred += OnErrorOccurred;
            shellStream.Closed += OnClosed;

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                _client = client;
                _shellStream = shellStream;
            }
        }
        catch
        {
            if (shellStream is not null)
            {
                shellStream.DataReceived -= OnDataReceived;
                shellStream.ErrorOccurred -= OnErrorOccurred;
                shellStream.Closed -= OnClosed;
                try
                {
                    shellStream.Dispose();
                }
                catch
                {
                }
            }

            client.Dispose();
            throw;
        }
    }

    public void Write(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        try
        {
            lock (_syncRoot)
            {
                if (_disposed || _shellStream is not { CanWrite: true } shellStream)
                {
                    return;
                }

                shellStream.Write(data);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    public void ChangeWindowSize(SshTerminalSize terminalSize)
    {
        try
        {
            lock (_syncRoot)
            {
                if (_disposed || _shellStream is null)
                {
                    return;
                }

                _shellStream.ChangeWindowSize(
                    terminalSize.Columns,
                    terminalSize.Rows,
                    terminalSize.Width,
                    terminalSize.Height);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private void OnDataReceived(object? sender, ShellDataEventArgs e)
    {
        if (e.Data.Length == 0)
        {
            return;
        }

        string data;
        lock (_utf8Decoder)
        {
            var charCount = _utf8Decoder.GetCharCount(e.Data, 0, e.Data.Length);
            var chars = new char[charCount];
            _utf8Decoder.GetChars(e.Data, 0, e.Data.Length, chars, 0);
            data = new string(chars);
        }

        if (data.Length > 0)
        {
            DataReceived?.Invoke(this, data);
        }
    }

    private void OnErrorOccurred(object? sender, ExceptionEventArgs e)
    {
        ErrorOccurred?.Invoke(this, e.Exception);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}