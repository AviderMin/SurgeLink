using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using Xunit.Sdk;

namespace SurgeLink.Protocols.Conformance;

/// <summary>Assertions shared by the conformance checks and by protocol-specific fixtures.</summary>
public static class ConformanceAssert
{
    /// <summary>Polls until <paramref name="condition"/> is true, failing after the timeout.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        ArgumentNullException.ThrowIfNull(condition);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XunitException($"{because} did not become true within {timeout.TotalMilliseconds:0} ms.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Awaits a task, failing the test instead of hanging when it takes too long.</summary>
    public static async Task<T> CompletesWithinAsync<T>(Task<T> task, TimeSpan timeout, string because)
    {
        ArgumentNullException.ThrowIfNull(task);
        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (!ReferenceEquals(completed, task))
        {
            throw new XunitException($"{because} did not complete within {timeout.TotalMilliseconds:0} ms.");
        }

        return await task;
    }

    /// <summary>Awaits a task, failing the test instead of hanging when it takes too long.</summary>
    public static async Task CompletesWithinAsync(Task task, TimeSpan timeout, string because)
    {
        ArgumentNullException.ThrowIfNull(task);
        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (!ReferenceEquals(completed, task))
        {
            throw new XunitException($"{because} did not complete within {timeout.TotalMilliseconds:0} ms.");
        }

        await task;
    }

    /// <summary>
    /// Asserts that the capability flags and the exposed session handles agree:
    /// a declared capability must have a non-null handle and a handle must be
    /// declared, otherwise the shell hides a feature the protocol actually has.
    /// </summary>
    public static void AssertCapabilityHandles(IProtocolSession session, SessionCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(session);
        Assert.Equal(Has(capabilities, SessionCapabilities.Terminal), session.Terminal is not null);
        Assert.Equal(Has(capabilities, SessionCapabilities.FileSystem), session.FileSystem is not null);
        Assert.Equal(Has(capabilities, SessionCapabilities.Monitor), session.Monitor is not null);
    }

    /// <summary>
    /// Asserts that a terminal raised <see cref="SurgeLink.Core.Terminal.IRemoteTerminal.Closed"/>
    /// at most once. A protocol that both completes its pump and disposes the
    /// terminal without an idempotence guard fails here.
    /// </summary>
    public static void AssertClosedAtMostOnce(int closedCount) =>
        Assert.InRange(closedCount, 0, 1);

    /// <summary>
    /// Asserts that a disposed session reached <see cref="ConnectionState.Disconnected"/>.
    /// Catches protocols that forget to transition state when they dispose.
    /// </summary>
    public static void AssertDisposed(IProtocolSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Assert.Equal(ConnectionState.Disconnected, session.State);
    }

    /// <summary>Validates every numeric range the monitor UI relies on.</summary>
    public static void AssertSaneSnapshot(SystemSnapshot snapshot)
    {
        Assert.NotNull(snapshot);
        Assert.NotEqual(default, snapshot.TimestampUtc);
        Assert.Equal(TimeSpan.Zero, snapshot.TimestampUtc.Offset);

        Assert.InRange(snapshot.Cpu.TotalPercent, 0d, 100d);
        foreach (double core in snapshot.Cpu.PerCorePercent)
        {
            Assert.InRange(core, 0d, 100d);
        }

        Assert.True(snapshot.Cpu.ProcessCount >= 0, "process count must not be negative");
        Assert.True(snapshot.Cpu.ThreadCount >= 0, "thread count must not be negative");

        Assert.True(snapshot.Memory.TotalBytes >= 0, "total memory must not be negative");
        Assert.True(snapshot.Memory.AvailableBytes >= 0, "available memory must not be negative");
        Assert.True(
            snapshot.Memory.AvailableBytes <= snapshot.Memory.TotalBytes,
            "available memory must not exceed total memory");
        Assert.True(
            snapshot.Memory.UsedBytes <= snapshot.Memory.TotalBytes,
            "used memory must not exceed total memory");
        if (snapshot.Memory.SwapTotalBytes > 0)
        {
            Assert.True(snapshot.Memory.SwapUsedBytes >= 0, "used swap must not be negative");
            Assert.True(
                snapshot.Memory.SwapUsedBytes <= snapshot.Memory.SwapTotalBytes,
                "used swap must not exceed total swap");
        }

        foreach (DiskSample disk in snapshot.Disks)
        {
            Assert.False(string.IsNullOrWhiteSpace(disk.MountPoint), "disk mount point must be set");
            Assert.True(disk.TotalBytes >= 0, "disk total must not be negative");
            Assert.True(disk.UsedBytes >= 0, "disk used must not be negative");
            Assert.True(disk.UsedBytes <= disk.TotalBytes, $"disk '{disk.MountPoint}' reports used > total");
            Assert.True(disk.AvailableBytes >= 0, "disk available must not be negative");
            Assert.True(disk.AvailableBytes <= disk.TotalBytes, $"disk '{disk.MountPoint}' reports available > total");
        }

        foreach (NetworkSample network in snapshot.Networks)
        {
            Assert.False(string.IsNullOrWhiteSpace(network.InterfaceName), "network interface name must be set");
            Assert.True(network.BytesReceived >= 0, "received bytes must not be negative");
            Assert.True(network.BytesSent >= 0, "sent bytes must not be negative");
            Assert.True(network.PacketsReceived >= 0, "received packets must not be negative");
            Assert.True(network.PacketsSent >= 0, "sent packets must not be negative");
        }
    }

    private static bool Has(SessionCapabilities capabilities, SessionCapabilities flag) => (capabilities & flag) == flag;
}
