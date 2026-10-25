namespace SurgeLink.Core.Files;

/// <summary>Deletes a confirmed selection sequentially, preserving each exact remote path.</summary>
public static class RemoteFileDelete
{
    public static async Task<RemoteDeleteResult> DeleteManyAsync(IRemoteFileSystem fileSystem,
        IReadOnlyList<RemoteFileEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(entries);
        // Freeze the caller's selection before the first asynchronous operation.
        var targets = entries.DistinctBy(entry => entry.FullPath, StringComparer.Ordinal).ToArray();
        var deleted = new List<RemoteFileEntry>();
        var failures = new List<RemoteDeleteFailure>();
        foreach (var entry in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var leaf = entry.FullPath[(entry.FullPath.LastIndexOf('/') + 1)..];
                if (!entry.FullPath.StartsWith('/') || leaf.Length == 0 || leaf is "." or ".." || entry.FullPath.Contains('\0'))
                    throw new IOException("Refusing to delete an invalid path or the remote root directory.");
                await fileSystem.DeleteAsync(entry.FullPath, entry.IsDirectory, cancellationToken).ConfigureAwait(false);
                deleted.Add(entry);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failures.Add(new RemoteDeleteFailure(entry, exception.Message));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new RemoteDeleteResult(deleted.ToArray(), failures.ToArray());
    }
}

/// <summary>One entry that could not be deleted.</summary>
public sealed record RemoteDeleteFailure(RemoteFileEntry Entry, string Message);

/// <summary>Successful and failed entries from a batch, for accurate partial-success feedback.</summary>
public sealed record RemoteDeleteResult(IReadOnlyList<RemoteFileEntry> Deleted, IReadOnlyList<RemoteDeleteFailure> Failures);
