using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IlliciumTTY.Models;

namespace IlliciumTTY.ViewModels;

public sealed class RemoteFileItemViewModel : ViewModelBase
{
    public RemoteFileItemViewModel(RemoteFileItem item, Func<RemoteFileItemViewModel, Task> open)
    {
        Item = item;
        OpenCommand = new AsyncRelayCommand(() => open(this), () => CanOpen);
    }

    public RemoteFileItem Item { get; }
    public IAsyncRelayCommand OpenCommand { get; }

    public string Name => Item.Name;
    public string Path => Item.Path;
    public RemoteFileType Type => Item.Type;

    public string TypeLabel => Type switch
    {
        RemoteFileType.Directory => "文件夹",
        RemoteFileType.File => "文件",
        RemoteFileType.Symlink => "链接",
        _ => "未知"
    };

    public string SizeLabel => Type == RemoteFileType.Directory ? "" : FormatBytes(Item.Size);
    public string ModifiedAtLabel => Item.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Permissions => Item.Permissions;
    public string PermissionMode => FormatPermissionMode(Permissions);
    public bool CanOpen => Type is RemoteFileType.Directory or RemoteFileType.File or RemoteFileType.Symlink;
    public string ActionLabel => Type == RemoteFileType.Directory ? "进入" : CanOpen ? "打开" : "";

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {units[unit]}" : $"{size:0.##} {units[unit]}";
    }

    private static string FormatPermissionMode(string permissions)
    {
        if (permissions.Length < 10)
        {
            return string.Empty;
        }

        return string.Concat(
            PermissionPartToDigit(permissions[1], permissions[2], permissions[3]),
            PermissionPartToDigit(permissions[4], permissions[5], permissions[6]),
            PermissionPartToDigit(permissions[7], permissions[8], permissions[9]));
    }

    private static int PermissionPartToDigit(char read, char write, char execute)
    {
        var value = 0;
        if (read == 'r')
        {
            value += 4;
        }

        if (write == 'w')
        {
            value += 2;
        }

        if (execute is 'x' or 's' or 't')
        {
            value += 1;
        }

        return value;
    }
}