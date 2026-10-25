using System.Text;

namespace SurgeLink.Terminal.Tests;

public sealed class MouseReportingTests
{
    [Theory]
    [InlineData(TerminalMouseButton.Left, TerminalMouseAction.Press, "\u001b[<0;10;5M")]
    [InlineData(TerminalMouseButton.Left, TerminalMouseAction.Release, "\u001b[<0;10;5m")]
    [InlineData(TerminalMouseButton.Right, TerminalMouseAction.Release, "\u001b[<2;10;5m")]
    [InlineData(TerminalMouseButton.Left, TerminalMouseAction.Move, "\u001b[<32;10;5M")]
    [InlineData(TerminalMouseButton.None, TerminalMouseAction.Move, "\u001b[<35;10;5M")]
    [InlineData(TerminalMouseButton.WheelUp, TerminalMouseAction.Wheel, "\u001b[<64;10;5M")]
    [InlineData(TerminalMouseButton.WheelDown, TerminalMouseAction.Wheel, "\u001b[<65;10;5M")]
    [InlineData(TerminalMouseButton.WheelLeft, TerminalMouseAction.Wheel, "\u001b[<66;10;5M")]
    public void SgrReportsUseOneBasedCoordinatesAndDistinctRelease(TerminalMouseButton button, TerminalMouseAction action, string expected)
    {
        var terminal = new TestTerminal(80, 24);
        terminal.Feed("\u001b[?1003;1006h");
        Assert.Equal(expected, Encoding.ASCII.GetString(TerminalMouseEncoder.Encode(9, 4, button, action, terminal.Modes)!));
    }

    [Fact]
    public void ModifiersAndLargeSgrCoordinatesAreNotTruncated()
    {
        var terminal = new TestTerminal(80, 24);
        terminal.Feed("\u001b[?1000;1006h");
        Assert.Equal("\u001b[<24;1001;501M", Encoding.ASCII.GetString(TerminalMouseEncoder.Encode(1000, 500,
            TerminalMouseButton.Left, TerminalMouseAction.Press, terminal.Modes, control: true, alt: true)!));
    }

    [Fact]
    public void ClassicReportsUseBinaryCoordinatesAndGenericRelease()
    {
        var modes = new TerminalModes { MouseTracking = true };
        Assert.Equal(new byte[] { 27, 91, 77, 32, 36, 39 }, TerminalMouseEncoder.Encode(3, 6, TerminalMouseButton.Left, TerminalMouseAction.Press, modes));
        Assert.Equal(new byte[] { 27, 91, 77, 35, 36, 39 }, TerminalMouseEncoder.Encode(3, 6, TerminalMouseButton.Right, TerminalMouseAction.Release, modes));
        Assert.Null(TerminalMouseEncoder.Encode(223, 6, TerminalMouseButton.Left, TerminalMouseAction.Press, modes));
    }

    [Fact]
    public void ReportingScopesAndModeResetAreRespected()
    {
        var terminal = new TestTerminal(80, 24);
        terminal.Feed("\u001b[?1000h");
        Assert.Null(TerminalMouseEncoder.Encode(0, 0, TerminalMouseButton.Left, TerminalMouseAction.Move, terminal.Modes));
        terminal.Feed("\u001b[?1002h");
        Assert.NotNull(TerminalMouseEncoder.Encode(0, 0, TerminalMouseButton.Left, TerminalMouseAction.Move, terminal.Modes));
        Assert.Null(TerminalMouseEncoder.Encode(0, 0, TerminalMouseButton.None, TerminalMouseAction.Move, terminal.Modes));
        terminal.Feed("\u001b[?1003;1006h");
        Assert.NotNull(TerminalMouseEncoder.Encode(0, 0, TerminalMouseButton.None, TerminalMouseAction.Move, terminal.Modes));
        terminal.Feed("\u001b[?1003l");
        Assert.Equal(TerminalMouseTrackingMode.Drag, terminal.Modes.MouseTrackingMode);
        terminal.Feed("\u001b[!p");
        Assert.False(terminal.Modes.SgrMouseEncoding);
        Assert.False(terminal.Modes.MouseTracking);
        Assert.Equal(TerminalMouseTrackingMode.None, terminal.Modes.MouseTrackingMode);
    }

    [Fact]
    public void ShiftAlwaysKeepsSelectionLocal()
    {
        var modes = new TerminalModes { MouseTracking = true, MouseTrackingMode = TerminalMouseTrackingMode.Any, SgrMouseEncoding = true };
        foreach (var action in Enum.GetValues<TerminalMouseAction>())
            Assert.Null(TerminalMouseEncoder.Encode(0, 0, TerminalMouseButton.Left, action, modes, shift: true));
        Assert.Null(TerminalMouseEncoder.Encode(0, 0, TerminalMouseButton.Left, TerminalMouseAction.Press, TerminalModes.Default));
    }
}
