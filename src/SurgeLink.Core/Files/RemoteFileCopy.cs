namespace SurgeLink.Core.Files;

/// <summary>Remote streaming copy with directory recursion and destination conflict checks.</summary>
public static class RemoteFileCopy
{
    public static async Task CopyAsync(IRemoteFileSystem fileSystem, string source, string destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        source = source.TrimEnd('/');
        destination = destination.TrimEnd('/');
        if (!source.StartsWith('/') || !destination.StartsWith('/') ||
            source.Split('/').Any(segment => segment is "." or "..") ||
            destination.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Copy paths must be absolute and normalized.");
        }
        if (source.Length == 0 || destination == source || destination.StartsWith(source + "/", StringComparison.Ordinal))
        {
            throw new IOException("Cannot copy an item into itself or its own subfolder.");
        }

        // Directories stay owner-writable until the whole tree has been copied.
        // Restoring a child's final mode immediately would make the rollback
        // unable to remove files inside it when a later sibling fails.
        var createdDirectories = new List<(string Path, int? Mode)>();
        var createdDestination = false;
        try
        {
            await CopyEntryAsync(source, destination, 0, isRoot: true).ConfigureAwait(false);
            if (fileSystem.SupportsPermissions)
            {
                foreach (var (path, mode) in createdDirectories.AsEnumerable().Reverse())
                {
                    if (mode is int value)
                        await fileSystem.SetPermissionsAsync(path, value, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // A failure part-way through a folder copy would otherwise leave a
            // half-populated destination that permanently blocks every retry.
            if (createdDestination && fileSystem.SupportsPermissions)
            {
                // Only directories created by this copy need their owner write
                // permission back before the recursive delete can succeed.
                foreach (var (path, _) in createdDirectories.AsEnumerable().Reverse())
                {
                    try { await fileSystem.SetPermissionsAsync(path, 0x1C0, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { /* Best effort; keep the original failure. */ }
                }
            }
            if (createdDestination)
            {
                try { await fileSystem.DeleteAsync(destination, recursive: true, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* Best effort; keep the original failure. */ }
            }

            throw;
        }

        async Task CopyEntryAsync(string from, string to, int depth, bool isRoot = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 128) { throw new IOException("Directory nesting exceeds the copy limit."); }
            var entry = await fileSystem.StatAsync(from, cancellationToken).ConfigureAwait(false);
            if (entry.Kind is not (RemoteEntryKind.File or RemoteEntryKind.Directory))
            {
                throw new IOException("Copying symbolic links or special files is not supported.");
            }
            var existing = await fileSystem.TryStatAsync(to, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                throw new IOException($"Destination already exists: {to}");
            }
            if (entry.IsDirectory)
            {
                var children = await fileSystem.ListAsync(from, cancellationToken).ConfigureAwait(false);
                await fileSystem.CreateDirectoryAsync(to, cancellationToken).ConfigureAwait(false);
                if (isRoot) { createdDestination = true; }
                createdDirectories.Add((to, entry.UnixMode is int directoryMode ? directoryMode & 0x1FF : null));
                if (fileSystem.SupportsPermissions)
                    await fileSystem.SetPermissionsAsync(to, 0x1C0, cancellationToken).ConfigureAwait(false);
                foreach (var child in children)
                {
                    if (string.IsNullOrEmpty(child.Name) || child.Name is "." or ".." || child.Name.Contains('/') || child.Name.Contains('\0'))
                    {
                        throw new IOException("The server returned an invalid child name.");
                    }
                    await CopyEntryAsync(from + "/" + child.Name, to + "/" + child.Name, depth + 1).ConfigureAwait(false);
                }
            }
            else
            {
                await using var input = await fileSystem.OpenReadAsync(from, cancellationToken).ConfigureAwait(false);
                await RemoteFileWrite.WriteAsync(fileSystem, to, entry.Size, null, false,
                    (output, token) => input.CopyToAsync(output, token), cancellationToken,
                    unixMode: (entry.UnixMode ?? 0x180) & 0x1FF).ConfigureAwait(false);
            }
        }
    }
}
