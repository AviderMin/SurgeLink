namespace SurgeLink.Core.Files;

/// <summary>Kind of entry returned by a remote directory listing.</summary>
public enum RemoteEntryKind
{
    File,
    Directory,
    SymbolicLink,
    Other,
}

/// <summary>One entry of a remote directory listing.</summary>
public sealed record RemoteFileEntry
{
    /// <summary>Last path segment.</summary>
    public required string Name { get; init; }

    /// <summary>Full remote path using forward slashes.</summary>
    public required string FullPath { get; init; }

    /// <summary>Entry kind.</summary>
    public RemoteEntryKind Kind { get; init; }

    /// <summary>Size in bytes; 0 for directories.</summary>
    public long Size { get; init; }

    /// <summary>Last modification time in UTC.</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Unix mode bits when the protocol exposes them.</summary>
    public int? UnixMode { get; init; }

    /// <summary>Owning user, when reported by the server.</summary>
    public string? Owner { get; init; }

    /// <summary>Owning group, when reported by the server.</summary>
    public string? Group { get; init; }

    /// <summary>Symlink target, when the entry is a link.</summary>
    public string? LinkTarget { get; init; }

    public bool IsDirectory => Kind == RemoteEntryKind.Directory;

    /// <summary>Conventional permission string such as <c>drwxr-xr-x</c>.</summary>
    public string PermissionText => UnixMode is int mode ? FormatMode(mode, Kind == RemoteEntryKind.Directory) : string.Empty;

    /// <summary>Renders unix mode bits as <c>rwxr-xr-x</c>.</summary>
    public static string FormatMode(int mode, bool isDirectory)
    {
        Span<char> buffer = stackalloc char[10];
        buffer[0] = isDirectory ? 'd' : '-';
        const string rwx = "rwx";
        for (var i = 0; i < 9; i++)
        {
            var bit = 1 << (8 - i);
            buffer[i + 1] = (mode & bit) != 0 ? rwx[i % 3] : '-';
        }

        return new string(buffer);
    }
}
