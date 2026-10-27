using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace SurgeLink.Core.Files;

/// <summary>
/// Session-scoped sequential transfer queue. Jobs run one after another so a
/// slow link is not saturated by parallel copies; each job reports progress
/// through the <see cref="IProgress{T}"/> the protocol hands back.
/// </summary>
/// <remarks>
/// The queue owns no component state. It exposes an immutable
/// <see cref="Snapshot"/> plus a lightweight <see cref="Changed"/> notification,
/// so a view can unmount and later re-subscribe without losing progress or
/// cancel controls. A tab owns one queue and disposes it when the session
/// closes; <see cref="DisposeAsync"/> cancels every active and queued job and
/// waits (bounded) for the worker before the protocol session is disposed.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The type is a transfer queue; the domain name is clearer than the analyzer's suggested alternatives.")]
public sealed class FileTransferQueue : IAsyncDisposable
{
    private const int BufferSize = 81_920;
    private static readonly TimeSpan SnapshotThrottle = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<TransferItem> _items = new();
    private readonly Queue<(TransferItem Item, IRemoteFileSystem FileSystem)> _pending = new();
    private readonly ILogger? _logger;
    private TransferItem[] _snapshot = Array.Empty<TransferItem>();
    private Task? _worker;
    private DateTimeOffset _lastPublish = DateTimeOffset.MinValue;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;

    /// <summary>Creates a queue; the logger is optional and used for shutdown diagnostics.</summary>
    public FileTransferQueue(ILogger? logger = null) => _logger = logger;

    /// <summary>Raised after the snapshot changes; may fire on a worker thread.</summary>
    public event Action? Changed;

    /// <summary>Raised once per job after it completes, fails or is cancelled; may fire on a worker thread.</summary>
    public event Action<TransferItem>? TransferFinished;

    /// <summary>Current immutable snapshot, safe to read from any thread.</summary>
    public IReadOnlyList<TransferItem> Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Subscription helper shaped for <c>UseExternalStore</c>.</summary>
    public Action Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var subscriptionGate = new object();
        var active = true;
        void Notify()
        {
            lock (subscriptionGate) { if (active) { listener(); } }
        }
        Changed += Notify;
        return () =>
        {
            lock (subscriptionGate) { active = false; }
            Changed -= Notify;
        };
    }

    /// <summary>Adds a job and starts the worker when it is idle.</summary>
    public void Enqueue(TransferItem item, IRemoteFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(fileSystem);

        lock (_gate)
        {
            if (_disposed)
            {
                item.Status = TransferStatus.Failed;
                item.Error = "The session transfer queue is closed. Reconnect before starting a transfer.";
                throw new InvalidOperationException(item.Error);
            }

            _items.Add(item);
            _pending.Enqueue((item, fileSystem));

            // Enqueue and worker start happen under one lock so a job can never be
            // queued between the worker's "queue is empty" check and its exit.
            if (_worker is null || _worker.IsCompleted)
            {
                _worker = Task.Run(ProcessAsync);
            }
        }

        Publish(force: true);
    }

    /// <summary>Cancels one transfer; a queued transfer is cancelled before it starts.</summary>
    public void Cancel(string id)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(entry => entry.Id == id);
            if (item is null || !item.IsActive)
            {
                return;
            }

            if (item.Status == TransferStatus.Queued)
            {
                item.Status = TransferStatus.Cancelled;
            }

            item.Cancellation.Cancel();
        }

        Publish(force: true);
    }

    /// <summary>Drops completed, failed and cancelled rows from the panel.</summary>
    public void ClearFinished()
    {
        lock (_gate)
        {
            _items.RemoveAll(item => !item.IsActive);
        }

        Publish(force: true);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        var start = false;
        lock (_gate)
        {
            if (_disposeCompletion is null)
            {
                _disposed = true;
                _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                start = true;
            }
            completion = _disposeCompletion;
        }
        // Cancellation and notifications may call back into the queue.
        if (start) { _ = CompleteDisposalAsync(completion); }
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private async Task DisposeCoreAsync()
    {
        Task? worker;
        TransferItem[] items;
        TransferItem[] pending;
        lock (_gate)
        {
            items = _items.ToArray();
            pending = _pending.Select(job => job.Item).ToArray();
            _pending.Clear();
            worker = _worker;
        }

        foreach (var item in items)
        {
            if (item.Status == TransferStatus.Queued) { item.Status = TransferStatus.Cancelled; }
            item.Cancellation.Cancel();
        }

        Publish(force: true);
        foreach (var item in pending) { RaiseTransferFinished(item); }

        var workerCompleted = true;
        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                workerCompleted = false;
                _logger?.LogWarning(
                    "A transfer worker did not stop within {Seconds}s during queue shutdown.",
                    ShutdownTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Transfer worker faulted during queue shutdown.");
            }
        }

        if (workerCompleted)
        {
            foreach (var item in items)
            {
                item.Cancellation.Dispose();
            }
        }

        lock (_gate)
        {
            _items.Clear();
            _snapshot = Array.Empty<TransferItem>();
        }

        RaiseChanged();
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            (TransferItem Item, IRemoteFileSystem FileSystem) job;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _worker = null;
                    return;
                }

                job = _pending.Dequeue();
            }

            await RunAsync(job.Item, job.FileSystem).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(TransferItem item, IRemoteFileSystem fileSystem)
    {
        if (item.Status == TransferStatus.Cancelled || item.Cancellation.IsCancellationRequested)
        {
            SetStatus(item, TransferStatus.Cancelled);
            RaiseTransferFinished(item);
            return;
        }

        SetStatus(item, TransferStatus.Running);

        try
        {
            var token = item.Cancellation.Token;
            if (item.Direction == TransferDirection.Upload)
            {
                if (item.IsDirectory) { await UploadDirectoryAsync(item, fileSystem, token).ConfigureAwait(false); }
                else { await UploadAsync(item, fileSystem, token).ConfigureAwait(false); }
            }
            else
            {
                await DownloadAsync(item, fileSystem, token).ConfigureAwait(false);
            }

            if (item.TotalBytes <= 0)
            {
                item.TotalBytes = item.DoneBytes;
            }

            SetStatus(item, TransferStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome, not a failure banner.
            SetStatus(item, TransferStatus.Cancelled);
        }
        catch (Exception ex)
        {
            SetStatus(item, TransferStatus.Failed, ex.Message);
        }
        finally
        {
            RaiseTransferFinished(item);
        }
    }

    private async Task UploadAsync(TransferItem item, IRemoteFileSystem fileSystem, CancellationToken token,
        string? localPath = null, string? remotePath = null, long offset = 0)
    {
        if (item.LocalPath is null)
        {
            throw new InvalidOperationException("Upload has no local source.");
        }

        await using var source = new FileStream(localPath ?? item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        var destination = remotePath ?? item.RemotePath;
        await RemoteFileWrite.WriteAsync(fileSystem, destination, source.Length,
            null, item.OverwritePaths.Contains(destination), async (target, writeToken) =>
            {
                var buffer = new byte[BufferSize];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, writeToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), writeToken).ConfigureAwait(false);
                    done += read;
                    SetDone(item, offset + done);
                }
            }, token).ConfigureAwait(false);
    }

    private async Task UploadDirectoryAsync(TransferItem item, IRemoteFileSystem fileSystem, CancellationToken token)
    {
        if (item.LocalPath is null) { throw new InvalidOperationException("Upload has no local source."); }
        var directories = new List<(string Local, string Remote)>();
        var files = new List<(string Local, string Remote, long Size)>();
        var pending = new Stack<(string Local, string Remote)>();
        pending.Push((item.LocalPath, item.RemotePath));
        long total = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory.Local) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Folder uploads cannot include symbolic links or junctions.");
            }
            directories.Add(directory);
            foreach (var entry in new DirectoryInfo(directory.Local).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Folder uploads cannot include symbolic links or junctions: " + entry.FullName);
                }
                var remote = directory.Remote.TrimEnd('/') + "/" + entry.Name;
                if (entry is DirectoryInfo) { pending.Push((entry.FullName, remote)); }
                else if (entry is FileInfo file)
                {
                    files.Add((file.FullName, remote, file.Length));
                    total += file.Length;
                }
            }
        }
        item.TotalBytes = total;
        Publish(force: true);
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            var split = directory.Remote.LastIndexOf('/');
            var parent = split <= 0 ? "/" : directory.Remote[..split];
            var name = directory.Remote[(split + 1)..];
            var listing = await fileSystem.ListAsync(parent, token).ConfigureAwait(false);
            var existing = listing.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
            if (existing is null) { await fileSystem.CreateDirectoryAsync(directory.Remote, token).ConfigureAwait(false); }
            else if (!existing.IsDirectory) { throw new IOException("Destination is not a directory: " + directory.Remote); }
        }
        long offset = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            await UploadAsync(item, fileSystem, token, file.Local, file.Remote, offset).ConfigureAwait(false);
            offset = item.DoneBytes;
        }
    }

    private async Task DownloadAsync(TransferItem item, IRemoteFileSystem fileSystem, CancellationToken token)
    {
        if (item.LocalPath is null) { throw new InvalidOperationException("Download has no local destination."); }
        var directory = Path.GetDirectoryName(Path.GetFullPath(item.LocalPath))!;
        // Validate again at the write boundary, including jobs created outside the UI.
        var destination = LocalDownloadPath.Resolve(directory, item.Name);
        if (!string.Equals(destination, Path.GetFullPath(item.LocalPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The download destination does not match its file name.");
        }
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".surge-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (item.IsDirectory)
            {
                await DownloadDirectoryZipAsync(item, fileSystem, temporary, token).ConfigureAwait(false);
            }
            else
            {
                await RemoteFileAccess.RequireRegularFileAsync(fileSystem, item.RemotePath, token).ConfigureAwait(false);
                await using var source = await fileSystem.OpenReadAsync(item.RemotePath, token).ConfigureAwait(false);
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    var buffer = new byte[BufferSize];
                    long done = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        done += read;
                        SetDone(item, done);
                    }
                    await target.FlushAsync(token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            // Refuse concurrent target creation instead of silently overwriting user data.
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadDirectoryZipAsync(TransferItem item, IRemoteFileSystem fileSystem,
        string temporary, CancellationToken token)
    {
        var rootName = item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? item.Name[..^4] : item.Name;
        _ = LocalDownloadPath.Resolve(Path.GetTempPath(), rootName);
        var directories = new List<string> { rootName + "/" };
        var files = new List<(string Remote, string Archive, long Size)>();
        var pending = new Stack<(string Remote, string Archive)>();
        pending.Push((item.RemotePath, rootName));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootName };
        long total = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            // A queued folder or child directory may have been replaced since
            // it was selected/listed. Do not follow it outside the download tree.
            var current = await fileSystem.StatAsync(directory.Remote, token).ConfigureAwait(false);
            if (current.Kind != RemoteEntryKind.Directory)
                throw new IOException("ZIP downloads require a directory: " + directory.Remote);
            var listing = await fileSystem.ListAsync(directory.Remote, token).ConfigureAwait(false);
            foreach (var entry in listing)
            {
                token.ThrowIfCancellationRequested();
                _ = LocalDownloadPath.Resolve(Path.GetTempPath(), entry.Name);
                var archivePath = directory.Archive + "/" + entry.Name;
                if (!names.Add(archivePath)) { throw new IOException("Duplicate archive path: " + archivePath); }
                // Construct child paths ourselves; listing metadata must not redirect traversal.
                var remote = directory.Remote.TrimEnd('/') + "/" + entry.Name;
                if (entry.IsDirectory)
                {
                    directories.Add(archivePath + "/");
                    pending.Push((remote, archivePath));
                }
                else if (entry.Kind == RemoteEntryKind.File)
                {
                    files.Add((remote, archivePath, entry.Size));
                    total = checked(total + Math.Max(0, entry.Size));
                }
                else { throw new IOException("ZIP downloads cannot include symbolic links or special files: " + remote); }
            }
        }
        item.TotalBytes = total;
        Publish(force: true);
        await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            archive.CreateEntry(directory);
        }
        var buffer = new byte[BufferSize];
        long done = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(file.Archive, CompressionLevel.Optimal);
            await RemoteFileAccess.RequireRegularFileAsync(fileSystem, file.Remote, token).ConfigureAwait(false);
            await using var source = await fileSystem.OpenReadAsync(file.Remote, token).ConfigureAwait(false);
            await using var target = entry.Open();
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                done += read;
                SetDone(item, done);
            }
        }
    }

    private void SetDone(TransferItem item, long done)
    {
        lock (_gate)
        {
            if (done > item.DoneBytes)
            {
                item.DoneBytes = done;
            }
        }

        Publish();
    }

    private void SetStatus(TransferItem item, TransferStatus status, string? error = null)
    {
        lock (_gate)
        {
            item.Status = status;
            item.Error = error;
        }

        Publish(force: true);
    }

    /// <summary>Publishes an immutable snapshot, throttled while bytes stream.</summary>
    private void Publish(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!force && now - _lastPublish < SnapshotThrottle)
            {
                return;
            }

            _lastPublish = now;
            _snapshot = _items.ToArray();
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handlers) { return; }
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Transfer change subscriber failed."); }
        }
    }

    private void RaiseTransferFinished(TransferItem item)
    {
        if (TransferFinished is not { } handlers) { return; }
        foreach (Action<TransferItem> handler in handlers.GetInvocationList())
        {
            try { handler(item); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Transfer completion subscriber failed."); }
        }
    }
}
