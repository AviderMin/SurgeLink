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

        cancellationToken.ThrowIfCancellationRequested();
        var sourceEntry = await fileSystem.StatAsync(source, cancellationToken).ConfigureAwait(false);
        if (sourceEntry.Kind is not (RemoteEntryKind.File or RemoteEntryKind.Directory))
            throw new IOException("Copying symbolic links or special files is not supported.");
        if (await fileSystem.TryStatAsync(destination, cancellationToken).ConfigureAwait(false) is not null)
            throw new IOException($"Destination already exists: {destination}");

        // Keep an incomplete tree away from the final name, including when mkdir
        // succeeds remotely but its reply is lost. Only this unique sibling is
        // eligible for rollback; a concurrent creator of destination is untouched.
        var copyDestination = sourceEntry.IsDirectory
            ? destination[..(destination.LastIndexOf('/') + 1)] + ".surge-" + Guid.NewGuid().ToString("N") + ".tmp"
            : destination;
        // Directories stay owner-writable until the whole tree has been copied.
        // Restoring a child's final mode immediately would make the rollback
        // unable to remove files inside it when a later sibling fails.
        var createdDirectories = new List<(string Path, int? Mode)>();
        var createdDestination = false;
        try
        {
            await CopyEntryAsync(source, copyDestination, 0, isRoot: true, known: sourceEntry).ConfigureAwait(false);
            if (fileSystem.SupportsPermissions)
            {
                foreach (var (path, mode) in createdDirectories.AsEnumerable().Reverse())
                {
                    if (mode is int value)
                        await fileSystem.SetPermissionsAsync(path, value, cancellationToken).ConfigureAwait(false);
                }
            }
            if (sourceEntry.IsDirectory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Rename must fail if another operation created the final name.
                // Never delete that name on failure, even if the rename reply is lost.
                await fileSystem.RenameAsync(copyDestination, destination, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // A failure part-way through a folder copy would otherwise leave a
            // half-populated destination that permanently blocks every retry.
            if (createdDestination && fileSystem.SupportsPermissions)
            {
                // Only directories created by this copy need their owner write
                // permission back before the recursive delete can succeed. Restore
                // from the root down: a parent without execute permission blocks
                // chmod on its children, so deepest-first would fail until the
                // parent is fixed and would never retry the children.
                foreach (var (path, _) in createdDirectories)
                {
                    try { await fileSystem.SetPermissionsAsync(path, 0x1C0, cleanup.Token).ConfigureAwait(false); }
                    catch (Exception) { /* Best effort; keep the original failure. */ }
                }
            }
            if (createdDestination)
            {
                try { await fileSystem.DeleteAsync(copyDestination, recursive: true, cleanup.Token).ConfigureAwait(false); }
                catch (Exception) { /* Best effort; keep the original failure. */ }
            }

            throw;
        }

        async Task CopyEntryAsync(string from, string to, int depth, bool isRoot = false, RemoteFileEntry? known = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > RemoteFileLimits.MaximumDepth) { throw new IOException("Directory nesting exceeds the copy limit."); }
            var entry = known ?? await fileSystem.StatAsync(from, cancellationToken).ConfigureAwait(false);
            if (entry.Kind is not (RemoteEntryKind.File or RemoteEntryKind.Directory))
            {
                throw new IOException("Copying symbolic links or special files is not supported.");
            }
            if (entry.IsDirectory)
            {
                var children = await fileSystem.ListAsync(from, cancellationToken).ConfigureAwait(false);
                // Record before awaiting mkdir: a lost reply can leave the path
                // present even though the call throws. It is under our staging root.
                if (isRoot) { createdDestination = true; }
                createdDirectories.Add((to, entry.UnixMode is int directoryMode ? directoryMode & 0x1FF : null));
                await fileSystem.CreateDirectoryAsync(to, cancellationToken).ConfigureAwait(false);
                if (fileSystem.SupportsPermissions)
                    await fileSystem.SetPermissionsAsync(to, 0x1C0, cancellationToken).ConfigureAwait(false);
                foreach (var child in children)
                {
                    if (string.IsNullOrEmpty(child.Name) || child.Name is "." or ".." || child.Name.Contains('/') || child.Name.Contains('\0'))
                    {
                        throw new IOException("The server returned an invalid child name.");
                    }
                    // Reuse the entry the parent listing already returned. A single-path
                    // stat re-lists the parent on SFTP, which would turn a wide directory
                    // into one directory scan per child.
                    await CopyEntryAsync(from + "/" + child.Name, to + "/" + child.Name, depth + 1, known: child).ConfigureAwait(false);
                }
            }
            else
            {
                var mode = (entry.UnixMode ?? 0x180) & 0x1FF;
                await using var input = await fileSystem.OpenReadAsync(from, cancellationToken).ConfigureAwait(false);
                if (isRoot)
                {
                    // A file copy publishes at the final name, so it keeps the staged
                    // write and its commit-time no-overwrite guarantee.
                    await RemoteFileWrite.WriteAsync(fileSystem, to, entry.Size, null, false,
                        (output, token) => input.CopyToAsync(output, token), cancellationToken,
                        unixMode: mode, destinationKnownAbsent: true).ConfigureAwait(false);
                }
                else
                {
                    // Inside the unique staging tree of a directory copy, a partial file
                    // is removed with the tree. Writing directly avoids one private
                    // staging directory and its parent scan per file.
                    await using (var output = await fileSystem.OpenWriteAsync(to, entry.Size, null, cancellationToken).ConfigureAwait(false))
                    {
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                    if (fileSystem.SupportsPermissions)
                        await fileSystem.SetPermissionsAsync(to, mode, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
