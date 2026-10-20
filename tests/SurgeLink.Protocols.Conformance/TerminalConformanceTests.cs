using System.Text;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Protocols.Conformance;

public sealed class TerminalConformanceTests
{
    [Theory]
    [MemberData(nameof(ProtocolConformanceData.TerminalFixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task TerminalStartsWritesAndResizes(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);
        TimeSpan timeout = ProtocolConformanceSuite.DefaultOperationTimeout;

        await using ConformanceSession connection = await ConformanceSession.OpenAsync(fixture);
        IRemoteTerminal terminal = connection.Session.Terminal!;

        await ConformanceAssert.CompletesWithinAsync(
            terminal.StartAsync(
                new TerminalStartOptions { Columns = 80, Rows = 24, TerminalType = "xterm-256color" },
                CancellationToken.None),
            timeout,
            "terminal StartAsync");

        Assert.True(terminal.IsOpen, "terminal must report open after StartAsync");
        Assert.Equal(80, terminal.Columns);
        Assert.Equal(24, terminal.Rows);

        await ConformanceAssert.CompletesWithinAsync(
            terminal.WriteAsync(Encoding.UTF8.GetBytes("echo conformance\r"), CancellationToken.None).AsTask(),
            timeout,
            "terminal WriteAsync");

        Assert.True(terminal.IsOpen, "terminal must stay open after WriteAsync");

        await ConformanceAssert.CompletesWithinAsync(
            terminal.ResizeAsync(100, 30, CancellationToken.None),
            timeout,
            "first ResizeAsync");
        Assert.Equal(100, terminal.Columns);
        Assert.Equal(30, terminal.Rows);

        await ConformanceAssert.CompletesWithinAsync(
            terminal.ResizeAsync(90, 20, CancellationToken.None),
            timeout,
            "second ResizeAsync");
        Assert.Equal(90, terminal.Columns);
        Assert.Equal(20, terminal.Rows);
    }

    [Theory]
    [MemberData(nameof(ProtocolConformanceData.TerminalFixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task DisposeRaisesClosedAtMostOnceAndWriteAfterDisposeIsSafe(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);
        TimeSpan timeout = ProtocolConformanceSuite.DefaultOperationTimeout;
        IProtocolSession session = await ConformanceConnector.ConnectAsync(fixture, new ConformanceHost());

        try
        {
            IRemoteTerminal terminal = session.Terminal!;
            await ConformanceAssert.CompletesWithinAsync(
                terminal.StartAsync(new TerminalStartOptions { Columns = 80, Rows = 24 }, CancellationToken.None),
                timeout,
                "terminal StartAsync");

            int closedCount = 0;
            terminal.Closed += (_, _) => Interlocked.Increment(ref closedCount);

            await ConformanceAssert.CompletesWithinAsync(terminal.DisposeAsync().AsTask(), timeout, "terminal DisposeAsync");

            // Give a stray background callback a window to land before asserting.
            await Task.Delay(50);

            ConformanceAssert.AssertClosedAtMostOnce(closedCount);
            Assert.False(terminal.IsOpen, "terminal must report closed after DisposeAsync");

            Exception? afterDispose = await Record.ExceptionAsync(async () =>
                await terminal
                    .WriteAsync(Encoding.UTF8.GetBytes("x"), CancellationToken.None)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.True(
                afterDispose is null or ObjectDisposedException or InvalidOperationException,
                $"WriteAsync after dispose threw {afterDispose?.GetType().Name ?? "nothing"}; expected a no-op, ObjectDisposedException or InvalidOperationException.");
        }
        finally
        {
            // The terminal was disposed directly, which some protocols do not expect;
            // ignore teardown failures here because the session dispose contract is
            // covered by SessionLifecycleConformanceTests.
            await Record.ExceptionAsync(async () => await session.DisposeAsync());
        }
    }
}
