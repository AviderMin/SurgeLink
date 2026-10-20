using SurgeLink.Core.Files;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Direction of a queued transfer.</summary>
internal enum TransferDirection
{
    Upload,
    Download,
}

/// <summary>Lifecycle of a queued transfer.</summary>
internal enum TransferStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One file transfer shown in the transfer panel. Mutable by design: the queue
/// updates counters while bytes move and republishes an immutable snapshot for
/// the UI.</summary>
internal sealed class TransferItem
{
    /// <summary>Stable identifier used for cancellation and keys.</summary>
    public required string Id { get; init; }

    /// <summary>Upload or download.</summary>
    public required TransferDirection Direction { get; init; }

    /// <summary>File name shown in the panel.</summary>
    public required string Name { get; init; }

    /// <summary>Remote path being read or written.</summary>
    public required string RemotePath { get; init; }

    /// <summary>Local path, for uploads the source and for downloads the destination.</summary>
    public string? LocalPath { get; init; }

    /// <summary>Total bytes when known, otherwise 0.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Bytes moved so far.</summary>
    public long DoneBytes { get; set; }

    /// <summary>Current status.</summary>
    public TransferStatus Status { get; set; } = TransferStatus.Queued;

    /// <summary>Failure message when <see cref="Status"/> is <see cref="TransferStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Cancellation source owned by this transfer.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>0..1 progress; a completed transfer with unknown size reports 1.</summary>
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)DoneBytes / TotalBytes, 0d, 1d)
        : Status == TransferStatus.Completed ? 1d : 0d;

    /// <summary><c>true</c> while the transfer can still be cancelled.</summary>
    public bool IsActive => Status is TransferStatus.Queued or TransferStatus.Running;
}

/// <summary>
/// Sequential transfer queue. Jobs run one after another so a slow link is not
/// saturated by parallel copies; each job reports progress through the
/// <see cref="IProgress{T}"/> the protocol hands back. The queue owns no UI
/// state: it publishes a snapshot and a completion callback, both of which the
/// view binds to component state.
/// </summary>
internal sealed class SftpTransferQueue
{
    private const int BufferSize = 81_920;

    private readonly object _gate = new();
    private readonly List<TransferItem> _items = new();
    private readonly Queue<(TransferItem Item, IRemoteFileSystem FileSystem)> _pending = new();
    private readonly Action<IReadOnlyList<TransferItem>> _publish;
    private readonly Action<TransferItem> _finished;
    private Task? _worker;
    private DateTimeOffset _lastPublish = DateTimeOffset.MinValue;

    /// <summary>Creates a queue that publishes snapshots and completion events.</summary>
    public SftpTransferQueue(Action<IReadOnlyList<TransferItem>> publish, Action<TransferItem> finished)
    {
        _publish = publish;
        _finished = finished;
    }

    /// <summary>Adds a job and starts the worker when it is idle.</summary>
    public void Enqueue(TransferItem item, IRemoteFileSystem fileSystem)
    {
        // Enqueue and worker start happen under one lock so a job can never be
        // queued between the worker's "queue is empty" check and its exit.
        lock (_gate)
        {
            _items.Add(item);
            _pending.Enqueue((item, fileSystem));
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
        if (item.Status == TransferStatus.Cancelled)
        {
            return;
        }

        item.Status = TransferStatus.Running;
        Publish(force: true);

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

            item.Status = TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            item.Status = TransferStatus.Cancelled;
        }
        catch (Exception ex)
        {
            item.Status = TransferStatus.Failed;
            item.Error = ex.Message;
        }
        finally
        {
            Publish(force: true);
            _finished(item);
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
        var protocolProgress = new Progress<TransferProgress>(progress =>
        {
            if (progress.BytesTransferred > item.DoneBytes)
            {
                item.DoneBytes = progress.BytesTransferred;
                Publish();
            }
        });

        await using var target = await fileSystem
            .OpenWriteAsync(item.RemotePath, item.TotalBytes > 0 ? item.TotalBytes : null, protocolProgress, token)
            .ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            item.DoneBytes += read;
            Publish();
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
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            item.DoneBytes += read;
            Publish();
        }

        await target.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>Publishes an immutable snapshot, throttled while bytes stream.</summary>
    private void Publish(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!force && now - _lastPublish < TimeSpan.FromMilliseconds(120))
            {
                return;
            }

            _lastPublish = now;
        }

        TransferItem[] snapshot;
        lock (_gate)
        {
            snapshot = _items.ToArray();
        }

        _publish(snapshot);
    }
}
