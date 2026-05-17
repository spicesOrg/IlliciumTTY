using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using IlliciumTTY.Models;

namespace IlliciumTTY.ViewModels;

public sealed partial class ConnectionNodeViewModel : ViewModelBase
{
    [ObservableProperty] private AuthType _authType = AuthType.Password;

    [ObservableProperty] private string? _defaultRemotePath;

    [ObservableProperty] private string _host = string.Empty;

    [ObservableProperty] private bool _isDragging;

    [ObservableProperty] private bool _isDropAfter;

    [ObservableProperty] private bool _isDropBefore;

    [ObservableProperty] private bool _isDropInside;

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private int _port = 22;

    [ObservableProperty] private string? _privateKeyPath;

    [ObservableProperty] private string _username = string.Empty;

    public ConnectionNodeViewModel(ConnectionNodeModel model)
    {
        Id = model.Id;
        Type = model.Type;
        GroupType = model.GroupType;
        ParentId = model.ParentId;
        SortOrder = model.SortOrder;
        CreatedAt = model.CreatedAt;
        UpdatedAt = model.UpdatedAt;
        Name = model.Name;
        Host = model.Host;
        Port = model.Port;
        Username = model.Username;
        AuthType = model.AuthType;
        PasswordRef = model.PasswordRef;
        PrivateKeyPath = model.PrivateKeyPath;
        PassphraseRef = model.PassphraseRef;
        DefaultRemotePath = model.DefaultRemotePath;
        TransientPassword = model.TransientPassword;
        TransientPassphrase = model.TransientPassphrase;

        foreach (var child in model.Children.OrderBy(child => child.SortOrder))
        {
            Children.Add(new ConnectionNodeViewModel(child));
        }
    }

    public string Id { get; }
    public NodeType Type { get; }
    public GroupType GroupType { get; set; }
    public string? ParentId { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string? PasswordRef { get; set; }
    public string? PassphraseRef { get; set; }
    public string? TransientPassword { get; set; }
    public string? TransientPassphrase { get; set; }
    public ObservableCollection<ConnectionNodeViewModel> Children { get; } = [];

    public bool IsFolder => Type == NodeType.Folder;
    public bool IsSshLink => Type == NodeType.SshLink;
    public string TypeLabel => IsFolder ? "文件夹" : "SSH";

    public string AuthLabel => AuthType switch
    {
        AuthType.Password => "密码",
        AuthType.PrivateKey => "私钥",
        AuthType.Agent => "Agent",
        _ => "未知"
    };

    public string DisplaySubtitle => IsFolder
        ? $"{Children.Count} 个项目"
        : $"{Username}@{Host}:{Port}";

    public string Badge => IsFolder ? "DIR" : "SSH";
    public double DragOpacity => IsDragging ? 0.45 : 1.0;

    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(DisplaySubtitle));
    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(DisplaySubtitle));
    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(DisplaySubtitle));
    partial void OnAuthTypeChanged(AuthType value) => OnPropertyChanged(nameof(AuthLabel));
    partial void OnIsDraggingChanged(bool value) => OnPropertyChanged(nameof(DragOpacity));

    public ConnectionNodeModel ToModel(string? parentId, GroupType groupType, int sortOrder)
    {
        ParentId = parentId;
        GroupType = groupType;
        SortOrder = sortOrder;
        UpdatedAt = DateTimeOffset.UtcNow;

        return new ConnectionNodeModel
        {
            Id = Id,
            Type = Type,
            Name = Name,
            ParentId = parentId,
            GroupType = groupType,
            SortOrder = sortOrder,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Children = Children
                .Select((child, index) => child.ToModel(Id, groupType, index))
                .ToList(),
            Host = Host,
            Port = Port,
            Username = Username,
            AuthType = AuthType,
            PasswordRef = PasswordRef,
            PrivateKeyPath = PrivateKeyPath,
            PassphraseRef = PassphraseRef,
            DefaultRemotePath = DefaultRemotePath,
            TransientPassword = TransientPassword,
            TransientPassphrase = TransientPassphrase
        };
    }

    public ConnectionNodeModel ToLinkModel()
    {
        return new ConnectionNodeModel
        {
            Id = Id,
            Type = Type,
            Name = Name,
            ParentId = ParentId,
            GroupType = GroupType,
            SortOrder = SortOrder,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Host = Host,
            Port = Port,
            Username = Username,
            AuthType = AuthType,
            PasswordRef = PasswordRef,
            PrivateKeyPath = PrivateKeyPath,
            PassphraseRef = PassphraseRef,
            DefaultRemotePath = DefaultRemotePath,
            TransientPassword = TransientPassword,
            TransientPassphrase = TransientPassphrase
        };
    }

    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(DisplaySubtitle));
    }
}