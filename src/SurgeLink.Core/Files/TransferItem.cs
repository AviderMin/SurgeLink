namespace SurgeLink.Core.Files;

/// <summary>Direction of a queued transfer.</summary>
public enum TransferDirection
{
    Upload,
    Download,
}

/// <summary>Lifecycle of a queued transfer.</summary>
public enum TransferStatus
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
public sealed class TransferItem
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

    /// <summary>Transfers a directory recursively; downloads produce a ZIP archive.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Exact existing files the user explicitly allowed this upload to replace.</summary>
    public IReadOnlySet<string> OverwritePaths { get; init; } = new HashSet<string>(StringComparer.Ordinal);

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
