using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using IlliciumTTY.Models;
using IlliciumTTY.ViewModels;

namespace IlliciumTTY.Controls;

internal sealed class ConnectionNavigationDragController(
    Control owner,
    Border dragPreview,
    ConnectionTreeSection favoriteSection,
    ConnectionTreeSection temporarySection,
    Func<MainWindowViewModel?> getViewModel)
{
    private const double FolderEdgeDropZoneRatio = 0.28;
    private const double MinimumFolderEdgeDropZone = 8;
    private const double MaximumFolderEdgeDropZone = 18;
    private static readonly TimeSpan DragStartDelay = TimeSpan.FromMilliseconds(350);

    private ConnectionNodeViewModel? _draggedNavLink;
    private CancellationTokenSource? _dragPressCts;
    private ConnectionNodeViewModel? _dropCueNode;

    public async Task StartDragAsync(ConnectionNodeViewModel node, Control source, PointerPressedEventArgs e)
    {
        if (node is not { IsSshLink: true } ||
            !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
        {
            return;
        }

        CancelPendingDrag();
        var delayCts = new CancellationTokenSource();
        _dragPressCts = delayCts;
        e.Pointer.Capture(source);

        try
        {
            await Task.Delay(DragStartDelay, delayCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_dragPressCts, delayCts))
            {
                _dragPressCts = null;
            }

            delayCts.Dispose();
        }

        if (!e.GetCurrentPoint(owner).Properties.IsLeftButtonPressed)
        {
            e.Pointer.Capture(null);
            return;
        }

        _draggedNavLink = node;
        node.IsDragging = true;
        ShowDragPreview(node, e.GetPosition(owner));

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(node.Id));

        try
        {
            e.Pointer.Capture(null);
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        finally
        {
            ClearDragState();
        }
    }

    public void DragOverNode(GroupType groupType, ConnectionNodeViewModel target, Control targetControl,
        DragEventArgs e)
    {
        UpdateDragPreview(e);
        var placement = GetDropPlacement(target, targetControl, e);

        if (CanDropNavLink(groupType, target, placement))
        {
            SetNodeDropCue(target, placement);
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            ClearDropCue();
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    public void DropNode(GroupType groupType, ConnectionNodeViewModel target, Control targetControl, DragEventArgs e)
    {
        DropNavLink(groupType, target, GetDropPlacement(target, targetControl, e), e);
    }

    public void DragOverSection(GroupType groupType, DragEventArgs e)
    {
        UpdateDragPreview(e);
        if (CanDropNavLink(groupType, null, ConnectionDropPlacement.Inside))
        {
            SetRootDropCue(groupType);
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            ClearDropCue();
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    public void DropSection(GroupType groupType, DragEventArgs e)
    {
        DropNavLink(groupType, null, ConnectionDropPlacement.Inside, e);
    }

    public void DragOverRoot(DragEventArgs e)
    {
        UpdateDragPreview(e);
        ClearDropCue();
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    public void DropRoot(DragEventArgs e)
    {
        ClearDragState();
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    public void ClearDragState()
    {
        CancelPendingDrag();
        ClearDropCue();
        if (_draggedNavLink is not null)
        {
            _draggedNavLink.IsDragging = false;
            _draggedNavLink = null;
        }

        dragPreview.IsVisible = false;
        dragPreview.DataContext = null;
    }

    public void CancelPendingDrag()
    {
        _dragPressCts?.Cancel();
        _dragPressCts = null;
    }

    private bool CanDropNavLink(
        GroupType destinationGroup,
        ConnectionNodeViewModel? target,
        ConnectionDropPlacement placement)
    {
        if (_draggedNavLink is not { IsSshLink: true } dragged ||
            ReferenceEquals(dragged, target) ||
            target is { IsSshLink: true } && placement == ConnectionDropPlacement.Inside)
        {
            return false;
        }

        return target is null || target.GroupType == destinationGroup;
    }

    private void DropNavLink(
        GroupType destinationGroup,
        ConnectionNodeViewModel? target,
        ConnectionDropPlacement placement,
        DragEventArgs e)
    {
        if (getViewModel() is { } viewModel &&
            CanDropNavLink(destinationGroup, target, placement) &&
            viewModel.MoveConnectionLink(_draggedNavLink, destinationGroup, target, placement))
        {
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        ClearDragState();
        e.Handled = true;
    }

    private void ShowDragPreview(ConnectionNodeViewModel node, Point position)
    {
        dragPreview.DataContext = node;
        dragPreview.IsVisible = true;
        SetDragPreviewPosition(position);
    }

    private void UpdateDragPreview(DragEventArgs e)
    {
        if (_draggedNavLink is null)
        {
            return;
        }

        if (!dragPreview.IsVisible)
        {
            dragPreview.DataContext = _draggedNavLink;
            dragPreview.IsVisible = true;
        }

        SetDragPreviewPosition(e.GetPosition(owner));
    }

    private void SetDragPreviewPosition(Point position)
    {
        Canvas.SetLeft(dragPreview, position.X + 14);
        Canvas.SetTop(dragPreview, position.Y + 14);
    }

    private static ConnectionDropPlacement GetDropPlacement(
        ConnectionNodeViewModel target,
        Control targetControl,
        DragEventArgs e)
    {
        var height = Math.Max(1, targetControl.Bounds.Height);
        var pointerY = Math.Clamp(e.GetPosition(targetControl).Y, 0, height);

        if (!target.IsFolder)
        {
            return pointerY < height / 2
                ? ConnectionDropPlacement.Before
                : ConnectionDropPlacement.After;
        }

        var edgeZone = Math.Clamp(
            height * FolderEdgeDropZoneRatio,
            MinimumFolderEdgeDropZone,
            MaximumFolderEdgeDropZone);

        if (pointerY <= edgeZone)
        {
            return ConnectionDropPlacement.Before;
        }

        return pointerY >= height - edgeZone
            ? ConnectionDropPlacement.After
            : ConnectionDropPlacement.Inside;
    }

    private void SetNodeDropCue(ConnectionNodeViewModel target, ConnectionDropPlacement placement)
    {
        ClearDropCue();
        _dropCueNode = target;

        switch (placement)
        {
            case ConnectionDropPlacement.Before:
                target.IsDropBefore = true;
                break;
            case ConnectionDropPlacement.Inside:
                target.IsDropInside = true;
                break;
            case ConnectionDropPlacement.After:
                target.IsDropAfter = true;
                break;
        }
    }

    private void SetRootDropCue(GroupType groupType)
    {
        ClearDropCue();
        favoriteSection.IsRootDropCueVisible = groupType == GroupType.Favorite;
        temporarySection.IsRootDropCueVisible = groupType == GroupType.Temporary;
    }

    private void ClearDropCue()
    {
        if (_dropCueNode is not null)
        {
            _dropCueNode.IsDropBefore = false;
            _dropCueNode.IsDropInside = false;
            _dropCueNode.IsDropAfter = false;
            _dropCueNode = null;
        }

        favoriteSection.IsRootDropCueVisible = false;
        temporarySection.IsRootDropCueVisible = false;
    }
}