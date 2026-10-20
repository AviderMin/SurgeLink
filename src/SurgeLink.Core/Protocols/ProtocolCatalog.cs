namespace SurgeLink.Core.Protocols;

/// <summary>Default <see cref="IProtocolCatalog"/> implementation.</summary>
public sealed class ProtocolCatalog : IProtocolCatalog
{
    private readonly object _gate = new();
    private readonly List<IProtocolPlugin> _plugins = new();

    /// <inheritdoc />
    public IReadOnlyList<IProtocolPlugin> Plugins
    {
        get
        {
            lock (_gate)
            {
                return _plugins.ToList();
            }
        }
    }

    /// <inheritdoc />
    public void Register(IProtocolPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_gate)
        {
            if (_plugins.Any(p => string.Equals(p.Descriptor.Id, plugin.Descriptor.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Protocol '{plugin.Descriptor.Id}' is already registered.");
            }

            _plugins.Add(plugin);
        }
    }

    /// <inheritdoc />
    public bool TryGet(string protocolId, out IProtocolPlugin plugin)
    {
        lock (_gate)
        {
            foreach (var candidate in _plugins)
            {
                if (string.Equals(candidate.Descriptor.Id, protocolId, StringComparison.OrdinalIgnoreCase))
                {
                    plugin = candidate;
                    return true;
                }
            }
        }

        plugin = null!;
        return false;
    }

    /// <inheritdoc />
    public IProtocolPlugin Get(string protocolId) =>
        TryGet(protocolId, out var plugin)
            ? plugin
            : throw new ProtocolException($"No protocol registered for '{protocolId}'.", ProtocolFailureKind.Configuration);
}
