namespace SurgeLink.Core.Files;

/// <summary>File operations that must never open devices, FIFOs, sockets or links.</summary>
public static class RemoteFileAccess
{
    public static void RequireRegularFile(RemoteFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != RemoteEntryKind.File)
            throw new IOException("This operation requires a regular file: " + entry.FullPath);
    }

    public static async Task RequireRegularFileAsync(IRemoteFileSystem fileSystem, string path, CancellationToken cancellationToken) =>
        RequireRegularFile(await fileSystem.StatAsync(path, cancellationToken).ConfigureAwait(false));
}
