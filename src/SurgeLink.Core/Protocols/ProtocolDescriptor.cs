namespace SurgeLink.Core.Protocols;

/// <summary>
/// Static description of a protocol plugin. The shell uses this for the
/// "new session" picker, default ports, and capability-driven UI.
/// </summary>
/// <param name="Id">Stable machine id, for example <c>ssh</c>.</param>
/// <param name="DisplayName">Human readable name shown in the UI.</param>
/// <param name="DefaultPort">Default TCP port, 0 when not network based.</param>
/// <param name="Capabilities">Features the protocol exposes.</param>
public sealed record ProtocolDescriptor(
    string Id,
    string DisplayName,
    int DefaultPort,
    SessionCapabilities Capabilities)
{
    /// <summary>Segoe Fluent Icons glyph used by the session picker.</summary>
    public string Glyph { get; init; } = "\uE8CE";

    /// <summary>Short description rendered under the name in pickers.</summary>
    public string? Description { get; init; }

    /// <summary>Login is entered in the terminal; the shell must not collect connection credentials.</summary>
    public bool AuthenticatesInTerminal { get; init; }

    /// <summary>False for local devices: no TCP port or user name is needed.</summary>
    public bool UsesNetworkAddress { get; init; } = true;

    /// <summary>Endpoint label and localization key for a local-device connection.</summary>
    public string EndpointLabel { get; init; } = "Device";
    public string EndpointLabelKey { get; init; } = "protocol.device";

    /// <summary>Optional discovery callback; returns device names without opening them.</summary>
    public Func<IReadOnlyList<string>>? DiscoverEndpoints { get; init; }

    /// <summary>Protocol-specific connection settings rendered by the session editor.</summary>
    public IReadOnlyList<ProtocolOptionDescriptor> ConnectionOptions { get; init; } = [];
}
