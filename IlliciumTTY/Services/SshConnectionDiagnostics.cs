using System;
using System.IO;
using System.Net.Sockets;
using Renci.SshNet.Common;

namespace IlliciumTTY.Services;

public static class SshConnectionDiagnostics
{
    public static string ExplainException(Exception ex)
    {
        return ex switch
        {
            SshAuthenticationException sshAuthenticationException =>
                ExplainException("连接失败：认证失败。", sshAuthenticationException),
            FileNotFoundException fileNotFoundException =>
                $"连接失败：私钥文件不存在。{CleanLine(fileNotFoundException.Message)}",
            SshOperationTimeoutException =>
                "连接失败：连接超时。请检查主机地址、端口和网络连通性。",
            TimeoutException =>
                "连接失败：连接超时。请检查主机地址、端口和网络连通性。",
            SocketException socketException =>
                ExplainSocketException(socketException),
            SshConnectionException sshConnectionException =>
                ExplainException("连接失败。", sshConnectionException),
            SshException sshException =>
                ExplainException("连接失败。", sshException),
            _ when FindInnerException<SocketException>(ex) is { } socketException =>
                ExplainSocketException(socketException),
            _ => ExplainException("连接失败。", ex)
        };
    }

    private static string ExplainException(string prefix, Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message) ? "未知原因。" : CleanLine(ex.Message);
        return $"{prefix} {message}";
    }

    private static string ExplainSocketException(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData =>
                $"连接失败：主机名解析失败。{CleanLine(ex.Message)}",
            SocketError.ConnectionRefused =>
                $"连接失败：目标端口拒绝连接。请确认 SSH 服务已启动且端口正确。{CleanLine(ex.Message)}",
            SocketError.TimedOut =>
                $"连接失败：连接超时。请检查主机地址、端口和网络连通性。{CleanLine(ex.Message)}",
            SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                $"连接失败：主机不可达。请检查网络、VPN、防火墙或路由。{CleanLine(ex.Message)}",
            _ =>
                $"连接失败：网络错误。{CleanLine(ex.Message)}"
        };
    }

    private static TException? FindInnerException<TException>(Exception ex)
        where TException : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is TException matched)
            {
                return matched;
            }
        }

        return null;
    }

    private static string CleanLine(string value)
    {
        var compact = string.Join(' ', value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return compact.Length <= 180 ? compact : compact[..180] + "...";
    }
}