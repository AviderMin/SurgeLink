using Microsoft.Extensions.Logging;
using Renci.SshNet;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh.Internal;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// SCP fallback used when the remote SSH server does not expose an SFTP
/// subsystem. Directory metadata is read with POSIX shell commands and files
/// are transferred through the SCP protocol; both travel over the same SSH
/// transport as the terminal session.
/// </summary>
internal sealed class ScpFileSystem : IRemoteFileSystem, IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private static readonly TimeSpan ScpOperationTimeout = TimeSpan.FromSeconds(60);

    private readonly SshConnectionFactory _factory;
    private readonly SshProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _transferLock = new(1, 1);

    private SshClient? _commandClient;
    private SshCommandRunner? _runner;
    private ScpClient? _scpClient;
    private string? _homeDirectory;
    private int _disposed;

    /// <summary>Creates the SCP fallback facade; connections are opened lazily.</summary>
    internal ScpFileSystem(SshConnectionFactory factory, SshProtocolOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool SupportsPermissions => true;

    /// <inheritdoc />
    public async Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken)
    {
        var home = _homeDirectory;
        if (!string.IsNullOrEmpty(home))
        {
            return home!;
        }

        var runner = await EnsureCommandRunnerAsync(cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(runner, ScpMetadata.BuildHomeCommand(), "resolve home directory", "/", cancellationToken)
            .ConfigureAwait(false);
        home = ScpMetadata.ParseHome(result.StandardOutput);

        _homeDirectory = home;
        return home;
    }

    /// <inheritdoc />
    public Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken) =>
        GetHomeDirectoryAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = NormalizePath(path);
        var runner = await EnsureCommandRunnerAsync(cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
                runner,
                ScpMetadata.BuildCommand(fullPath, directory: true),
                "list directory",
                fullPath,
                cancellationToken)
            .ConfigureAwait(false);

        var entries = ScpMetadata.Parse(fullPath, result.StandardOutput, directory: true).ToList();
        entries.Sort(CompareEntries);
        return entries;
    }

    /// <inheritdoc />
    public async Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = NormalizePath(path);
        var runner = await EnsureCommandRunnerAsync(cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
                runner,
                ScpMetadata.BuildCommand(fullPath, directory: false),
                "read file status",
                fullPath,
                cancellationToken)
            .ConfigureAwait(false);

        return ScpMetadata.Parse(fullPath, result.StandardOutput, directory: false)[0];
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = NormalizePath(path);
        return Task.FromResult<Stream>(new RemoteReadStream(async (target, token) =>
        {
            await _transferLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // SCP's synchronous Download can stay blocked in its private
                // pipe after Disconnect. Stream raw bytes through an owned,
                // cancellable exec channel instead; uploads still use SCP.
                var runner = await EnsureCommandRunnerAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    await RemoteFileAccess.RequireRegularFileAsync(this, fullPath, token).ConfigureAwait(false);
                    await runner.CopyToAsync("cat -- " + Quote(fullPath), target, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                catch (Exception exception)
                {
                    token.ThrowIfCancellationRequested();
                    throw SshErrorMapper.MapFileSystem(exception, fullPath, "download over SCP");
                }
            }
            finally { _transferLock.Release(); }
        }, cancellationToken));
    }

    /// <inheritdoc />
    public Task<Stream> OpenWriteAsync(
        string path,
        long? expectedSize,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = NormalizePath(path);
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            "surge-scp-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);

            return Task.FromResult<Stream>(new DeferredUploadStream(file, temporaryPath,
                token => UploadFileAsync(temporaryPath, fullPath, expectedSize, progress, token),
                cancellationToken));
        }
        catch (Exception exception)
        {
            TryDelete(temporaryPath);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            throw SshErrorMapper.MapFileSystem(exception, fullPath, "open for upload over SCP");
        }
    }

    /// <inheritdoc />
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = NormalizePath(path);
        var runner = await EnsureCommandRunnerAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(runner, "mkdir " + Quote(fullPath), "create directory", fullPath, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var fullPath = NormalizePath(path);
        if (fullPath == "/")
        {
            throw new ProtocolException("Refusing to delete the remote root directory.", ProtocolFailureKind.Configuration);
        }

        var runner = await EnsureCommandRunnerAsync(cancellationToken).ConfigureAwait(false);
        if (recursive)
        {
            await RunAsync(runner, "rm -rf " + Quote(fullPath), "delete directory tree", fullPath, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var entry = await StatAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var command = entry.IsDirectory ? "rmdir " : "rm -f ";
        await RunAsync(runner, command + Quote(fullPath), "delete", fullPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CommitWriteAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var source = NormalizePath(temporaryPath);
        var destination = NormalizePath(destinationPath);
        var runner = await EnsureCommandRunnerAsync(cancellationToken).ConfigureAwait(false);
        // -T prevents a directory or symlink-to-directory being treated as a folder
        // to move/link into. Missing server support must fail rather than overwrite.
        if (overwrite)
            await RemoteFileAccess.RequireRegularFileAsync(this, destination, cancellationToken).ConfigureAwait(false);
        var command = overwrite
            ? "mv -fT -- " + Quote(source) + " " + Quote(destination)
            : "ln -T -- " + Quote(source) + " " + Quote(destination) + " && rm -f -- " + Quote(source);
        await RunAsync(runner, command, "commit staged file", destination, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var source = NormalizePath(sourcePath);
        var destination = NormalizePath(destinationPath);
        var runner = await EnsureCommandRunnerAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(
                runner,
                BuildRenameCommand(source, destination),
                "rename",
                source,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string BuildRenameCommand(string source, string destination) =>
        "mv -nT -- " + Quote(source) + " " + Quote(destination)
        + " && test ! -e " + Quote(source) + " && test ! -L " + Quote(source);

    /// <inheritdoc />
    public async Task SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken)
    {
        var fullPath = NormalizePath(path);
        var mode = Convert.ToString(unixMode & 0xFFF, 8).PadLeft(3, '0');
        var runner = await EnsureCommandRunnerAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(
                runner,
                "chmod " + mode + " " + Quote(fullPath),
                "change permissions",
                fullPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        SshClient? commandClient;
        ScpClient? scpClient;
        lock (_lifecycleGate)
        {
            if (_disposed != 0) return ValueTask.CompletedTask;
            Volatile.Write(ref _disposed, 1);
            commandClient = Interlocked.Exchange(ref _commandClient, null);
            scpClient = Interlocked.Exchange(ref _scpClient, null);
            _runner = null;
        }
        if (commandClient is not null)
        {
            DisposeClient(commandClient);
        }

        if (scpClient is not null)
        {
            DisposeClient(scpClient);
        }

        // Do not dispose semaphores still referenced by queued/in-flight operations.
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    internal async Task UploadFileAsync(
        string localPath,
        string remotePath,
        long? expectedSize,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _transferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var client = await EnsureScpClientAsync(cancellationToken).ConfigureAwait(false);
                using var cancelTransfer = cancellationToken.Register(() =>
                {
                    try { client.Disconnect(); } catch (Exception) { }
                });
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new TransferProgress(0, expectedSize, remotePath));
                await Task.Run(
                        () => client.Upload(new FileInfo(localPath), remotePath),
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // SCP inherits a local file mode on some servers. The payload is
                // inside a private staging directory; narrow its mode before commit.
                await SetPermissionsAsync(remotePath, 0x180, cancellationToken).ConfigureAwait(false);
                progress?.Report(new TransferProgress(new FileInfo(localPath).Length, expectedSize, remotePath));
            }
            finally
            {
                _transferLock.Release();
            }
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exception is OperationCanceledException)
            {
                throw;
            }

            throw SshErrorMapper.MapFileSystem(exception, remotePath, "upload over SCP");
        }
    }

    private async Task<SshCommandRunner> EnsureCommandRunnerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var existing = _runner;
        if (existing is not null && _commandClient?.IsConnected == true)
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            existing = _runner;
            if (existing is not null && _commandClient?.IsConnected == true)
            {
                return existing;
            }

            var commandClient = Interlocked.Exchange(ref _commandClient, null);
            if (commandClient is not null)
            {
                DisposeClient(commandClient);
            }

            using var budget = _factory.CreateConnectBudget(cancellationToken);
            var connectionInfo = _factory.CreateConnectionInfo(budget.Token, budget);
            var verifier = _factory.CreateVerifier(budget.Token, budget);
            var client = new SshClient(connectionInfo);
            client.HostKeyReceived += verifier.Handle;

            try
            {
                await budget.ConnectAsync(client).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                client.HostKeyReceived -= verifier.Handle;
                client.Dispose();
                cancellationToken.ThrowIfCancellationRequested();

                if (budget.IsCancellationRequested)
                    throw new ProtocolException($"Connecting SCP file commands to {_factory.Endpoint} timed out.", ProtocolFailureKind.Timeout, exception);

                if (verifier.RejectionReason is { Length: > 0 } reason)
                {
                    throw new ProtocolException(reason, ProtocolFailureKind.HostKey, exception);
                }

                throw SshErrorMapper.Map(exception, $"connect SCP file commands to {_factory.Endpoint}");
            }

            var runner = new SshCommandRunner(client, _options.Encoding, _options.CommandTimeout);
            lock (_lifecycleGate)
            {
                if (_disposed == 0 && !cancellationToken.IsCancellationRequested)
                {
                    _commandClient = client;
                    _runner = runner;
                    return runner;
                }
            }
            DisposeClient(client);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(ScpFileSystem));
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<ScpClient> EnsureScpClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var existing = _scpClient;
        if (existing is not null && existing.IsConnected)
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            existing = _scpClient;
            if (existing is not null && existing.IsConnected)
            {
                return existing;
            }

            var old = Interlocked.Exchange(ref _scpClient, null);
            if (old is not null)
            {
                DisposeClient(old);
            }

            using var budget = _factory.CreateConnectBudget(cancellationToken);
            var connectionInfo = _factory.CreateConnectionInfo(budget.Token, budget);
            var verifier = _factory.CreateVerifier(budget.Token, budget);
            var client = new ScpClient(connectionInfo, RemotePathTransformation.ShellQuote)
            {
                OperationTimeout = ScpOperationTimeout,
            };
            client.HostKeyReceived += verifier.Handle;

            try
            {
                await budget.ConnectAsync(client).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                client.HostKeyReceived -= verifier.Handle;
                client.Dispose();
                cancellationToken.ThrowIfCancellationRequested();

                if (budget.IsCancellationRequested)
                    throw new ProtocolException($"Connecting SCP transfer to {_factory.Endpoint} timed out.", ProtocolFailureKind.Timeout, exception);

                if (verifier.RejectionReason is { Length: > 0 } reason)
                {
                    throw new ProtocolException(reason, ProtocolFailureKind.HostKey, exception);
                }

                throw SshErrorMapper.Map(exception, $"connect SCP transfer to {_factory.Endpoint}");
            }

            lock (_lifecycleGate)
            {
                if (_disposed == 0 && !cancellationToken.IsCancellationRequested)
                {
                    _scpClient = client;
                    return client;
                }
            }
            DisposeClient(client);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(ScpFileSystem));
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static async Task<SshCommandResult> RunAsync(
        SshCommandRunner runner,
        string command,
        string operation,
        string path,
        CancellationToken cancellationToken)
    {
        SshCommandResult result;
        try
        {
            result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw SshErrorMapper.Map(exception, $"{operation} '{path}' over SCP");
        }

        if (!result.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The remote command failed."
                : result.StandardError.Trim();
            throw new ProtocolException(
                $"Could not {operation} '{path}' over SCP. {detail}",
                ProtocolFailureKind.ServerRejected);
        }

        return result;
    }

    private static int CompareEntries(RemoteFileEntry left, RemoteFileEntry right)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizePath(string path) => PosixPath.Resolve(path);

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DisposeClient(BaseClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
        catch (Exception)
        {
            // Best effort during teardown.
        }

        try
        {
            client.Dispose();
        }
        catch (Exception)
        {
            // Best effort during teardown.
        }
    }

}
