using System;
using System.Text;
using IlliciumTTY.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace IlliciumTTY.Services;

public static class SshConnectionInfoFactory
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static ConnectionInfo Create(ConnectionNodeModel link, TimeSpan? timeout = null)
    {
        ValidateEndpoint(link);

        var connectionInfo = link.AuthType switch
        {
            AuthType.Password => CreatePasswordConnectionInfo(link),
            AuthType.PrivateKey => CreatePrivateKeyConnectionInfo(link),
            AuthType.Agent => throw new NotSupportedException("SSH.NET 不支持使用本机 SSH Agent。请改用密码或私钥认证。"),
            _ => throw new SshAuthenticationException("认证方式无效。")
        };

        connectionInfo.Timeout = timeout ?? DefaultTimeout;
        connectionInfo.RetryAttempts = 1;
        connectionInfo.Encoding = Encoding.UTF8;
        return connectionInfo;
    }

    private static void ValidateEndpoint(ConnectionNodeModel link)
    {
        if (string.IsNullOrWhiteSpace(link.Host))
        {
            throw new SshConnectionException("未提供主机地址。");
        }

        if (string.IsNullOrWhiteSpace(link.Username))
        {
            throw new SshAuthenticationException("未提供用户名。");
        }

        if (link.Port is <= 0 or > 65535)
        {
            throw new SshConnectionException("SSH 端口无效。");
        }
    }

    private static ConnectionInfo CreatePasswordConnectionInfo(ConnectionNodeModel link)
    {
        if (string.IsNullOrEmpty(link.TransientPassword))
        {
            throw new SshAuthenticationException("未提供密码。请在编辑连接中保存密码。");
        }

        var password = link.TransientPassword;
        var passwordMethod = new PasswordAuthenticationMethod(link.Username, password);
        var keyboardInteractiveMethod = new KeyboardInteractiveAuthenticationMethod(link.Username);
        keyboardInteractiveMethod.AuthenticationPrompt += (_, args) =>
        {
            foreach (var prompt in args.Prompts)
            {
                prompt.Response = password;
            }
        };

        return new ConnectionInfo(
            link.Host,
            link.Port,
            link.Username,
            passwordMethod,
            keyboardInteractiveMethod);
    }

    private static PrivateKeyConnectionInfo CreatePrivateKeyConnectionInfo(ConnectionNodeModel link)
    {
        var privateKeyPath = string.IsNullOrWhiteSpace(link.PrivateKeyPath)
            ? throw new SshAuthenticationException("未提供私钥路径。")
            : link.PrivateKeyPath;

        var keyFile = string.IsNullOrWhiteSpace(link.TransientPassphrase)
            ? new PrivateKeyFile(privateKeyPath)
            : new PrivateKeyFile(privateKeyPath, link.TransientPassphrase);

        return new PrivateKeyConnectionInfo(link.Host, link.Port, link.Username, keyFile);
    }
}