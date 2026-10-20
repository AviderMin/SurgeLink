using SurgeLink.Core.Files;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;
using Xunit.Sdk;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// Meta tests for the kit itself: they prove the reusable assertions fail when a
/// protocol misbehaves (wrong capability handles, nonsense monitor ranges,
/// hanging operations), which is what makes the suite a conformance gate rather
/// than a smoke test.
/// </summary>
public sealed class KitSelfTests
{
    [Fact]
    public void CapabilityHandlesMustMatchDeclaredCapabilities()
    {
        var session = new StubSession();

        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertCapabilityHandles(session, SessionCapabilities.Terminal));
        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertCapabilityHandles(session, SessionCapabilities.FileSystem));
        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertCapabilityHandles(session, SessionCapabilities.Monitor));
        ConformanceAssert.AssertCapabilityHandles(session, SessionCapabilities.None);
    }

    [Fact]
    public void SaneSnapshotCheckAcceptsValidSnapshot()
    {
        ConformanceAssert.AssertSaneSnapshot(CreateSaneSnapshot());
    }

    [Fact]
    public void SaneSnapshotCheckRejectsMemoryAvailableAboveTotal()
    {
        SystemSnapshot snapshot = CreateSaneSnapshot() with
        {
            Memory = new MemorySample { TotalBytes = 100, AvailableBytes = 200 },
        };

        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertSaneSnapshot(snapshot));
    }

    [Fact]
    public void SaneSnapshotCheckRejectsDiskUsedAboveTotal()
    {
        SystemSnapshot snapshot = CreateSaneSnapshot() with
        {
            Disks = new[] { new DiskSample { MountPoint = "/", TotalBytes = 100, UsedBytes = 150 } },
        };

        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertSaneSnapshot(snapshot));
    }

    [Fact]
    public void SaneSnapshotCheckRejectsCpuOutOfRange()
    {
        SystemSnapshot snapshot = CreateSaneSnapshot() with
        {
            Cpu = new CpuSample { TotalPercent = 20, PerCorePercent = new[] { 20d, 150d } },
        };

        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertSaneSnapshot(snapshot));
    }

    [Fact]
    public void SaneSnapshotCheckRejectsNonUtcTimestamp()
    {
        SystemSnapshot snapshot = CreateSaneSnapshot() with
        {
            TimestampUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)),
        };

        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertSaneSnapshot(snapshot));
    }

    [Fact]
    public async Task HangingOperationsFailInsteadOfHangingTheRun()
    {
        await Assert.ThrowsAsync<XunitException>(
            () => ConformanceAssert.CompletesWithinAsync(
                Task.Delay(TimeSpan.FromSeconds(30)),
                TimeSpan.FromMilliseconds(20),
                "hanging operation"));
    }


    [Fact]
    public void ClosedAtMostOnceCheckRejectsDuplicateClosedEvents()
    {
        ConformanceAssert.AssertClosedAtMostOnce(0);
        ConformanceAssert.AssertClosedAtMostOnce(1);
        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertClosedAtMostOnce(2));
    }

    [Fact]
    public void DisposedCheckRejectsSessionsThatDidNotDisconnect()
    {
        ConformanceAssert.AssertDisposed(new StubSession(ConnectionState.Disconnected));
        Assert.ThrowsAny<XunitException>(() => ConformanceAssert.AssertDisposed(new StubSession(ConnectionState.Connected)));
    }

    private static SystemSnapshot CreateSaneSnapshot() => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow,
        Cpu = new CpuSample
        {
            TotalPercent = 25,
            PerCorePercent = new[] { 20d, 30d },
            ProcessCount = 10,
            ThreadCount = 20,
        },
        Memory = new MemorySample
        {
            TotalBytes = 1000,
            AvailableBytes = 400,
            SwapTotalBytes = 100,
            SwapUsedBytes = 10,
        },
        Disks = new[]
        {
            new DiskSample { MountPoint = "/", TotalBytes = 1000, UsedBytes = 400, AvailableBytes = 600 },
        },
        Networks = new[]
        {
            new NetworkSample { InterfaceName = "eth0", BytesReceived = 10, BytesSent = 5 },
        },
    };

    private sealed class StubSession : IProtocolSession
    {
        private readonly ConnectionState _state;

        public StubSession(ConnectionState state = ConnectionState.Connected) => _state = state;

        public string SessionId { get; } = Guid.NewGuid().ToString("n");

        public ProtocolDescriptor Protocol { get; } = new("stub", "Stub", 0, SessionCapabilities.None);

        public ConnectionState State => _state;

        public RemoteHostInfo HostInfo { get; } = new();

        public IRemoteTerminal? Terminal => null;

        public IRemoteFileSystem? FileSystem => null;

        public IRemoteSystemMonitor? Monitor => null;

#pragma warning disable CS0067 // The stub never raises events; they exist to satisfy IProtocolSession.
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

        public event EventHandler<string>? Faulted;
#pragma warning restore CS0067

        public Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken cancellationToken) => Task.FromResult(HostInfo);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
