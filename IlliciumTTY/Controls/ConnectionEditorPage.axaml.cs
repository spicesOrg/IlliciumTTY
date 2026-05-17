using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using IlliciumTTY.ViewModels;

namespace IlliciumTTY.Controls;

public partial class ConnectionEditorPage : UserControl
{
    private static readonly FilePickerFileType PrivateKeyFileType = new("SSH 私钥文件")
    {
        Patterns = ["*.ppk", "*.pem", "*.key", "id_*", "*_rsa", "*_ed25519", "*_ecdsa", "*_dsa"],
        MimeTypes = ["application/octet-stream", "text/plain"],
        AppleUniformTypeIdentifiers = ["public.item"]
    };

    private static readonly FilePickerFileType AllFilesFileType = new("所有文件")
    {
        Patterns = ["*"],
        MimeTypes = ["application/octet-stream", "text/plain"],
        AppleUniformTypeIdentifiers = ["public.item"]
    };

    public ConnectionEditorPage()
    {
        InitializeComponent();
    }

    private async void OnPrivateKeyBrowseClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider.CanOpen != true)
        {
            viewModel.ShowToast("当前平台不支持文件选择器");
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 SSH 私钥文件",
            AllowMultiple = false,
            FileTypeFilter = [PrivateKeyFileType, AllFilesFileType]
        });

        if (files.Count == 0)
        {
            return;
        }

        var localPath = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            viewModel.EditorPrivateKeyPath = localPath;
        }
    }
}