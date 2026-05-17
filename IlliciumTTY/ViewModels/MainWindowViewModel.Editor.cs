using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using IlliciumTTY.Models;

namespace IlliciumTTY.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand]
    private void SaveEditor()
    {
        if (EditorGroupOption is null)
        {
            ShowToast("请选择所属区域");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditorName))
        {
            ShowToast("名称不能为空");
            return;
        }

        var groupType = EditorGroupOption.Value;
        var parent = EditorParentFolder?.Node;
        var node = _editingNode ?? CreateNodeFromEditor(groupType);

        if (!ApplyEditorToNode(node))
        {
            return;
        }

        RemoveNode(node);
        InsertNode(node, groupType, parent);
        node.IsExpanded = true;
        SelectedNavNode = node;
        IsEditorOpen = false;
        ShowToast(_editingNode is null ? "已创建连接项" : "已保存连接项");
        _editingNode = null;
        SaveConfig();
    }

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditorOpen = false;
        _editingNode = null;
    }

    private void OpenEditor(ConnectionNodeViewModel? node, NodeType nodeType, GroupType groupType)
    {
        _editingNode = node;
        IsEditorForLink = nodeType == NodeType.SshLink;
        EditorTitle = node is null
            ? (IsEditorForLink ? "新建 SSH 链接" : "新建文件夹")
            : (IsEditorForLink ? "编辑 SSH 链接" : "编辑文件夹");

        EditorName = node?.Name ?? "";
        EditorHost = node?.Host ?? "";
        EditorPortText = (node?.Port ?? 22).ToString();
        EditorUsername = node?.Username ?? Environment.UserName;
        EditorAuthOption = AuthOptions.First(option => option.Value == (node?.AuthType ?? AuthType.Password));
        EditorPassword = "";
        EditorPrivateKeyPath = node?.PrivateKeyPath ?? "";
        EditorPassphrase = "";
        EditorDefaultRemotePath = string.IsNullOrWhiteSpace(node?.DefaultRemotePath) ? "." : node!.DefaultRemotePath!;
        EditorGroupOption = GroupOptions.First(option => option.Value == groupType);
        RefreshFolderOptions();

        var preferredParentId = node?.ParentId;
        if (preferredParentId is null &&
            SelectedNavNode?.IsFolder == true &&
            SelectedNavNode.GroupType == groupType &&
            !ReferenceEquals(node, SelectedNavNode))
        {
            preferredParentId = SelectedNavNode.Id;
        }

        EditorParentFolder = FolderOptions.FirstOrDefault(option => option.Node?.Id == preferredParentId)
                             ?? FolderOptions.FirstOrDefault();
        IsEditorOpen = true;
    }

    private ConnectionNodeViewModel CreateNodeFromEditor(GroupType groupType)
    {
        var model = new ConnectionNodeModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = IsEditorForLink ? NodeType.SshLink : NodeType.Folder,
            GroupType = groupType,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return new ConnectionNodeViewModel(model);
    }

    private bool ApplyEditorToNode(ConnectionNodeViewModel node)
    {
        node.Name = EditorName.Trim();
        node.GroupType = EditorGroupOption?.Value ?? GroupType.Favorite;
        node.ParentId = EditorParentFolder?.Node?.Id;

        if (!IsEditorForLink)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(EditorHost))
        {
            ShowToast("主机地址不能为空");
            return false;
        }

        if (!int.TryParse(EditorPortText, out var port) || port <= 0 || port > 65535)
        {
            ShowToast("端口必须在 1 到 65535 之间");
            return false;
        }

        if (string.IsNullOrWhiteSpace(EditorUsername))
        {
            ShowToast("用户名不能为空");
            return false;
        }

        node.Host = EditorHost.Trim();
        node.Port = port;
        node.Username = EditorUsername.Trim();
        node.AuthType = EditorAuthOption?.Value ?? AuthType.Password;
        node.PrivateKeyPath = string.IsNullOrWhiteSpace(EditorPrivateKeyPath) ? null : EditorPrivateKeyPath.Trim();
        node.DefaultRemotePath =
            string.IsNullOrWhiteSpace(EditorDefaultRemotePath) ? "." : EditorDefaultRemotePath.Trim();

        if (node.AuthType == AuthType.Password)
        {
            if (!string.IsNullOrEmpty(EditorPassword))
            {
                node.TransientPassword = EditorPassword;
            }
        }
        else
        {
            node.PasswordRef = null;
            node.TransientPassword = null;
        }

        if (node.AuthType == AuthType.PrivateKey)
        {
            if (!string.IsNullOrEmpty(EditorPassphrase))
            {
                node.TransientPassphrase = EditorPassphrase;
            }
        }
        else
        {
            node.PassphraseRef = null;
            node.TransientPassphrase = null;
        }

        return true;
    }

    private void RefreshFolderOptions()
    {
        var selectedGroup = EditorGroupOption?.Value ?? GroupType.Favorite;
        var folders = selectedGroup == GroupType.Favorite
            ? FavoriteNodes
            : TemporaryNodes;

        FolderOptions.Clear();
        FolderOptions.Add(new FolderChoiceOption("根目录", null));

        foreach (var folder in EnumerateFolders(folders, 0))
        {
            if (_editingNode is not null &&
                (folder.Node.Id == _editingNode.Id || ContainsNode(_editingNode.Children, folder.Node.Id)))
            {
                continue;
            }

            FolderOptions.Add(new FolderChoiceOption($"{new string(' ', folder.Depth * 2)}{folder.Node.Name}",
                folder.Node));
        }

        if (EditorParentFolder is null || !FolderOptions.Any(option => option.Node?.Id == EditorParentFolder.Node?.Id))
        {
            EditorParentFolder = FolderOptions.FirstOrDefault();
        }
    }
}