namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// Protocol conformance suite. It drives every <see cref="IProtocolUnderTest"/>
/// fixture through the checks a SurgeLink protocol plugin must satisfy, so a new
/// protocol only needs one fixture class to be certified.
/// <para>
/// <b>Adding a protocol</b>
/// </para>
/// <list type="number">
///   <item>Reference the protocol project from this test project (an existing project reference in the csproj, or ask the Lead to add one).</item>
///   <item>
///     Add one fixture class. It must be public with a public parameterless
///     constructor so it can be discovered automatically by reflecting over
///     loaded assemblies that reference this suite; no registration code is
///     needed. (Fixtures with other constructors can call
///     <see cref="ProtocolConformanceRegistry.Register"/> explicitly instead.)
///     <code>
///     public sealed class SshProtocolFixture : ProtocolFixture
///     {
///         public override string Name => "ssh";
///
///         public override IProtocolPlugin Plugin { get; } = new SshProtocolPlugin();
///
///         public override ConnectionRequest CreateConnectionRequest() => new()
///         {
///             Host = "127.0.0.1",
///             Username = "conformance",
///         };
///     }
///     </code>
///   </item>
///   <item>
///     Run the suite with
///     <c>&amp; ".\tools\agent-build.ps1" -Project tests/SurgeLink.Protocols.Conformance -Agent conformance -Test</c>.
///     Descriptor and lifecycle checks run for every fixture; terminal, file system
///     and monitor checks run only for the capabilities the descriptor declares, so
///     a protocol without a capability simply produces no test cases for it.
///   </item>
/// </list>
/// <para>
/// Fixture constructors must be cheap and side-effect free: the registry
/// instantiates them once while discovering the suite. Fleets that share remote
/// state should create unique names in their tests and delete them in
/// <see cref="IProtocolUnderTest.CleanupAsync"/>.
/// </para>
/// </summary>
public static class ProtocolConformanceSuite
{
    /// <summary>How long a single protocol operation may take before a test fails.</summary>
    public static TimeSpan DefaultOperationTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a disposed session is observed for late callbacks.</summary>
    public static TimeSpan LateCallbackObservationWindow { get; } = TimeSpan.FromMilliseconds(200);
}
