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
        Changed += listener;
        return () => Changed -= listener;
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
                item.Status = TransferStatus.Cancelled;
                return;
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
    public async ValueTask DisposeAsync()
    {
        Task? worker;
        TransferItem[] items;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            items = _items.ToArray();
            _pending.Clear();
            worker = _worker;
        }

        foreach (var item in items)
        {
            item.Cancellation.Cancel();
        }

        Publish(force: true);

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
            TransferFinished?.Invoke(item);
            return;
        }

        SetStatus(item, TransferStatus.Running);

        try
        {
            var token = item.Cancellation.Token;
            if (item.Direction == TransferDirection.Upload)
            {
                await UploadAsync(item, fileSystem, token).ConfigureAwait(false);
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
            TransferFinished?.Invoke(item);
        }
    }

    private async Task UploadAsync(TransferItem item, IRemoteFileSystem fileSystem, CancellationToken token)
    {
        if (item.LocalPath is null)
        {
            throw new InvalidOperationException("Upload has no local source.");
        }

        await using var source = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        // The protocol reports progress as data flows; mirror it into the row so
        // protocols that never report still show movement.
        var protocolProgress = new Progress<TransferProgress>(progress => SetDone(item, progress.BytesTransferred));

        await using var target = await fileSystem
            .OpenWriteAsync(item.RemotePath, item.TotalBytes > 0 ? item.TotalBytes : null, protocolProgress, token)
            .ConfigureAwait(false);

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

    private async Task DownloadAsync(TransferItem item, IRemoteFileSystem fileSystem, CancellationToken token)
    {
        if (item.LocalPath is null)
        {
            throw new InvalidOperationException("Download has no local destination.");
        }

        var directory = Path.GetDirectoryName(item.LocalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = await fileSystem.OpenReadAsync(item.RemotePath, token).ConfigureAwait(false);
        await using var target = new FileStream(item.LocalPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

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

    private void RaiseChanged() => Changed?.Invoke();
}
