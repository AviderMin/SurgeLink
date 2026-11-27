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
    // Shared with the copy recursion so a tree the copier may create is a tree this
    // connection is also allowed to remove when the copy fails mid-way.
    internal const int MaximumDeleteDepth = RemoteFileLimits.MaximumDepth;
    private static readonly TimeSpan SftpOperationTimeout = TimeSpan.FromSeconds(60);

    private readonly SshConnectionFactory _factory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _lifecycleGate = new();

    private SftpClient? _client;
    private string? _homeDirectory;
    private int _disposed;
    private long _directoryListRequests;

    /// <summary>READDIR requests issued by this connection; lets tests observe scan cost.</summary>
    internal long DirectoryListRequests => Interlocked.Read(ref _directoryListRequests);

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
            Interlocked.Increment(ref _directoryListRequests);
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
            return await ResolveEntryNoFollowAsync(client, fullPath, cancellationToken).ConfigureAwait(false);
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
            // Re-check the current type with a single no-follow LSTAT immediately before
            // OPEN. A file replaced by a symbolic link or a FIFO after any earlier listing
            // is refused here instead of being followed or opened (which could block).
            RemoteFileAccess.RequireRegularFile(
                await ResolveEntryNoFollowAsync(client, fullPath, cancellationToken).ConfigureAwait(false));
            return await OpenReadCoreAsync(client, fullPath, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Resolves one path without following its final link. Prefers a single LSTAT request
    /// and falls back to the parent listing where the transport does not expose LSTAT.
    /// </summary>
    private async Task<RemoteFileEntry> ResolveEntryNoFollowAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        var entryPath = path.Length > 1 ? path.TrimEnd('/') : path;
        if (entryPath.Length == 0)
        {
            entryPath = "/";
        }

        var name = entryPath == "/" ? "/" : entryPath[(entryPath.LastIndexOf('/') + 1)..];
        // A final "." or ".." must be interpreted by the server; keep the listing-based
        // spelling that delete and rename already rely on for those paths.
        if (SftpNoFollow.IsAvailable && name is not ("." or ".."))
        {
            var attributes = await SftpNoFollow.TryLStatAsync(client, entryPath, cancellationToken).ConfigureAwait(false);
            if (attributes is not null)
            {
                return MapAttributes(entryPath, name, attributes);
            }
        }

        return MapEntry(await GetEntryNoFollowAsync(client, path, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Stream> OpenReadCoreAsync(SftpClient client, string fullPath, CancellationToken cancellationToken) =>
        await client.OpenAsync(fullPath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);

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
        if (IsRootPath(fullPath))
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

            var entry = await GetEntryNoFollowAsync(client, fullPath, cancellationToken).ConfigureAwait(false);
            // Delete the entry object obtained from the parent directory listing.
            // SftpClient.DeleteFileAsync canonicalizes the full path first, which
            // would remove the link target instead of the link itself.
            await entry.DeleteAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task CommitWriteAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var source = ResolvePath(client, temporaryPath);
        var destination = ResolvePath(client, destinationPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite)
            {
                RemoteFileAccess.RequireRegularFile(
                    await ResolveEntryNoFollowAsync(client, destination, cancellationToken).ConfigureAwait(false));
            }
            // Send the literal paths. REALPATH can turn a destination symlink
            // into its referent before the server has a chance to replace it.
            await SftpNoFollow.RenameAsync(client, source, destination, overwrite, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ProtocolException))
        {
            throw SshErrorMapper.MapFileSystem(exception, destination, "commit");
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
            if (SftpNoFollow.CanRename(overwrite: false))
            {
                await SftpNoFollow.RenameAsync(client, sourceFullPath, destinationFullPath, overwrite: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Fallback when the private session accessor is unavailable: resolve via parent
                // listing so a final symlink is renamed rather than its target.
                var entry = await GetEntryNoFollowAsync(client, sourceFullPath, cancellationToken).ConfigureAwait(false);
                await Task.Run(() => entry.MoveTo(destinationFullPath), cancellationToken).ConfigureAwait(false);
            }
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
        var mode = ToSftpPermissionMode(unixMode);

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
        SftpClient? client;
        lock (_lifecycleGate)
        {
            if (_disposed != 0) return ValueTask.CompletedTask;
            Volatile.Write(ref _disposed, 1);
            client = _client;
            _client = null;
        }
        if (client is not null)
        {
            DisposeClient(client);
        }

        // Waiting/in-flight connects still own this semaphore; let it be collected with the facade.

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal async Task InitializeAsync(CancellationToken cancellationToken) =>
        _ = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

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
            lock (_lifecycleGate)
            {
                if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                {
                    _client = client;
                    _homeDirectory = TryGetWorkingDirectory(client);
                    return client;
                }
            }
            DisposeClient(client);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(SftpFileSystem));
        }
        finally
        {
            ReleaseConnectLock();
        }
    }

    private async Task<SftpClient> ConnectClientAsync(CancellationToken cancellationToken)
    {
        using var budget = _factory.CreateConnectBudget(cancellationToken);
        var connectionInfo = _factory.CreateConnectionInfo(budget.Token, budget);
        HostKeyVerifier verifier;
        SftpClient client;
        try
        {
            verifier = _factory.CreateVerifier(budget.Token, budget);
            client = new SftpClient(connectionInfo)
            {
                OperationTimeout = SftpOperationTimeout,
            };
        }
        catch
        {
            SshConnectionFactory.DisposeAuthenticationMethods(connectionInfo.AuthenticationMethods);
            throw;
        }

        if (_factory.KeepAliveInterval > TimeSpan.Zero)
        {
            client.KeepAliveInterval = _factory.KeepAliveInterval;
        }

        client.HostKeyReceived += verifier.Handle;
        try
        {
            await budget.ConnectAsync(client).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException exception)
        {
            client.HostKeyReceived -= verifier.Handle;
            client.Dispose();
            if (!cancellationToken.IsCancellationRequested)
                throw new ProtocolException($"Connecting SFTP to {_factory.Endpoint} timed out.", ProtocolFailureKind.Timeout, exception);
            throw;
        }
        catch (Exception exception)
        {
            client.HostKeyReceived -= verifier.Handle;
            client.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested)
                throw new ProtocolException($"Connecting SFTP to {_factory.Endpoint} timed out.", ProtocolFailureKind.Timeout, exception);
            if (verifier.RejectionReason is { Length: > 0 } reason)
            {
                throw new ProtocolException(reason, ProtocolFailureKind.HostKey, exception);
            }

            if (SshFileSystem.IsSftpUnavailable(exception)) throw new SftpUnavailableException(exception);
            throw SshErrorMapper.Map(exception, $"connect SFTP to {_factory.Endpoint}");
        }
    }

    private async Task DeleteTreeAsync(SftpClient client, string path, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ISftpFile entry;
        try
        {
            entry = await GetEntryNoFollowAsync(client, path, cancellationToken).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            return;
        }

        await DeleteTreeEntryAsync(client, entry, depth, cancellationToken).ConfigureAwait(false);
    }

    // Reuses the ISftpFile instances returned by the parent's READDIR instead of
    // re-listing each parent for every child. Each entry keeps the link's own
    // FullName/attributes, so DeleteAsync sends REMOVE/RMDIR without REALPATH and
    // only genuine directories are enumerated.
    private async Task DeleteTreeEntryAsync(
        SftpClient client,
        ISftpFile entry,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!entry.IsDirectory || entry.IsSymbolicLink)
        {
            await DeleteEntryIgnoringMissingAsync(entry, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (depth > MaximumDeleteDepth)
        {
            throw new ProtocolException(
                $"Refusing to delete '{entry.FullName}': the directory tree is deeper than {MaximumDeleteDepth} levels.",
                ProtocolFailureKind.Configuration);
        }

        var children = new List<ISftpFile>();
        var entryFullName = entry.FullName;
        if (entryFullName.EndsWith("/.", StringComparison.Ordinal)
            || entryFullName.EndsWith("/..", StringComparison.Ordinal))
        {
            // READDIR/REALPATH report the resolved directory without the final
            // dot segment; compare children against that resolved spelling.
            entryFullName = PosixPath.Normalize(entryFullName);
        }

        var expectedPrefix = entryFullName.EndsWith('/') ? entryFullName : entryFullName + "/";
        try
        {
            await foreach (var child in client.ListDirectoryAsync(entry.FullName, cancellationToken).ConfigureAwait(false))
            {
                if (child.Name is "." or "..")
                {
                    continue;
                }

                // SFTP names are single path components. Reject malformed server
                // replies instead of letting an embedded separator escape the
                // directory prefix checked below.
                if (child.Name.Contains('/') || child.Name.Contains('\0'))
                {
                    throw new ProtocolException(
                        $"Refusing to delete '{entry.FullName}': the server returned an invalid child name.",
                        ProtocolFailureKind.Configuration);
                }

                // ListDirectoryAsync canonicalizes the directory path. If the
                // entry was swapped for a link after the parent READDIR, the
                // children will be reported under the link target instead.
                if (!child.FullName.StartsWith(expectedPrefix, StringComparison.Ordinal))
                {
                    throw new ProtocolException(
                        $"Refusing to delete '{entry.FullName}': the directory was replaced while being enumerated.",
                        ProtocolFailureKind.Configuration);
                }

                children.Add(child);
            }
        }
        catch (SftpPathNotFoundException)
        {
            // The directory disappeared after the parent READDIR; it is already deleted.
            return;
        }

        foreach (var child in children)
        {
            await DeleteTreeEntryAsync(client, child, depth + 1, cancellationToken).ConfigureAwait(false);
        }

        // RequestRmDir on the directory's own FullName; DeleteDirectoryAsync would REALPATH first.
        // A concurrent delete between READDIR and RMDIR is also success for this tree delete.
        await DeleteEntryIgnoringMissingAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteEntryIgnoringMissingAsync(ISftpFile entry, CancellationToken cancellationToken)
    {
        try
        {
            await entry.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            // The entry was removed concurrently after the parent listed it.
        }
    }

    private string ResolvePath(SftpClient client, string? path)
    {
        var baseDirectory = _homeDirectory ?? TryGetWorkingDirectory(client) ?? "/";
        return PosixPath.Resolve(path, baseDirectory, baseDirectory);
    }

    private string? TryGetWorkingDirectory(SftpClient client)
    {
        try
        {
            var value = client.WorkingDirectory;
            return string.IsNullOrEmpty(value) ? null : PosixPath.Resolve(value);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not read the remote working directory.");
            return null;
        }
    }

    // SSH.NET Get/GetAttributes first REALPATH the complete name, following a
    // final symbolic link before LSTAT. READDIR of the parent preserves its type.
    private async Task<ISftpFile> GetEntryNoFollowAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        // Resolve a trailing separator away without normalising dot segments:
        // those must still be interpreted by the server, including through links.
        var entryPath = path.Length > 1 ? path.TrimEnd('/') : path;
        if (entryPath.Length == 0) { entryPath = "/"; }
        if (entryPath == "/") { return await client.GetAsync(entryPath, cancellationToken).ConfigureAwait(false); }
        var separator = entryPath.LastIndexOf('/');
        var parent = separator == 0 ? "/" : entryPath[..separator];
        var name = entryPath[(separator + 1)..];

        // A final "." or ".." cannot be matched to a READDIR name. Let the
        // server resolve its normal path semantics; a lexical rewrite would be
        // wrong when an earlier component is a symbolic link.
        if (name is "." or "..")
        {
            var resolved = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (IsRootPath(resolved.FullName))
            {
                throw new ProtocolException(
                    $"Refusing to inspect '{path}': it resolves to the remote root directory.",
                    ProtocolFailureKind.Configuration);
            }

            return resolved;
        }

        try
        {
            Interlocked.Increment(ref _directoryListRequests);
            await foreach (var entry in client.ListDirectoryAsync(parent, cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal)) { return entry; }
            }
        }
        catch (SftpPermissionDeniedException)
        {
            // A parent may grant wx (search/write) without r (list). A known
            // non-link path can still be inspected through REALPATH + LSTAT;
            // reject the fallback when REALPATH proves the requested path is a
            // link or resolves somewhere else, because deleting/renaming it
            // through the canonical target would be unsafe.
            var resolved = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            var expected = PosixPath.Normalize(path);
            if (!string.Equals(resolved.FullName.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.Ordinal))
            {
                throw new ProtocolException(
                    $"Cannot safely inspect '{path}': its parent is not readable and the path resolves elsewhere.",
                    ProtocolFailureKind.Configuration);
            }

            return resolved;
        }

        throw new SftpPathNotFoundException("Remote path not found: " + entryPath);
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
            FullPath = PosixPath.Resolve(file.FullName),
            Kind = kind,
            Size = file.IsDirectory ? 0 : Math.Max(0, file.Length),
            ModifiedUtc = ToUtcOffset(file.LastWriteTimeUtc),
            UnixMode = ResolveUnixMode(file),
            Owner = FormatId(file.UserId),
            Group = FormatId(file.GroupId),
        };
    }

    private static RemoteFileEntry MapAttributes(string fullPath, string name, SftpFileAttributes attributes) =>
        new()
        {
            Name = name,
            FullPath = PosixPath.Resolve(fullPath),
            Kind = attributes.IsSymbolicLink
                ? RemoteEntryKind.SymbolicLink
                : attributes.IsDirectory
                    ? RemoteEntryKind.Directory
                    : attributes.IsRegularFile
                        ? RemoteEntryKind.File
                        : RemoteEntryKind.Other,
            Size = attributes.IsDirectory ? 0 : Math.Max(0, attributes.Size),
            ModifiedUtc = ToUtcOffset(attributes.LastWriteTimeUtc),
            UnixMode = ResolveUnixMode(attributes),
            Owner = FormatId(attributes.UserId),
            Group = FormatId(attributes.GroupId),
        };

    private static int? ResolveUnixMode(ISftpFile file) => ResolveUnixMode(file.Attributes);

    private static int? ResolveUnixMode(SftpFileAttributes attributes)
    {
        var mode = 0;
        if (attributes.OwnerCanRead) mode |= 0x100;
        if (attributes.OwnerCanWrite) mode |= 0x80;
        if (attributes.OwnerCanExecute) mode |= 0x40;
        if (attributes.GroupCanRead) mode |= 0x20;
        if (attributes.GroupCanWrite) mode |= 0x10;
        if (attributes.GroupCanExecute) mode |= 0x8;
        if (attributes.OthersCanRead) mode |= 0x4;
        if (attributes.OthersCanWrite) mode |= 0x2;
        if (attributes.OthersCanExecute) mode |= 0x1;

        if (attributes.IsUIDBitSet) mode |= 0x800;
        if (attributes.IsGroupIDBitSet) mode |= 0x400;
        if (attributes.IsStickyBitSet) mode |= 0x200;
        return mode;
    }

    /// <summary>True for paths that lexically resolve to the root; used before any recursive delete.</summary>
    internal static bool IsRootPath(string path) => PosixPath.Normalize(path) == "/";

    // SSH.NET accepts octal *digits* (700), whereas our contract carries the
    // permission bit mask (0x1C0 / decimal 448).
    internal static short ToSftpPermissionMode(int unixMode) =>
        short.Parse(Convert.ToString(unixMode & 0xFFF, 8), CultureInfo.InvariantCulture);

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
