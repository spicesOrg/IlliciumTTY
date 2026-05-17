using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using IlliciumTTY.Models;

namespace IlliciumTTY.ViewModels;

public partial class MainWindowViewModel
{
    private void LoadNodes(IEnumerable<ConnectionNodeModel> source,
        ObservableCollection<ConnectionNodeViewModel> target)
    {
        target.Clear();
        foreach (var node in source.OrderBy(node => node.SortOrder))
        {
            target.Add(new ConnectionNodeViewModel(node));
        }
    }

    private void ApplyExpandedState(IEnumerable<ConnectionNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = _config.ExpandedNodeIds.Contains(node.Id);
            ApplyExpandedState(node.Children);
        }
    }

    private void SaveConfig()
    {
        var persistentLastOpenedNodeId = SelectedNavNode?.GroupType == GroupType.Favorite
            ? SelectedNavNode.Id
            : null;

        var snapshot = new AppConfig
        {
            FavoriteNodes = FavoriteNodes
                .Select((node, index) => node.ToModel(null, GroupType.Favorite, index))
                .ToList(),
            TemporaryNodes = [],
            DefaultSyncMode = SyncMode,
            LastOpenedNodeId = persistentLastOpenedNodeId,
            ExpandedNodeIds = EnumerateNodes(FavoriteNodes)
                .Where(node => node.IsExpanded)
                .Select(node => node.Id)
                .ToHashSet(),
            SidebarWidth = _config.SidebarWidth,
            WorkspaceSplitRatio = _config.WorkspaceSplitRatio
        };

        _config.FavoriteNodes = snapshot.FavoriteNodes;
        _config.TemporaryNodes = snapshot.TemporaryNodes;
        _config.DefaultSyncMode = snapshot.DefaultSyncMode;
        _config.LastOpenedNodeId = snapshot.LastOpenedNodeId;
        _config.ExpandedNodeIds = snapshot.ExpandedNodeIds;

        _ = SaveConfigAsync(snapshot);
    }

    private async Task SaveConfigAsync(AppConfig snapshot)
    {
        try
        {
            await _configRepository.SaveAsync(snapshot);
        }
        catch (Exception ex)
        {
            if (_isShuttingDown)
            {
                return;
            }

            ShowToast($"保存配置失败：{ex.Message}");
        }
    }
}