using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using IlliciumTTY.Models;

namespace IlliciumTTY.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand]
    private void NewFavoriteLink() => OpenEditor(null, NodeType.SshLink, GroupType.Favorite);

    [RelayCommand]
    private void NewTemporaryLink() => OpenEditor(null, NodeType.SshLink, GroupType.Temporary);

    [RelayCommand]
    private void NewFavoriteFolder() => OpenEditor(null, NodeType.Folder, GroupType.Favorite);

    [RelayCommand]
    private void NewTemporaryFolder() => OpenEditor(null, NodeType.Folder, GroupType.Temporary);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditSelected()
    {
        EditNode(SelectedNavNode);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        DeleteNode(SelectedNavNode);
    }

    public void EditNode(ConnectionNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        OpenEditor(node, node.Type, node.GroupType);
    }

    public void DeleteNode(ConnectionNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        if (!ReferenceEquals(_pendingDeleteNode, node))
        {
            _pendingDeleteNode = node;
            ShowToast($"再次点击删除将移除：{node.Name}");
            return;
        }

        var removed = node;
        if (!RemoveNode(removed))
        {
            return;
        }

        DisconnectRemovedSessions(removed);

        if (SelectedNavNode is not null && ContainsNodeOrSelf(removed, SelectedNavNode.Id))
        {
            SelectedNavNode = FavoriteNodes.FirstOrDefault()
                              ?? TemporaryNodes.FirstOrDefault();
        }

        _pendingDeleteNode = null;
        ShowToast($"已删除：{removed.Name}");
        SaveConfig();
    }

    public bool MoveConnectionLink(
        ConnectionNodeViewModel? link,
        GroupType destinationGroup,
        ConnectionNodeViewModel? target,
        ConnectionDropPlacement placement)
    {
        if (link is not { IsSshLink: true } ||
            ReferenceEquals(link, target) ||
            target is not null && target.GroupType != destinationGroup ||
            target is { IsSshLink: true } && placement == ConnectionDropPlacement.Inside)
        {
            return false;
        }

        var sourceOwner = FindNodeOwner(link);
        if (sourceOwner is null)
        {
            return false;
        }

        var destinationOwner = ResolveDropTarget(destinationGroup, target, placement);
        if (destinationOwner is null)
        {
            return false;
        }

        sourceOwner.Value.Collection.Remove(link);
        sourceOwner.Value.Parent?.RefreshDisplay();

        var insertIndex = destinationOwner.Value.Collection.Count;
        if (target is not null && placement != ConnectionDropPlacement.Inside)
        {
            insertIndex = destinationOwner.Value.Collection.IndexOf(target);
            if (insertIndex < 0)
            {
                insertIndex = destinationOwner.Value.Collection.Count;
            }
            else if (placement == ConnectionDropPlacement.After)
            {
                insertIndex++;
            }
        }

        link.ParentId = destinationOwner.Value.Parent?.Id;
        SetGroupRecursive(link, destinationGroup);
        destinationOwner.Value.Collection.Insert(insertIndex, link);
        destinationOwner.Value.Parent?.RefreshDisplay();
        if (destinationOwner.Value.Parent is not null)
        {
            destinationOwner.Value.Parent.IsExpanded = true;
        }

        RefreshSortOrders(sourceOwner.Value.Collection);
        if (!ReferenceEquals(sourceOwner.Value.Collection, destinationOwner.Value.Collection))
        {
            RefreshSortOrders(destinationOwner.Value.Collection);
        }

        SelectWithoutActivation(link);
        ShowToast($"已移动：{link.Name}");
        SaveConfig();
        return true;
    }

    private void InsertNode(ConnectionNodeViewModel node, GroupType groupType, ConnectionNodeViewModel? parent)
    {
        SetGroupRecursive(node, groupType);
        node.ParentId = parent?.Id;

        var target = parent?.Children ?? (groupType == GroupType.Favorite ? FavoriteNodes : TemporaryNodes);
        if (!target.Contains(node))
        {
            target.Add(node);
        }

        for (var i = 0; i < target.Count; i++)
        {
            target[i].SortOrder = i;
        }

        if (parent is not null)
        {
            parent.IsExpanded = true;
            parent.RefreshDisplay();
        }
    }

    private void SelectWithoutActivation(ConnectionNodeViewModel node)
    {
        if (ReferenceEquals(SelectedNavNode, node))
        {
            return;
        }

        _suppressSelectionActivation = true;
        try
        {
            SelectedNavNode = node;
        }
        finally
        {
            _suppressSelectionActivation = false;
        }
    }

    private void DisconnectRemovedSessions(ConnectionNodeViewModel removed)
    {
        foreach (var linkId in EnumerateNodes(new[] { removed })
                     .Where(node => node.IsSshLink)
                     .Select(node => node.Id)
                     .ToList())
        {
            if (!_workspacesByLinkId.Remove(linkId, out var workspace))
            {
                continue;
            }

            workspace.FileBrowser.NavigatedFromFileManager -= OnFileBrowserNavigatedFromFileManager;
            workspace.FileBrowser.OperationMessage -= OnFileBrowserOperationMessage;
            workspace.FileBrowser.Dispose();
            var session = workspace.TerminalSession;
            session.BeginDisconnect();
            MultiSessions.Remove(session);
            if (ReferenceEquals(ActiveSession, session))
            {
                ActiveSession = null;
                ActiveFileBrowser = null;
            }
        }

        RefreshMultiPreviewSessions();
        OnPropertyChanged(nameof(IsMultiModeEmpty));
    }

    private static void SetGroupRecursive(ConnectionNodeViewModel node, GroupType groupType)
    {
        node.GroupType = groupType;
        foreach (var child in node.Children)
        {
            SetGroupRecursive(child, groupType);
        }
    }

    private bool RemoveNode(ConnectionNodeViewModel node)
    {
        if (FavoriteNodes.Remove(node) || TemporaryNodes.Remove(node))
        {
            return true;
        }

        return RemoveNodeFromChildren(FavoriteNodes, node) || RemoveNodeFromChildren(TemporaryNodes, node);
    }

    private static bool RemoveNodeFromChildren(IEnumerable<ConnectionNodeViewModel> nodes,
        ConnectionNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Remove(target))
            {
                node.RefreshDisplay();
                return true;
            }

            if (RemoveNodeFromChildren(node.Children, target))
            {
                return true;
            }
        }

        return false;
    }

    private (System.Collections.ObjectModel.ObservableCollection<ConnectionNodeViewModel> Collection,
        ConnectionNodeViewModel? Parent)?
        ResolveDropTarget(
            GroupType groupType,
            ConnectionNodeViewModel? target,
            ConnectionDropPlacement placement)
    {
        if (target is null)
        {
            return (groupType == GroupType.Favorite ? FavoriteNodes : TemporaryNodes, null);
        }

        if (target.IsFolder && placement == ConnectionDropPlacement.Inside)
        {
            return (target.Children, target);
        }

        return FindNodeOwner(target);
    }

    private (System.Collections.ObjectModel.ObservableCollection<ConnectionNodeViewModel> Collection,
        ConnectionNodeViewModel? Parent)? FindNodeOwner(
            ConnectionNodeViewModel node)
    {
        if (FavoriteNodes.Contains(node))
        {
            return (FavoriteNodes, null);
        }

        if (TemporaryNodes.Contains(node))
        {
            return (TemporaryNodes, null);
        }

        return FindNodeOwnerInChildren(FavoriteNodes, node)
               ?? FindNodeOwnerInChildren(TemporaryNodes, node);
    }

    private static (ObservableCollection<ConnectionNodeViewModel> Collection, ConnectionNodeViewModel? Parent)?
        FindNodeOwnerInChildren(
            IEnumerable<ConnectionNodeViewModel> nodes,
            ConnectionNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Contains(target))
            {
                return (node.Children, node);
            }

            var owner = FindNodeOwnerInChildren(node.Children, target);
            if (owner is not null)
            {
                return owner;
            }
        }

        return null;
    }

    private static void RefreshSortOrders(ObservableCollection<ConnectionNodeViewModel> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            nodes[i].SortOrder = i;
        }
    }

    private ConnectionNodeViewModel? FindNode(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return EnumerateNodes(FavoriteNodes).Concat(EnumerateNodes(TemporaryNodes))
            .FirstOrDefault(node => node.Id == id);
    }

    private static IEnumerable<ConnectionNodeViewModel> EnumerateNodes(IEnumerable<ConnectionNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in EnumerateNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<ConnectionNodeViewModel> EnumerateLinks(IEnumerable<ConnectionNodeViewModel> nodes)
    {
        return EnumerateNodes(nodes).Where(node => node.IsSshLink);
    }

    private static IEnumerable<(ConnectionNodeViewModel Node, int Depth)> EnumerateFolders(
        IEnumerable<ConnectionNodeViewModel> nodes,
        int depth)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder)
            {
                continue;
            }

            yield return (node, depth);

            foreach (var child in EnumerateFolders(node.Children, depth + 1))
            {
                yield return child;
            }
        }
    }

    private static bool ContainsNode(IEnumerable<ConnectionNodeViewModel> nodes, string id)
    {
        return EnumerateNodes(nodes).Any(node => node.Id == id);
    }

    private static bool ContainsNodeOrSelf(ConnectionNodeViewModel root, string id)
    {
        return root.Id == id || ContainsNode(root.Children, id);
    }
}