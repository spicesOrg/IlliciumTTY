using System;
using Avalonia.Controls;
using IlliciumTTY.ViewModels;

namespace IlliciumTTY.Controls;

internal static class ConnectionNodeContextMenuFactory
{
    public static ContextMenu Create(ConnectionNodeViewModel node, Func<MainWindowViewModel?> getViewModel)
    {
        var editMenuItem = new MenuItem
        {
            Header = "编辑"
        };
        editMenuItem.Click += (_, _) => getViewModel()?.EditNode(node);

        var deleteMenuItem = new MenuItem
        {
            Header = "删除"
        };
        deleteMenuItem.Click += (_, _) => getViewModel()?.DeleteNode(node);

        var reconnectMenuItem = new MenuItem
        {
            Header = "（重新）链接",
            IsVisible = node.IsSshLink
        };
        reconnectMenuItem.Click += (_, _) => getViewModel()?.ReconnectNode(node);

        var contextMenu = new ContextMenu
        {
            DataContext = node
        };
        contextMenu.Items.Add(editMenuItem);
        contextMenu.Items.Add(deleteMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(reconnectMenuItem);

        return contextMenu;
    }
}