using System;
using Avalonia.Controls;
using Avalonia.Input;
using IlliciumTTY.Models;
using IlliciumTTY.ViewModels;

namespace IlliciumTTY.Controls;

public sealed class ConnectionTreeNodePointerEventArgs(
    ConnectionNodeViewModel node,
    Control source,
    PointerEventArgs pointerEventArgs)
    : EventArgs
{
    public ConnectionNodeViewModel Node { get; } = node;
    public Control Source { get; } = source;
    public PointerEventArgs PointerEventArgs { get; } = pointerEventArgs;
}

public sealed class ConnectionTreeNodePointerCaptureLostEventArgs(
    ConnectionNodeViewModel node,
    Control source,
    PointerCaptureLostEventArgs pointerEventArgs)
    : EventArgs
{
    public ConnectionNodeViewModel Node { get; } = node;
    public Control Source { get; } = source;
    public PointerCaptureLostEventArgs PointerEventArgs { get; } = pointerEventArgs;
}

public sealed class ConnectionTreeNodeDragEventArgs(
    GroupType groupType,
    ConnectionNodeViewModel node,
    Control source,
    DragEventArgs dragEventArgs)
    : EventArgs
{
    public GroupType GroupType { get; } = groupType;
    public ConnectionNodeViewModel Node { get; } = node;
    public Control Source { get; } = source;
    public DragEventArgs DragEventArgs { get; } = dragEventArgs;
}

public sealed class ConnectionTreeSectionDragEventArgs(
    GroupType groupType,
    DragEventArgs dragEventArgs)
    : EventArgs
{
    public GroupType GroupType { get; } = groupType;
    public DragEventArgs DragEventArgs { get; } = dragEventArgs;
}