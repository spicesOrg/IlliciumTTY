namespace IlliciumTTY.ViewModels;

public sealed class ConnectionWorkspaceState(
    ConnectionNodeViewModel link,
    TerminalSessionViewModel terminalSession,
    RemoteFileBrowserViewModel fileBrowser)
{
    public ConnectionNodeViewModel Link { get; } = link;
    public TerminalSessionViewModel TerminalSession { get; } = terminalSession;
    public RemoteFileBrowserViewModel FileBrowser { get; } = fileBrowser;
}