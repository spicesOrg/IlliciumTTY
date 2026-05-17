using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IlliciumTTY.Models;
using IlliciumTTY.ViewModels;

namespace IlliciumTTY.Controls;

public partial class SingleModePanel : UserControl
{
    public SingleModePanel()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private RemoteFileBrowserViewModel? FileBrowser => ViewModel?.ActiveFileBrowser;

    private async void OnRemoteFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!TryGetRemoteFileItem(e.Source, out var item) || FileBrowser is not { } fileBrowser)
        {
            return;
        }

        e.Handled = true;
        await fileBrowser.OpenItemAsync(item);
    }

    private void OnRemoteFileListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || sender is not Control source || FileBrowser is not { } fileBrowser)
        {
            return;
        }

        e.Handled = true;
        if (TryGetRemoteFileItem(e.Source, out var item))
        {
            BuildItemContextMenu(fileBrowser, item).Open(source);
            return;
        }

        BuildDirectoryContextMenu(fileBrowser).Open(source);
    }

    private static bool TryGetRemoteFileItem(object? source, out RemoteFileItemViewModel item)
    {
        for (var current = source as Control; current is not null; current = current.Parent as Control)
        {
            if (current.DataContext is RemoteFileItemViewModel currentItem)
            {
                item = currentItem;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private ContextMenu BuildItemContextMenu(RemoteFileBrowserViewModel fileBrowser, RemoteFileItemViewModel item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(item.Type == RemoteFileType.Directory ? "进入" : "打开",
            () => fileBrowser.OpenItemAsync(item), item.CanOpen));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("删除", () => DeleteItemAsync(fileBrowser, item)));
        menu.Items.Add(CreateMenuItem("重命名", () => RenameItemAsync(fileBrowser, item)));
        menu.Items.Add(CreateMenuItem("权限管理", () => ChangePermissionsAsync(fileBrowser, item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("新建文件", () => CreateFileAsync(fileBrowser)));
        menu.Items.Add(CreateMenuItem("新建文件夹", () => CreateDirectoryAsync(fileBrowser)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("刷新", () => fileBrowser.RefreshFileManagerCommand.ExecuteAsync(null)));
        return menu;
    }

    private ContextMenu BuildDirectoryContextMenu(RemoteFileBrowserViewModel fileBrowser)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("新建文件", () => CreateFileAsync(fileBrowser)));
        menu.Items.Add(CreateMenuItem("新建文件夹", () => CreateDirectoryAsync(fileBrowser)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("刷新", () => fileBrowser.RefreshFileManagerCommand.ExecuteAsync(null)));
        return menu;
    }

    private async Task CreateFileAsync(RemoteFileBrowserViewModel fileBrowser)
    {
        var name = await RemoteFileOperationDialogs.ShowTextInputAsync(
            this,
            "新建文件",
            "文件名",
            "untitled.txt",
            "untitled.txt");
        if (name is null)
        {
            return;
        }

        await fileBrowser.CreateFileAsync(name);
    }

    private async Task CreateDirectoryAsync(RemoteFileBrowserViewModel fileBrowser)
    {
        var name = await RemoteFileOperationDialogs.ShowTextInputAsync(
            this,
            "新建文件夹",
            "文件夹名",
            "new-folder",
            "new-folder");
        if (name is null)
        {
            return;
        }

        await fileBrowser.CreateDirectoryAsync(name);
    }

    private async Task RenameItemAsync(RemoteFileBrowserViewModel fileBrowser, RemoteFileItemViewModel item)
    {
        var name = await RemoteFileOperationDialogs.ShowTextInputAsync(
            this,
            "重命名",
            "新名称",
            item.Name,
            item.Name);
        if (name is null)
        {
            return;
        }

        await fileBrowser.RenameAsync(item, name);
    }

    private async Task DeleteItemAsync(RemoteFileBrowserViewModel fileBrowser, RemoteFileItemViewModel item)
    {
        var confirmed = await RemoteFileOperationDialogs.ShowConfirmAsync(
            this,
            "删除",
            $"确定删除 {item.Name}？",
            "删除");
        if (!confirmed)
        {
            return;
        }

        await fileBrowser.DeleteAsync(item);
    }

    private async Task ChangePermissionsAsync(RemoteFileBrowserViewModel fileBrowser, RemoteFileItemViewModel item)
    {
        var mode = await RemoteFileOperationDialogs.ShowTextInputAsync(
            this,
            "权限管理",
            $"权限模式（当前 {item.Permissions}）",
            string.IsNullOrWhiteSpace(item.PermissionMode) ? "644" : item.PermissionMode,
            "例如 644 或 755");
        if (mode is null)
        {
            return;
        }

        await fileBrowser.ChangePermissionsAsync(item, mode);
    }

    private static MenuItem CreateMenuItem(string header, Func<Task> action, bool isEnabled = true)
    {
        var menuItem = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };
        menuItem.Click += async (_, _) => await action();
        return menuItem;
    }
}

internal static class RemoteFileOperationDialogs
{
    public static Task<string?> ShowTextInputAsync(
        Control ownerControl,
        string title,
        string label,
        string initialValue,
        string placeholder)
    {
        var owner = TopLevel.GetTopLevel(ownerControl) as Window;
        if (owner is null)
        {
            return Task.FromResult<string?>(null);
        }

        var textBox = new TextBox
        {
            Text = initialValue,
            PlaceholderText = placeholder,
            MinWidth = 320,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var dialog = CreateDialog(title);
        var confirmButton = CreatePrimaryButton("确定");
        var cancelButton = CreateSecondaryButton("取消");

        confirmButton.Click += (_, _) =>
            dialog.Close(string.IsNullOrWhiteSpace(textBox.Text) ? "" : textBox.Text.Trim());
        cancelButton.Click += (_, _) => dialog.Close(null);
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                dialog.Close(string.IsNullOrWhiteSpace(textBox.Text) ? "" : textBox.Text.Trim());
            }
        };

        dialog.Content = CreateDialogContent(
            new TextBlock
            {
                Text = label,
                Foreground = Brushes.DimGray
            },
            textBox,
            cancelButton,
            confirmButton);
        dialog.Opened += (_, _) => textBox.Focus();
        return dialog.ShowDialog<string?>(owner);
    }

    public static Task<bool> ShowConfirmAsync(
        Control ownerControl,
        string title,
        string message,
        string confirmText)
    {
        var owner = TopLevel.GetTopLevel(ownerControl) as Window;
        if (owner is null)
        {
            return Task.FromResult(false);
        }

        var dialog = CreateDialog(title);
        var confirmButton = CreateDangerButton(confirmText);
        var cancelButton = CreateSecondaryButton("取消");

        confirmButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = CreateDialogContent(
            new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                MaxWidth = 360
            },
            null,
            cancelButton,
            confirmButton);
        return dialog.ShowDialog<bool>(owner);
    }

    private static Window CreateDialog(string title)
    {
        return new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
    }

    private static Control CreateDialogContent(
        Control body,
        Control? secondaryBody,
        Button cancelButton,
        Button confirmButton)
    {
        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14
        };
        content.Children.Add(body);
        if (secondaryBody is not null)
        {
            content.Children.Add(secondaryBody);
        }

        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                cancelButton,
                confirmButton
            }
        });

        return content;
    }

    private static Button CreatePrimaryButton(string text)
    {
        return CreateDialogButton(text, Color.Parse("#3157A8"), Brushes.White, Color.Parse("#3157A8"));
    }

    private static Button CreateDangerButton(string text)
    {
        return CreateDialogButton(text, Color.Parse("#BE123C"), Brushes.White, Color.Parse("#BE123C"));
    }

    private static Button CreateSecondaryButton(string text)
    {
        return CreateDialogButton(text, Colors.Transparent, new SolidColorBrush(Color.Parse("#334155")),
            Color.Parse("#CBD5E1"));
    }

    private static Button CreateDialogButton(string text, Color background, IBrush foreground, Color border)
    {
        return new Button
        {
            Content = text,
            MinWidth = 74,
            Padding = new Thickness(14, 7),
            Background = new SolidColorBrush(background),
            Foreground = foreground,
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }
}