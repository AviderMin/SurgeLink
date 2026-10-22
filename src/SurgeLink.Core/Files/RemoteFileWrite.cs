namespace SurgeLink.Core.Files;

/// <summary>Stages complete file contents before publishing them at the final path.</summary>
public static class RemoteFileWrite
{
    public static async Task WriteAsync(IRemoteFileSystem fileSystem, string destination, long? size,
        IProgress<TransferProgress>? progress, bool overwrite,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var separator = destination.LastIndexOf('/');
        if (separator < 0 || separator == destination.Length - 1) { throw new ArgumentException("An absolute file path is required.", nameof(destination)); }
        var directory = separator == 0 ? "/" : destination[..separator];
        var temporary = directory.TrimEnd('/') + "/.surge-" + Guid.NewGuid().ToString("N") + ".tmp";
        var committed = false;
        var stagingStarted = false;
        try
        {
            var siblings = await fileSystem.ListAsync(directory, cancellationToken).ConfigureAwait(false);
            var existing = siblings.FirstOrDefault(entry => entry.Name == destination[(separator + 1)..]);
            if (existing is not null && (!overwrite || existing.Kind != RemoteEntryKind.File))
            {
                throw new IOException($"Destination already exists or is not a regular file: {destination}");
            }
            stagingStarted = true;
            await using (var stream = await fileSystem.OpenWriteAsync(temporary, size, progress, cancellationToken).ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (existing?.UnixMode is int mode && fileSystem.SupportsPermissions)
            {
                await fileSystem.SetPermissionsAsync(temporary, mode, cancellationToken).ConfigureAwait(false);
            }
            await fileSystem.CommitWriteAsync(temporary, destination, overwrite && existing is not null, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            if (stagingStarted && !committed)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await fileSystem.DeleteAsync(temporary, false, cleanup.Token).ConfigureAwait(false); }
                catch (Exception) { /* Keep the original failure; never delete the final destination. */ }
            }
        }
    }
}
