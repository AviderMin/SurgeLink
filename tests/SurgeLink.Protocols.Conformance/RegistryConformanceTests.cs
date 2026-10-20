using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Conformance;

public sealed class RegistryConformanceTests
{
    [Fact]
    public void AtLeastOneFixtureIsDiscovered()
    {
        Assert.NotEmpty(ProtocolConformanceRegistry.All);
        Assert.Empty(ProtocolConformanceRegistry.DiscoveryErrors);
    }

    [Fact]
    public void FixtureNamesAreUniqueAndSuitableForTestNames()
    {
        IReadOnlyList<IProtocolUnderTest> fixtures = ProtocolConformanceRegistry.All;

        Assert.Equal(fixtures.Count, fixtures.Select(fixture => fixture.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (IProtocolUnderTest fixture in fixtures)
        {
            Assert.False(string.IsNullOrWhiteSpace(fixture.Name));
            Assert.DoesNotContain(" ", fixture.Name);
            Assert.NotNull(fixture.Plugin);
            Assert.NotNull(fixture.Plugin.Descriptor);
        }
    }

    [Fact]
    public void DemoFixtureIsDiscoveredWithEveryCapability()
    {
        IProtocolUnderTest demo = ProtocolConformanceRegistry.Get("demo");

        Assert.Equal("demo", demo.Plugin.Descriptor.Id);
        SessionCapabilities capabilities = demo.Plugin.Descriptor.Capabilities;
        Assert.True((capabilities & SessionCapabilities.Terminal) != 0);
        Assert.True((capabilities & SessionCapabilities.FileSystem) != 0);
        Assert.True((capabilities & SessionCapabilities.Monitor) != 0);
    }

    [Fact]
    public void UnknownFixtureNameReportsKnownFixtures()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProtocolConformanceRegistry.Get("does-not-exist"));

        Assert.Contains("does-not-exist", exception.Message);
        Assert.Contains("demo", exception.Message);
    }

    [Fact]
    public void CapabilityProjectionsOnlyContainCapableFixtures()
    {
        foreach (string name in ProtocolConformanceData.TerminalFixtureNames)
        {
            Assert.True(
                ProtocolConformanceData.HasCapability(ProtocolConformanceRegistry.Get(name), SessionCapabilities.Terminal),
                $"fixture '{name}' was projected as terminal capable");
        }

        foreach (string name in ProtocolConformanceData.FileSystemFixtureNames)
        {
            Assert.True(
                ProtocolConformanceData.HasCapability(ProtocolConformanceRegistry.Get(name), SessionCapabilities.FileSystem),
                $"fixture '{name}' was projected as file system capable");
        }

        foreach (string name in ProtocolConformanceData.MonitorFixtureNames)
        {
            Assert.True(
                ProtocolConformanceData.HasCapability(ProtocolConformanceRegistry.Get(name), SessionCapabilities.Monitor),
                $"fixture '{name}' was projected as monitor capable");
        }
    }

    [Fact]
    public void CapabilityProjectionsCoverEveryCapableFixture()
    {
        foreach (IProtocolUnderTest fixture in ProtocolConformanceRegistry.All)
        {
            Assert.Equal(
                ProtocolConformanceData.HasCapability(fixture, SessionCapabilities.Terminal),
                ContainsName(ProtocolConformanceData.TerminalFixtureNames, fixture.Name));
            Assert.Equal(
                ProtocolConformanceData.HasCapability(fixture, SessionCapabilities.FileSystem),
                ContainsName(ProtocolConformanceData.FileSystemFixtureNames, fixture.Name));
            Assert.Equal(
                ProtocolConformanceData.HasCapability(fixture, SessionCapabilities.Monitor),
                ContainsName(ProtocolConformanceData.MonitorFixtureNames, fixture.Name));
        }
    }

    private static bool ContainsName(TheoryData<string> data, string name)
    {
        // TheoryData<T> exposes a pattern based enumerator, not IEnumerable<T>,
        // so LINQ cannot be used here.
        foreach (string value in data)
        {
            if (string.Equals(value, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
