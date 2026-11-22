using System.Runtime.CompilerServices;

namespace SurgeLink.Core.Files;

/// <summary>Stages complete file contents before publishing them at the final path.</summary>
public static class RemoteFileWrite
{
    private static readonly ConditionalWeakTable<IRemoteFileSystem, SemaphoreSlim> Writers = new();

    /// <summary>
    /// Writes complete contents at <paramref name="destination"/>. The precheck stat is a
    /// friendly fast-fail only; the commit still refuses to replace an existing file when
    /// <paramref name="overwrite"/> is false. Set <paramref name="destinationKnownAbsent"/>
    /// when the caller just created a unique staging parent: on SFTP every single-path
    /// stat re-lists its parent, so probing each file would make a wide batch copy
    /// quadratic.
    /// </summary>
    public static async Task WriteAsync(IRemoteFileSystem fileSystem, string destination, long? size,
        IProgress<TransferProgress>? progress, bool overwrite,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? validateDestination = null, int? unixMode = null,
        bool destinationKnownAbsent = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(write);
        if (overwrite && destinationKnownAbsent)
            throw new ArgumentException("A destination known to be absent cannot be overwritten.", nameof(overwrite));
        var separator = destination.LastIndexOf('/');
        if (!destination.StartsWith('/') || separator == destination.Length - 1) { throw new ArgumentException("An absolute file path is required.", nameof(destination)); }
        var writer = Writers.GetValue(fileSystem, _ => new SemaphoreSlim(1, 1));
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteCoreAsync(fileSystem, destination, size, progress, overwrite, write, validateDestination, unixMode, destinationKnownAbsent, cancellationToken).ConfigureAwait(false); }
        finally { writer.Release(); }
    }

    private static async Task WriteCoreAsync(IRemoteFileSystem fileSystem, string destination, long? size,
        IProgress<TransferProgress>? progress, bool overwrite, Func<Stream, CancellationToken, Task> write,
        Func<CancellationToken, Task>? validateDestination, int? unixMode, bool destinationKnownAbsent,
        CancellationToken cancellationToken)
    {
        var separator = destination.LastIndexOf('/');
        var directory = separator == 0 ? "/" : destination[..separator];
        var stagingDirectory = directory.TrimEnd('/') + "/.surge-" + Guid.NewGuid().ToString("N") + ".tmp";
        // Restrict the empty directory before choosing the payload name or writing
        // any content. SCP may create files with its own default permissions.
        string? temporary = null;
        var committed = false;
        var stagingStarted = false;
        try
        {
            if (validateDestination is not null) await validateDestination(cancellationToken).ConfigureAwait(false);
            // CommitWriteAsync still re-checks and never overwrites when overwrite is
            // false, so this lookup is only a fast, friendly precheck.
            RemoteFileEntry? existing = null;
            if (!destinationKnownAbsent)
            {
                existing = await fileSystem.TryStatAsync(destination, cancellationToken).ConfigureAwait(false);
                if (existing is not null && (!overwrite || existing.Kind != RemoteEntryKind.File))
                {
                    throw new IOException($"Destination already exists or is not a regular file: {destination}");
                }
            }
            // Mark before the await returns: a lost reply can leave the directory
            // behind even though CreateDirectoryAsync throws, and the finally block
            // must still try to remove it.
            stagingStarted = true;
            await fileSystem.CreateDirectoryAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);
            if (fileSystem.SupportsPermissions)
                await fileSystem.SetPermissionsAsync(stagingDirectory, 0x1C0, cancellationToken).ConfigureAwait(false);
            temporary = stagingDirectory + "/" + Guid.NewGuid().ToString("N");
            await using (var stream = await fileSystem.OpenWriteAsync(temporary, size, progress, cancellationToken).ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Content replacement inherits ordinary access bits, never privilege bits.
            if ((unixMode ?? (existing?.UnixMode & 0x1FF)) is int mode && fileSystem.SupportsPermissions)
            {
                await fileSystem.SetPermissionsAsync(temporary, mode, cancellationToken).ConfigureAwait(false);
            }
            if (validateDestination is not null) await validateDestination(cancellationToken).ConfigureAwait(false);
            if (overwrite && existing is not null)
                await RemoteFileAccess.RequireRegularFileAsync(fileSystem, destination, cancellationToken).ConfigureAwait(false);
            await fileSystem.CommitWriteAsync(temporary, destination, overwrite && existing is not null, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            if (stagingStarted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    if (!committed && temporary is not null)
                    {
                        try { await fileSystem.DeleteAsync(temporary, false, cleanup.Token).ConfigureAwait(false); }
                        catch (Exception) { /* A failed open may not have created a payload. */ }
                    }
                    await fileSystem.DeleteAsync(stagingDirectory, false, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* Keep the original failure; never delete the final destination. */ }
            }
        }
    }
}
