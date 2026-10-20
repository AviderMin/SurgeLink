using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Demo;

namespace SurgeLink.Protocols.Conformance;

/// <summary>Conformance fixture for the offline Demo protocol (the reference implementation).</summary>
public sealed class DemoProtocolFixture : ProtocolFixture
{
    /// <inheritdoc />
    public override string Name => "demo";

    /// <inheritdoc />
    public override IProtocolPlugin Plugin { get; } = new DemoProtocolPlugin();

    /// <inheritdoc />
    public override ConnectionRequest CreateConnectionRequest() => new()
    {
        Host = "demo-host",
        Username = "conformance",
        DisplayName = "Demo conformance session",
        Options = new Dictionary<string, string> { ["campaign"] = "conformance" },
    };
}
