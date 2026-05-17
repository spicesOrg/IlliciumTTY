using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace IlliciumTTY.Models;

public enum NodeType
{
    Folder,
    SshLink
}

public enum GroupType
{
    Favorite,
    Temporary
}

public enum ConnectionDropPlacement
{
    Before,
    Inside,
    After
}

public enum AuthType
{
    Password,
    PrivateKey,
    Agent
}

public enum WorkspaceMode
{
    Single,
    Multi
}

public enum SyncMode
{
    TerminalToFileManager,
    FileManagerToTerminal,
    Bidirectional,
    Disabled
}

public enum TerminalStatus
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
    Error
}

public enum RemoteFileType
{
    File,
    Directory,
    Symlink,
    Unknown
}

public sealed class ConnectionNodeModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public NodeType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public GroupType GroupType { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ConnectionNodeModel> Children { get; set; } = [];

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public AuthType AuthType { get; set; } = AuthType.Password;
    public string? PasswordRef { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PassphraseRef { get; set; }
    public string? DefaultRemotePath { get; set; }

    [JsonIgnore] public string? TransientPassword { get; set; }

    [JsonIgnore] public string? TransientPassphrase { get; set; }
}

public sealed class AppConfig
{
    public List<ConnectionNodeModel> FavoriteNodes { get; set; } = [];
    public List<ConnectionNodeModel> TemporaryNodes { get; set; } = [];
    public SyncMode DefaultSyncMode { get; set; } = SyncMode.Bidirectional;
    public string? LastOpenedNodeId { get; set; }
    public HashSet<string> ExpandedNodeIds { get; set; } = [];
    public double SidebarWidth { get; set; } = 230;
    public double WorkspaceSplitRatio { get; set; } = 0.52;
}

public sealed class RemoteFileItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public RemoteFileType Type { get; set; }
    public long Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string Permissions { get; set; } = string.Empty;
    public string? Owner { get; set; }
    public string? Group { get; set; }
}