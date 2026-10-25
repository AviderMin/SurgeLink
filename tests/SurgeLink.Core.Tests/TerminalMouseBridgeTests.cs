using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Sessions;
using SurgeLink.Terminal;

namespace SurgeLink.Core.Tests;

public sealed class TerminalMouseBridgeTests
{
    [Fact]
    public async Task MouseReportsTravelThroughTheHumanInputAndTransportPipeline()
    {
        var remote = new AutomationRemote();
        await using var bridge = new TerminalBridge(remote, new AutomationDispatcher(), NullLogger.Instance);
        remote.Emit("\u001b[?1000;1006h");
        var input = 0;
        bridge.UserInput += (_, _) => input++;
        Assert.True(bridge.SendMouse(2, 3, TerminalMouseButton.Left, TerminalMouseAction.Press));
        Assert.Equal("\u001b[<0;3;4M", Assert.Single(remote.Writes));
        Assert.Equal(1, input);
        Assert.Equal(1, bridge.ReadScreen().InputRevision);
        Assert.False(bridge.SendMouse(2, 3, TerminalMouseButton.Left, TerminalMouseAction.Press, shift: true));
        Assert.Single(remote.Writes);
        Assert.Equal(1, input);
    }
}
