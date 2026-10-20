namespace SurgeLink.Core.Protocols;

/// <summary>Raised for expected, user presentable protocol failures.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message, ProtocolFailureKind kind = ProtocolFailureKind.Unknown, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    /// <summary>Category used to pick the right UI treatment.</summary>
    public ProtocolFailureKind Kind { get; }
}

/// <summary>Why a connection attempt failed.</summary>
public enum ProtocolFailureKind
{
    Unknown,
    Network,
    Timeout,
    Authentication,
    HostKey,
    Configuration,
    Cancelled,
    ServerRejected,
}
