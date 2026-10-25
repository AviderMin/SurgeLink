using System.Runtime.CompilerServices;

namespace SurgeLink.Core.Files;

/// <summary>Stages complete file contents before publishing them at the final path.</summary>
public static class RemoteFileWrite
{
    private static readonly ConditionalWeakTable<IRemoteFileSystem, SemaphoreSlim> Writers = new();

    public static async Task WriteAsync(IRemoteFileSystem fileSystem, string destination, long? size,
        IProgress<TransferProgress>? progress, bool overwrite,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? validateDestination = null, int? unixMode = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(write);
        var separator = destination.LastIndexOf('/');
        if (!destination.StartsWith('/') || separator == destination.Length - 1) { throw new ArgumentException("An absolute file path is required.", nameof(destination)); }
        var writer = Writers.GetValue(fileSystem, _ => new SemaphoreSlim(1, 1));
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteCoreAsync(fileSystem, destination, size, progress, overwrite, write, validateDestination, unixMode, cancellationToken).ConfigureAwait(false); }
        finally { writer.Release(); }
    }

    private static async Task WriteCoreAsync(IRemoteFileSystem fileSystem, string destination, long? size,
        IProgress<TransferProgress>? progress, bool overwrite, Func<Stream, CancellationToken, Task> write,
        Func<CancellationToken, Task>? validateDestination, int? unixMode, CancellationToken cancellationToken)
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
            var siblings = await fileSystem.ListAsync(directory, cancellationToken).ConfigureAwait(false);
            var existing = siblings.FirstOrDefault(entry => entry.Name == destination[(separator + 1)..]);
            if (existing is not null && (!overwrite || existing.Kind != RemoteEntryKind.File))
            {
                throw new IOException($"Destination already exists or is not a regular file: {destination}");
            }
            await fileSystem.CreateDirectoryAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);
            stagingStarted = true;
            if (fileSystem.SupportsPermissions)
                await fileSystem.SetPermissionsAsync(stagingDirectory, 0x1C0, cancellationToken).ConfigureAwait(false);
            temporary = stagingDirectory + "/" + Guid.NewGuid().ToString("N");
            await using (var stream = await fileSystem.OpenWriteAsync(temporary, size, progress, cancellationToken).ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if ((unixMode ?? existing?.UnixMode) is int mode && fileSystem.SupportsPermissions)
            {
                await fileSystem.SetPermissionsAsync(temporary, mode, cancellationToken).ConfigureAwait(false);
            }
            if (validateDestination is not null) await validateDestination(cancellationToken).ConfigureAwait(false);
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
