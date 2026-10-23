using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Serial;
using SurgeLink.Tests.Support;

namespace SurgeLink.Protocols.Conformance;

/// <summary>Exercises the production session and terminal against a bounded in-memory port.</summary>
public sealed class SerialProtocolFixture : ProtocolFixture
{
    public override string Name => "serial";
    public override IProtocolPlugin Plugin { get; } = new SerialProtocolPlugin(_ => new FakeSerialConnection { Echo = true });
    public override ConnectionRequest CreateConnectionRequest() => new() { Host = "COM3", Username = "conformance" };
}
