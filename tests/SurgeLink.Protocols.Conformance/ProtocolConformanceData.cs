using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// xunit MemberData sources for the conformance tests. xunit 2.9 has no runtime
/// skip, so capability checks are projected per capability instead: a protocol
/// that does not declare a capability gets no test cases for that capability,
/// which is the xunit 2 equivalent of a skip.
/// </summary>
public static class ProtocolConformanceData
{
    /// <summary>Every discovered fixture.</summary>
    public static TheoryData<string> FixtureNames => Select(_ => true);

    /// <summary>Fixtures whose descriptor declares <see cref="SessionCapabilities.Terminal"/>.</summary>
    public static TheoryData<string> TerminalFixtureNames => Select(fixture => HasCapability(fixture, SessionCapabilities.Terminal));

    /// <summary>Fixtures whose descriptor declares <see cref="SessionCapabilities.FileSystem"/>.</summary>
    public static TheoryData<string> FileSystemFixtureNames => Select(fixture => HasCapability(fixture, SessionCapabilities.FileSystem));

    /// <summary>Fixtures whose descriptor declares <see cref="SessionCapabilities.Monitor"/>.</summary>
    public static TheoryData<string> MonitorFixtureNames => Select(fixture => HasCapability(fixture, SessionCapabilities.Monitor));

    /// <summary>True when the fixture declares every flag in <paramref name="capability"/>.</summary>
    public static bool HasCapability(IProtocolUnderTest fixture, SessionCapabilities capability) =>
        (fixture.Plugin.Descriptor.Capabilities & capability) == capability;

    private static TheoryData<string> Select(Func<IProtocolUnderTest, bool> predicate)
    {
        var data = new TheoryData<string>();
        foreach (IProtocolUnderTest fixture in ProtocolConformanceRegistry.All)
        {
            if (predicate(fixture))
            {
                data.Add(fixture.Name);
            }
        }

        return data;
    }
}
