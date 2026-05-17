using System;

namespace IlliciumTTY.Services;

public static class SshCommandBuilder
{
    public static string BuildCdCommand(string remotePath)
    {
        if (remotePath == "~" || remotePath.StartsWith("~/", StringComparison.Ordinal))
        {
            return $"cd {remotePath}";
        }

        return $"cd {QuoteForPosixShell(remotePath)}";
    }

    private static string QuoteForPosixShell(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }
}