using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Services;
using SurgeLink.Core.Files;
using SurgeLink.Core.Sessions;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Props for <see cref="SftpView"/>.</summary>
/// <param name="FileSystem">Remote file system of the session; <c>null</c> when the protocol has no SFTP.</param>
/// <param name="Profile">Session profile, used for headings and the local download folder hint.</param>
/// <param name="Settings">User preferences: the hidden file toggle lives here.</param>
internal sealed record SftpViewProps(IRemoteFileSystem? FileSystem, SessionProfile Profile, IAppSettingsStore Settings);

/// <summary>Columns the file list can be sorted by.</summary>
internal enum SftpSortColumn
{
    Name,
    Size,
    Modified,
    Permissions,
    Owner,
}

/// <summary>Which modal dialog the file manager currently shows.</summary>
internal enum SftpDialogKind
{
    None,
    NewFolder,
    Rename,
    Delete,
    Permissions,
}

/// <summary>State of the one dialog that can be open at a time.</summary>
/// <param name="Kind">Dialog to show.</param>
/// <param name="Target">Entry the action applies to; <c>null</c> for new folder.</param>
internal sealed record SftpDialogState(SftpDialogKind Kind, RemoteFileEntry? Target = null)
{
    /// <summary>The "nothing open" state.</summary>
    public static SftpDialogState None { get; } = new(SftpDialogKind.None);
}

/// <summary>A dismissible status message rendered as an InfoBar above the list.</summary>
internal sealed record SftpBanner(InfoBarSeverity Severity, string Title, string Message);
