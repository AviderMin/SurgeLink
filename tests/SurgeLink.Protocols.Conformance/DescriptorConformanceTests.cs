using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Conformance;

public sealed class DescriptorConformanceTests
{
    [Theory]
    [MemberData(nameof(ProtocolConformanceData.FixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public void DescriptorIsWellFormed(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);
        ProtocolDescriptor descriptor = fixture.Plugin.Descriptor;

        Assert.NotNull(descriptor);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Id), "descriptor id must be set");
        Assert.Equal(descriptor.Id, descriptor.Id.Trim());
        Assert.DoesNotContain(" ", descriptor.Id);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName), "descriptor display name must be set");
        Assert.Equal(descriptor.DisplayName, descriptor.DisplayName.Trim());
        Assert.True(descriptor.DefaultPort >= 0, "default port must be 0 (not network based) or a valid port");
    }

    [Theory]
    [MemberData(nameof(ProtocolConformanceData.FixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public void FixtureProvidesUsableConnectionDefaults(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);

        Assert.False(string.IsNullOrWhiteSpace(fixture.Name), "fixture name must be set");
        Assert.DoesNotContain(" ", fixture.Name);
        Assert.NotNull(fixture.Plugin);

        ConnectionRequest request = fixture.CreateConnectionRequest();
        Assert.NotNull(request);
        Assert.False(string.IsNullOrWhiteSpace(request.Host), "connection request host must be set");
        Assert.False(string.IsNullOrWhiteSpace(request.Username), "connection request user name must be set");
        Assert.True(request.Port >= 0);
        Assert.NotNull(request.Options);
    }
}
