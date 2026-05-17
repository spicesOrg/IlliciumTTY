using System;
using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using IlliciumTTY.Models;
using IlliciumTTY.ViewModels;

namespace IlliciumTTY.Controls;

public partial class ConnectionTreeSection : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, string>(nameof(Title));

    public static readonly StyledProperty<GroupType> GroupTypeProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, GroupType>(nameof(GroupType));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, object?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> NewLinkCommandProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, ICommand?>(nameof(NewLinkCommand));

    public static readonly StyledProperty<ICommand?> NewFolderCommandProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, ICommand?>(nameof(NewFolderCommand));

    public static readonly StyledProperty<string> NewLinkToolTipProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, string>(nameof(NewLinkToolTip));

    public static readonly StyledProperty<string> NewFolderToolTipProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, string>(nameof(NewFolderToolTip));

    public static readonly StyledProperty<bool> IsRootDropCueVisibleProperty =
        AvaloniaProperty.Register<ConnectionTreeSection, bool>(nameof(IsRootDropCueVisible));

    public ConnectionTreeSection()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public GroupType GroupType
    {
        get => GetValue(GroupTypeProperty);
        set => SetValue(GroupTypeProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public ICommand? NewLinkCommand
    {
        get => GetValue(NewLinkCommandProperty);
        set => SetValue(NewLinkCommandProperty, value);
    }

    public ICommand? NewFolderCommand
    {
        get => GetValue(NewFolderCommandProperty);
        set => SetValue(NewFolderCommandProperty, value);
    }

    public string NewLinkToolTip
    {
        get => GetValue(NewLinkToolTipProperty);
        set => SetValue(NewLinkToolTipProperty, value);
    }

    public string NewFolderToolTip
    {
        get => GetValue(NewFolderToolTipProperty);
        set => SetValue(NewFolderToolTipProperty, value);
    }

    public bool IsRootDropCueVisible
    {
        get => GetValue(IsRootDropCueVisibleProperty);
        set => SetValue(IsRootDropCueVisibleProperty, value);
    }

    public event EventHandler<ConnectionTreeNodePointerEventArgs>? NodePointerPressed;
    public event EventHandler<ConnectionTreeNodePointerEventArgs>? NodePointerReleased;
    public event EventHandler<ConnectionTreeNodePointerCaptureLostEventArgs>? NodePointerCaptureLost;
    public event EventHandler<ConnectionTreeNodeDragEventArgs>? NodeDragOver;
    public event EventHandler<ConnectionTreeNodeDragEventArgs>? NodeDrop;
    public event EventHandler<ConnectionTreeSectionDragEventArgs>? SectionDragOver;
    public event EventHandler<ConnectionTreeSectionDragEventArgs>? SectionDrop;

    private void OnNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryGetNode(sender, out var control, out var node))
        {
            NodePointerPressed?.Invoke(this, new ConnectionTreeNodePointerEventArgs(node, control, e));
        }
    }

    private void OnNodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (TryGetNode(sender, out var control, out var node))
        {
            NodePointerReleased?.Invoke(this, new ConnectionTreeNodePointerEventArgs(node, control, e));
        }
    }

    private void OnNodePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (TryGetNode(sender, out var control, out var node))
        {
            NodePointerCaptureLost?.Invoke(this, new ConnectionTreeNodePointerCaptureLostEventArgs(node, control, e));
        }
    }

    private void OnNodeDragOver(object? sender, DragEventArgs e)
    {
        if (TryGetNode(sender, out var control, out var node))
        {
            NodeDragOver?.Invoke(this, new ConnectionTreeNodeDragEventArgs(GroupType, node, control, e));
        }
    }

    private void OnNodeDrop(object? sender, DragEventArgs e)
    {
        if (TryGetNode(sender, out var control, out var node))
        {
            NodeDrop?.Invoke(this, new ConnectionTreeNodeDragEventArgs(GroupType, node, control, e));
        }
    }

    private void OnSectionDragOver(object? sender, DragEventArgs e)
    {
        SectionDragOver?.Invoke(this, new ConnectionTreeSectionDragEventArgs(GroupType, e));
    }

    private void OnSectionDrop(object? sender, DragEventArgs e)
    {
        SectionDrop?.Invoke(this, new ConnectionTreeSectionDragEventArgs(GroupType, e));
    }

    private static bool TryGetNode(
        object? sender,
        out Control control,
        out ConnectionNodeViewModel node)
    {
        if (sender is Control { DataContext: ConnectionNodeViewModel connectionNode } senderControl)
        {
            control = senderControl;
            node = connectionNode;
            return true;
        }

        control = null!;
        node = null!;
        return false;
    }
}