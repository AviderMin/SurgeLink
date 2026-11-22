using SurgeLink.Core.Protocols;

namespace SurgeLink.Core.Files;

/// <summary>Progress of a single file transfer.</summary>
public sealed record TransferProgress(long BytesTransferred, long? TotalBytes, string CurrentPath)
{
    /// <summary>0..1 when the total is known, otherwise <c>null</c>.</summary>
    public double? Fraction =>
        TotalBytes is long total and > 0 ? Math.Clamp((double)BytesTransferred / total, 0d, 1d) : null;
}

/// <summary>
/// Minimal remote file system surface used by the SFTP browser.
/// Protocols without file transfer simply do not expose this capability.
/// </summary>
public interface IRemoteFileSystem
{
    /// <summary>True when the protocol can report and change POSIX permissions.</summary>
    bool SupportsPermissions { get; }

    /// <summary>Remote home directory of the logged in user.</summary>
    Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken);

    /// <summary>Current working directory of the file subsystem.</summary>
    Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken);

    /// <summary>Lists a directory, excluding <c>.</c> and <c>..</c>.</summary>
    Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>Resolves a single entry, following no symlinks.</summary>
    Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a single entry, returning <c>null</c> when the server reports that
    /// the path does not exist. Protocols that use one stat call per path can
    /// override this; the default intentionally maps only "configuration/not
    /// found" failures to <c>null</c> and lets transport failures propagate.
    /// </summary>
    async Task<RemoteFileEntry?> TryStatAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await StatAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolException exception) when (exception.Kind == ProtocolFailureKind.Configuration)
        {
            return null;
        }
    }

    /// <summary>Opens a readable stream; the caller disposes it.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a writable stream. <paramref name="progress"/> receives byte counts
    /// as data flows; the caller disposes the stream to complete the write.
    /// </summary>
    Task<Stream> OpenWriteAsync(
        string path,
        long? expectedSize,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Creates a directory (single level).</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Deletes a file or, when <paramref name="recursive"/>, a directory tree.</summary>
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);

    /// <summary>Renames or moves an entry. Must fail without replacing or moving into
    /// an existing destination; never emulate a move by deleting the destination first.</summary>
    Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);

    /// <summary>Publishes a staging file on the destination's filesystem. Must fail without replacing an existing
    /// target when overwrite is false. With overwrite, replace atomically or fail safely.</summary>
    Task CommitWriteAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("This protocol does not support safe staged writes."));

    /// <summary>Changes POSIX permissions; no-op when <see cref="SupportsPermissions"/> is false.</summary>
    Task SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken);
}
