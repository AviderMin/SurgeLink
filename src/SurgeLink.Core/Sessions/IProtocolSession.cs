using SurgeLink.Core.Files;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Core.Sessions;

/// <summary>
/// A live connection. Capabilities that the protocol does not support stay
/// <c>null</c>, which is how the shell decides which tabs to show.
/// </summary>
public interface IProtocolSession : IAsyncDisposable
{
    /// <summary>Process-unique session identifier.</summary>
    string SessionId { get; }

    /// <summary>Descriptor of the protocol that created this session.</summary>
    ProtocolDescriptor Protocol { get; }

    /// <summary>Current lifecycle state.</summary>
    ConnectionState State { get; }

    /// <summary>What is known about the remote host.</summary>
    RemoteHostInfo HostInfo { get; }

    /// <summary>Interactive terminal, when the protocol supports shells.</summary>
    IRemoteTerminal? Terminal { get; }

    /// <summary>Remote file system browser, when the protocol supports files.</summary>
    IRemoteFileSystem? FileSystem { get; }

    /// <summary>Remote performance monitor, when the protocol supports it.</summary>
    IRemoteSystemMonitor? Monitor { get; }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>Raised for non fatal problems worth surfacing (channel dropped, keep-alive failed).</summary>
    event EventHandler<string>? Faulted;

    /// <summary>Resolves and caches host facts; cheap to call repeatedly.</summary>
    Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken cancellationToken);
}
