using Microsoft.Extensions.Logging;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh.Internal;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// File-system facade for an SSH session. SFTP is preferred because it supports
/// metadata and random access; if the server has no SFTP subsystem (common on
/// minimal Dropbear/OpenWrt installations) the facade switches to the SCP
/// fallback, which lists paths with SSH commands and transfers files with SCP.
/// </summary>
internal sealed class SshFileSystem : IRemoteFileSystem, IAsyncDisposable
{
    private readonly SshConnectionFactory _factory;
    private readonly SshProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly SftpFileSystem _sftp;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private ScpFileSystem? _scp;
    private int _disposed;

    internal SshFileSystem(SshConnectionFactory factory, SshProtocolOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _options = options;
        _logger = logger;
        _sftp = new SftpFileSystem(factory, logger);
    }

    /// <inheritdoc />
    public bool SupportsPermissions => true;

    /// <inheritdoc />
    public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.GetHomeDirectoryAsync(token), cancellationToken);

    /// <inheritdoc />
    public Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.GetCurrentDirectoryAsync(token), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.ListAsync(path, token), cancellationToken);

    /// <inheritdoc />
    public Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.StatAsync(path, token), cancellationToken);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.OpenReadAsync(path, token), cancellationToken);

    /// <inheritdoc />
    public Task<Stream> OpenWriteAsync(
        string path,
        long? expectedSize,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (fileSystem, token) => fileSystem.OpenWriteAsync(path, expectedSize, progress, token),
            cancellationToken);

    /// <inheritdoc />
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.CreateDirectoryAsync(path, token), cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.DeleteAsync(path, recursive, token), cancellationToken);

    public Task CommitWriteAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.CommitWriteAsync(temporaryPath, destinationPath, overwrite, token), cancellationToken);

    /// <inheritdoc />
    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.RenameAsync(sourcePath, destinationPath, token), cancellationToken);

    /// <inheritdoc />
    public Task SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken) =>
        ExecuteAsync((fileSystem, token) => fileSystem.SetPermissionsAsync(path, unixMode, token), cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var scp = Interlocked.Exchange(ref _scp, null);
        if (scp is not null)
        {
            await scp.DisposeAsync().ConfigureAwait(false);
        }

        await _sftp.DisposeAsync().ConfigureAwait(false);
        _switchLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ExecuteAsync(
        Func<IRemoteFileSystem, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
                async (fileSystem, token) =>
                {
                    await operation(fileSystem, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> ExecuteAsync<T>(
        Func<IRemoteFileSystem, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var scp = Volatile.Read(ref _scp);
        if (scp is not null)
        {
            return await operation(scp, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await operation(_sftp, cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolException exception) when (IsSftpUnavailable(exception))
        {
            await _switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                scp = _scp;
                if (scp is null)
                {
                    _logger.LogInformation(
                        exception,
                        "SFTP is unavailable for this server; switching the file system to SCP fallback.");
                    scp = new ScpFileSystem(_factory, _options, _logger);
                    Volatile.Write(ref _scp, scp);
                    await _sftp.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _switchLock.Release();
            }

            return await operation(scp!, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsSftpUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (string.IsNullOrEmpty(message))
            {
                continue;
            }

            if (message.Contains("sftp", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("channel was closed", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("subsystem", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
