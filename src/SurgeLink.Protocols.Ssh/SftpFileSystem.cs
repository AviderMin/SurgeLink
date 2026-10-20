using System.Globalization;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh.Internal;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// SFTP backed file system. Opens its own SSH connection built from the same
/// <see cref="ConnectionRequest"/> so terminal and file traffic never share a channel.
/// </summary>
/// <remarks>
/// The SFTP protocol only carries numeric user and group identifiers, so
/// <see cref="RemoteFileEntry.Owner"/> and <see cref="RemoteFileEntry.Group"/> hold ids
/// rather than account names.
/// </remarks>
public sealed class SftpFileSystem : IRemoteFileSystem, IAsyncDisposable
{
    private const int MaximumDeleteDepth = 64;
    private static readonly TimeSpan SftpOperationTimeout = TimeSpan.FromSeconds(60);

    private readonly SshConnectionFactory _factory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private SftpClient? _client;
    private string? _homeDirectory;
    private int _disposed;

    /// <summary>Creates the file system facade; the SFTP connection is opened on first use.</summary>
    internal SftpFileSystem(SshConnectionFactory factory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool SupportsPermissions => true;

    /// <inheritdoc />
    public async Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var home = _homeDirectory;
        if (!string.IsNullOrEmpty(home))
        {
            return home!;
        }

        home = TryGetWorkingDirectory(client) ?? "/";
        _homeDirectory = home;
        return home;
    }

    /// <inheritdoc />
    public async Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken) =>
        await GetHomeDirectoryAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = ResolvePath(client, path);

        try
        {
            var entries = new List<RemoteFileEntry>();
            await foreach (var file in client.ListDirectoryAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                entries.Add(MapEntry(file));
            }

            entries.Sort(CompareEntries);
            return entries;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SshErrorMapper.MapFileSystem(exception, fullPath, "list directory");
        }
    }

    /// <inheritdoc />
    public async Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = ResolvePath(client, path);

        try
        {
            var file = await client.GetAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                throw new ProtocolException($"Remote path not found: '{fullPath}'.", ProtocolFailureKind.Configuration);
            }

            return MapEntry(file);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SshErrorMapper.MapFileSystem(exception, fullPath, "read file status");
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = ResolvePath(client, path);

        try
        {
            var stream = await client.OpenAsync(fullPath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SshErrorMapper.MapFileSystem(exception, fullPath, "open for reading");
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenWriteAsync(
        string path,
        long? expectedSize,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = ResolvePath(client, path);

        try
        {
            var stream = await client.OpenAsync(fullPath, FileMode.Create, FileAccess.Write, cancellationToken).ConfigureAwait(false);
            return new ProgressStream(stream, expectedSize, fullPath, progress, leaveOpen: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SshErrorMapper.MapFileSystem(exception, fullPath, "open for writing");
        }
    }

    /// <inheritdoc />
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = ResolvePath(client, path);

        try
        {
            await client.CreateDirectoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SshErrorMapper.MapFileSystem(exception, fullPath, "create directory");
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = ResolvePath(client, path);
        if (fullPath == "/")
        {
            throw new ProtocolException("Refusing to delete the remote root directory.", ProtocolFailureKind.Configuration);
        }

        try
        {
            if (recursive)
            {
                await DeleteTreeAsync(client, fullPath, 0, cancellationToken).ConfigureAwait(false);
                return;
            }

            var entry = await client.GetAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (entry.IsDirectory && !entry.IsSymbolicLink)
            {
                await client.DeleteDirectoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SshErrorMapper.MapFileSystem(exception, fullPath, "delete");
        }
    }

    /// <inheritdoc />
    public async Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var sourceFullPath = ResolvePath(client, sourcePath);
        var destinationFullPath = ResolvePath(client, destinationPath);

        try
        {
            await client.RenameFileAsync(sourceFullPath, destinationFullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var mapped = SshErrorMapper.MapFileSystem(exception, sourceFullPath, "rename");
            throw new ProtocolException(
                $"Could not rename '{sourceFullPath}' to '{destinationFullPath}' over SFTP. {exception.Message}",
                mapped.Kind,
                exception);
        }
    }

    /// <inheritdoc />
    public async Task SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken)
    {
        if (!SupportsPermissions)
        {
            return;
        }

        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = ResolvePath(client, path);
        var mode = (short)(unixMode & 0xFFF);

        try
        {
            await Task.Run(() => client.ChangePermissions(fullPath, mode), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SshErrorMapper.MapFileSystem(exception, fullPath, "change permissions");
        }
    }

    /// <summary>Closes the SFTP connection; safe to call twice.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            DisposeClient(client);
        }

        try
        {
            _connectLock.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private async Task<SftpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var existing = _client;
        if (existing is not null && existing.IsConnected)
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            existing = _client;
            if (existing is not null && existing.IsConnected)
            {
                return existing;
            }

            if (existing is not null)
            {
                _client = null;
                DisposeClient(existing);
            }

            var client = await ConnectClientAsync(cancellationToken).ConfigureAwait(false);
            if (IsDisposed)
            {
                DisposeClient(client);
                throw new ObjectDisposedException(nameof(SftpFileSystem));
            }

            _client = client;
            _homeDirectory = TryGetWorkingDirectory(client);
            return client;
        }
        finally
        {
            ReleaseConnectLock();
        }
    }

    private async Task<SftpClient> ConnectClientAsync(CancellationToken cancellationToken)
    {
        var connectionInfo = _factory.CreateConnectionInfo(cancellationToken);
        var verifier = _factory.CreateVerifier(cancellationToken);
        var client = new SftpClient(connectionInfo)
        {
            OperationTimeout = SftpOperationTimeout,
        };

        if (_factory.KeepAliveInterval > TimeSpan.Zero)
        {
            client.KeepAliveInterval = _factory.KeepAliveInterval;
        }

        client.HostKeyReceived += verifier.Handle;
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException)
        {
            client.HostKeyReceived -= verifier.Handle;
            client.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            client.HostKeyReceived -= verifier.Handle;
            client.Dispose();
            if (verifier.RejectionReason is { Length: > 0 } reason)
            {
                throw new ProtocolException(reason, ProtocolFailureKind.HostKey, exception);
            }

            throw SshErrorMapper.Map(exception, $"connect SFTP to {_factory.Endpoint}");
        }
    }

    private async Task DeleteTreeAsync(SftpClient client, string path, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaximumDeleteDepth)
        {
            throw new ProtocolException(
                $"Refusing to delete '{path}': the directory tree is deeper than {MaximumDeleteDepth} levels.",
                ProtocolFailureKind.Configuration);
        }

        ISftpFile entry;
        try
        {
            entry = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            return;
        }

        if (!entry.IsDirectory || entry.IsSymbolicLink)
        {
            await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }

        var children = new List<string>();
        await foreach (var child in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (child.Name is "." or "..")
            {
                continue;
            }

            children.Add(child.FullName);
        }

        foreach (var child in children)
        {
            await DeleteTreeAsync(client, child, depth + 1, cancellationToken).ConfigureAwait(false);
        }

        await client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private string ResolvePath(SftpClient client, string? path)
    {
        var baseDirectory = _homeDirectory ?? TryGetWorkingDirectory(client) ?? "/";
        return PosixPath.Normalize(path, baseDirectory, baseDirectory);
    }

    private string? TryGetWorkingDirectory(SftpClient client)
    {
        try
        {
            var value = client.WorkingDirectory;
            return string.IsNullOrWhiteSpace(value) ? null : PosixPath.Normalize(value);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not read the remote working directory.");
            return null;
        }
    }

    private static RemoteFileEntry MapEntry(ISftpFile file)
    {
        var kind = file.IsSymbolicLink
            ? RemoteEntryKind.SymbolicLink
            : file.IsDirectory
                ? RemoteEntryKind.Directory
                : file.IsRegularFile
                    ? RemoteEntryKind.File
                    : RemoteEntryKind.Other;

        return new RemoteFileEntry
        {
            Name = file.Name,
            FullPath = PosixPath.Normalize(file.FullName),
            Kind = kind,
            Size = file.IsDirectory ? 0 : Math.Max(0, file.Length),
            ModifiedUtc = ToUtcOffset(file.LastWriteTimeUtc),
            UnixMode = ResolveUnixMode(file),
            Owner = FormatId(file.UserId),
            Group = FormatId(file.GroupId),
        };
    }

    private static int? ResolveUnixMode(ISftpFile file)
    {
        var mode = 0;
        if (file.OwnerCanRead) mode |= 0x100;
        if (file.OwnerCanWrite) mode |= 0x80;
        if (file.OwnerCanExecute) mode |= 0x40;
        if (file.GroupCanRead) mode |= 0x20;
        if (file.GroupCanWrite) mode |= 0x10;
        if (file.GroupCanExecute) mode |= 0x8;
        if (file.OthersCanRead) mode |= 0x4;
        if (file.OthersCanWrite) mode |= 0x2;
        if (file.OthersCanExecute) mode |= 0x1;

        var attributes = file.Attributes;
        if (attributes.IsUIDBitSet) mode |= 0x800;
        if (attributes.IsGroupIDBitSet) mode |= 0x400;
        if (attributes.IsStickyBitSet) mode |= 0x200;
        return mode;
    }

    private static string? FormatId(int id) =>
        id >= 0 ? id.ToString(CultureInfo.InvariantCulture) : null;

    private static DateTimeOffset ToUtcOffset(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    private static int CompareEntries(RemoteFileEntry left, RemoteFileEntry right)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeClient(SftpClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to disconnect the SFTP session cleanly.");
        }

        try
        {
            client.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to dispose the SFTP client cleanly.");
        }
    }

    private void ReleaseConnectLock()
    {
        try
        {
            _connectLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // The file system was disposed while a connection was being established.
        }
    }
}