using SurgeLink.Core.Sessions;

namespace SurgeLink.Core.Protocols;

/// <summary>
/// Entry point every protocol implements. A new protocol is one class that
/// returns <see cref="ProtocolDescriptor"/> and creates sessions; the shell
/// discovers it through <see cref="IProtocolCatalog"/> and needs no changes.
/// </summary>
public interface IProtocolPlugin
{
    /// <summary>Static metadata for the protocol.</summary>
    ProtocolDescriptor Descriptor { get; }

    /// <summary>
    /// Connects and returns a live session. Implementations should throw
    /// <see cref="ProtocolException"/> for expected failures (bad credentials,
    /// unreachable host, rejected host key) so the UI can present a clean message.
    /// </summary>
    Task<IProtocolSession> ConnectAsync(
        ConnectionRequest request,
        ProtocolHostContext context,
        CancellationToken cancellationToken);
}
