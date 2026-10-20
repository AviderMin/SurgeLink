using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// Convenience base for <see cref="IProtocolUnderTest"/>. Override
/// <see cref="Name"/>, <see cref="Plugin"/> and (when the protocol needs a
/// specific endpoint or authentication) <see cref="CreateConnectionRequest"/>.
/// </summary>
public abstract class ProtocolFixture : IProtocolUnderTest
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract IProtocolPlugin Plugin { get; }

    /// <summary>
    /// Default request for an offline/local conformance endpoint. Protocols that
    /// need a real or simulated server override this.
    /// </summary>
    public virtual ConnectionRequest CreateConnectionRequest() => new()
    {
        Host = "localhost",
        Username = "conformance",
        DisplayName = $"Conformance ({Name})",
    };

    /// <inheritdoc />
    public virtual ValueTask CleanupAsync(IProtocolSession session) => ValueTask.CompletedTask;
}
