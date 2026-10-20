using SurgeLink.Core.Protocols;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class ProtocolCatalogTests
{
    [Fact]
    public void RegisterAddsPluginsInRegistrationOrder()
    {
        var catalog = new ProtocolCatalog();

        catalog.Register(new FakeProtocolPlugin("ssh"));
        catalog.Register(new FakeProtocolPlugin("sftp"));
        catalog.Register(new FakeProtocolPlugin("rdp"));

        Assert.Equal(new[] { "ssh", "sftp", "rdp" }, catalog.Plugins.Select(p => p.Descriptor.Id));
    }

    [Fact]
    public void TryGetFindsPluginCaseInsensitively()
    {
        var catalog = new ProtocolCatalog();
        var plugin = new FakeProtocolPlugin("ssh");
        catalog.Register(plugin);

        Assert.True(catalog.TryGet("ssh", out IProtocolPlugin exact));
        Assert.True(catalog.TryGet("SSH", out IProtocolPlugin upper));
        Assert.True(catalog.TryGet("SsH", out IProtocolPlugin mixed));
        Assert.Same(plugin, exact);
        Assert.Same(plugin, upper);
        Assert.Same(plugin, mixed);
    }

    [Fact]
    public void TryGetMissingReturnsFalseAndNullPlugin()
    {
        var catalog = new ProtocolCatalog();

        bool found = catalog.TryGet("missing", out IProtocolPlugin plugin);

        Assert.False(found);
        Assert.Null(plugin);
    }

    [Fact]
    public void GetReturnsRegisteredPlugin()
    {
        var catalog = new ProtocolCatalog();
        var plugin = new FakeProtocolPlugin("ssh");
        catalog.Register(plugin);

        Assert.Same(plugin, catalog.Get("SSH"));
    }

    [Fact]
    public void GetMissingThrowsConfigurationProtocolException()
    {
        var catalog = new ProtocolCatalog();

        ProtocolException exception = Assert.Throws<ProtocolException>(() => catalog.Get("missing"));

        Assert.Equal(ProtocolFailureKind.Configuration, exception.Kind);
        Assert.Contains("missing", exception.Message);
    }

    [Fact]
    public void RegisterDuplicateIdThrows()
    {
        var catalog = new ProtocolCatalog();
        catalog.Register(new FakeProtocolPlugin("ssh"));

        Assert.Throws<InvalidOperationException>(() => catalog.Register(new FakeProtocolPlugin("ssh")));
    }

    [Fact]
    public void RegisterDuplicateIdIsCaseInsensitive()
    {
        var catalog = new ProtocolCatalog();
        catalog.Register(new FakeProtocolPlugin("ssh"));

        Assert.Throws<InvalidOperationException>(() => catalog.Register(new FakeProtocolPlugin("SSH")));
    }

    [Fact]
    public void RegisterNullThrows()
    {
        var catalog = new ProtocolCatalog();

        Assert.Throws<ArgumentNullException>(() => catalog.Register(null!));
    }

    [Fact]
    public void PluginsReturnsDefensiveSnapshot()
    {
        var catalog = new ProtocolCatalog();
        catalog.Register(new FakeProtocolPlugin("ssh"));

        IReadOnlyList<IProtocolPlugin> snapshot = catalog.Plugins;
        catalog.Register(new FakeProtocolPlugin("sftp"));

        Assert.Single(snapshot);
        Assert.Equal(2, catalog.Plugins.Count);

        var mutable = (List<IProtocolPlugin>)snapshot;
        mutable.Clear();
        Assert.Equal(2, catalog.Plugins.Count);
    }

    [Fact]
    public void ConcurrentRegistrationIsSafe()
    {
        var catalog = new ProtocolCatalog();

        Parallel.For(0, 32, i => catalog.Register(new FakeProtocolPlugin("protocol-" + i)));

        Assert.Equal(32, catalog.Plugins.Count);
        for (int i = 0; i < 32; i++)
        {
            Assert.True(catalog.TryGet("protocol-" + i, out _));
        }
    }
}
