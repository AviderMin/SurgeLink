using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// One protocol plugin enrolled in the conformance suite. Implement this (or
/// derive from <see cref="ProtocolFixture"/>) once per protocol to certify it.
/// </summary>
public interface IProtocolUnderTest
{
    /// <summary>Unique, stable name used in test case names and registry lookups.</summary>
    string Name { get; }

    /// <summary>The plugin instance under test. Must be safe to reuse for several connections.</summary>
    IProtocolPlugin Plugin { get; }

    /// <summary>Creates the connection request used by the generic conformance checks.</summary>
    ConnectionRequest CreateConnectionRequest();

    /// <summary>
    /// Optional hook invoked while the session is still alive, just before the
    /// suite disposes it. Use it to delete remote artifacts created by the tests.
    /// </summary>
    ValueTask CleanupAsync(IProtocolSession session) => ValueTask.CompletedTask;
}
