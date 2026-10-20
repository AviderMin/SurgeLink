namespace SurgeLink.Core.Protocols;

/// <summary>
/// Registry of protocol plugins. Adding a protocol is exactly one
/// <see cref="Register"/> call in the composition root.
/// </summary>
public interface IProtocolCatalog
{
    /// <summary>Registered plugins in registration order.</summary>
    IReadOnlyList<IProtocolPlugin> Plugins { get; }

    /// <summary>Adds a plugin; throws when the id is already taken.</summary>
    void Register(IProtocolPlugin plugin);

    /// <summary>Finds a plugin by id.</summary>
    bool TryGet(string protocolId, out IProtocolPlugin plugin);

    /// <summary>Finds a plugin by id or throws <see cref="ProtocolException"/>.</summary>
    IProtocolPlugin Get(string protocolId);
}
