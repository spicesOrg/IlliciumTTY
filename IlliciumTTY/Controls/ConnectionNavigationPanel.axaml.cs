using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using IlliciumTTY.ViewModels;

namespace IlliciumTTY.Controls;

public partial class ConnectionNavigationPanel : UserControl
{
    private readonly ConnectionNavigationDragController _dragController;

    public ConnectionNavigationPanel()
    {
        InitializeComponent();
        _dragController = new ConnectionNavigationDragController(
            this,
            NavDragPreview,
            FavoriteSection,
            TemporarySection,
            GetViewModel);

        FavoriteSection.NodePointerPressed += OnNavNodePointerPressed;
        FavoriteSection.NodePointerReleased += OnNavNodePointerReleased;
        FavoriteSection.NodePointerCaptureLost += OnNavNodePointerCaptureLost;
        FavoriteSection.NodeDragOver += OnNavNodeDragOver;
        FavoriteSection.NodeDrop += OnNavNodeDrop;
        FavoriteSection.SectionDragOver += OnSectionDragOver;
        FavoriteSection.SectionDrop += OnSectionDrop;

        TemporarySection.NodePointerPressed += OnNavNodePointerPressed;
        TemporarySection.NodePointerReleased += OnNavNodePointerReleased;
        TemporarySection.NodePointerCaptureLost += OnNavNodePointerCaptureLost;
        TemporarySection.NodeDragOver += OnNavNodeDragOver;
        TemporarySection.NodeDrop += OnNavNodeDrop;
        TemporarySection.SectionDragOver += OnSectionDragOver;
        TemporarySection.SectionDrop += OnSectionDrop;

        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private async void OnNavNodePointerPressed(object? sender, ConnectionTreeNodePointerEventArgs args)
    {
        if (args.PointerEventArgs is not PointerPressedEventArgs e)
        {
            return;
        }

        var pointer = e.GetCurrentPoint(args.Source);

        if (pointer.Properties.IsRightButtonPressed)
        {
            _dragController.CancelPendingDrag();
            e.Pointer.Capture(null);
            e.Handled = true;
            ConnectionNodeContextMenuFactory.Create(args.Node, GetViewModel).Open(args.Source);
            return;
        }

        await _dragController.StartDragAsync(args.Node, args.Source, e);
    }

    private void OnNavNodePointerReleased(object? sender, ConnectionTreeNodePointerEventArgs args)
    {
        _dragController.CancelPendingDrag();
        args.PointerEventArgs.Pointer.Capture(null);
    }

    private void OnNavNodePointerCaptureLost(object? sender, ConnectionTreeNodePointerCaptureLostEventArgs args)
    {
        _dragController.CancelPendingDrag();
    }

    private void OnNavNodeDragOver(object? sender, ConnectionTreeNodeDragEventArgs args)
    {
        _dragController.DragOverNode(args.GroupType, args.Node, args.Source, args.DragEventArgs);
    }

    private void OnNavNodeDrop(object? sender, ConnectionTreeNodeDragEventArgs args)
    {
        _dragController.DropNode(args.GroupType, args.Node, args.Source, args.DragEventArgs);
    }

    private void OnSectionDragOver(object? sender, ConnectionTreeSectionDragEventArgs args)
    {
        _dragController.DragOverSection(args.GroupType, args.DragEventArgs);
    }

    private void OnSectionDrop(object? sender, ConnectionTreeSectionDragEventArgs args)
    {
        _dragController.DropSection(args.GroupType, args.DragEventArgs);
    }

    private void OnRootDragOver(object? sender, DragEventArgs e)
    {
        _dragController.DragOverRoot(e);
    }

    private void OnRootDrop(object? sender, DragEventArgs e)
    {
        _dragController.DropRoot(e);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachedFromVisualTree -= OnDetachedFromVisualTree;

        FavoriteSection.NodePointerPressed -= OnNavNodePointerPressed;
        FavoriteSection.NodePointerReleased -= OnNavNodePointerReleased;
        FavoriteSection.NodePointerCaptureLost -= OnNavNodePointerCaptureLost;
        FavoriteSection.NodeDragOver -= OnNavNodeDragOver;
        FavoriteSection.NodeDrop -= OnNavNodeDrop;
        FavoriteSection.SectionDragOver -= OnSectionDragOver;
        FavoriteSection.SectionDrop -= OnSectionDrop;

        TemporarySection.NodePointerPressed -= OnNavNodePointerPressed;
        TemporarySection.NodePointerReleased -= OnNavNodePointerReleased;
        TemporarySection.NodePointerCaptureLost -= OnNavNodePointerCaptureLost;
        TemporarySection.NodeDragOver -= OnNavNodeDragOver;
        TemporarySection.NodeDrop -= OnNavNodeDrop;
        TemporarySection.SectionDragOver -= OnSectionDragOver;
        TemporarySection.SectionDrop -= OnSectionDrop;

        _dragController.ClearDragState();
    }

    private MainWindowViewModel? GetViewModel() => DataContext as MainWindowViewModel;
}