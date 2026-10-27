namespace SurgeLink.Terminal.Tests;

/// <summary>
/// Regressions found while auditing the emulator and the input encoders against
/// xterm, instead of against the behaviour the other tests happened to lock in.
/// </summary>
public sealed class TerminalAuditTests
{
    [Fact]
    public void HorizontalTabSkipsAStopCoveredByAWideGlyph()
    {
        var terminal = new TestTerminal(24, 3);

        // One feed: the cell flags are only correct right after the glyph is printed.
        terminal.Feed("1234567\u001b[1;8H中");
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 7).Width);
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 8).Width);

        terminal.Feed("\u001b[1;1H\t");

        // Column 8 is the default stop after 0 but the glyph owns it as its right half.
        // Stopping there left the cursor on a continuation cell, and the next character
        // then erased half of "中"; the tab has to go on to the next real stop.
        Assert.Equal(16, terminal.Cursor.Column);
        terminal.Feed("X");
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 7).Width);
        Assert.Equal("X", terminal.CellText(0, 16));
    }

    [Fact]
    public void TiltWheelUsesDistinctRightCodeInSgrReports()
    {
        var terminal = new TestTerminal(80, 24);
        terminal.Feed("\u001b[?1003;1006h");

        // xterm numbers the wheel as 64 plus the direction: 66 left, 67 right. Reporting
        // both as 66 made every tilt-right arrive at the application as a tilt-left.
        Assert.Equal(66, (int)TerminalMouseButton.WheelLeft);
        Assert.Equal(67, (int)TerminalMouseButton.WheelRight);
        Assert.Equal("\u001b[<66;7;5M", Encode(terminal, TerminalMouseButton.WheelLeft));
        Assert.Equal("\u001b[<67;7;5M", Encode(terminal, TerminalMouseButton.WheelRight));
    }

    [Fact]
    public void ClassicReportsOmitTiltWheelBecauseItsCodeMeansAButton()
    {
        var modes = TerminalModes.Default with { MouseTracking = true };

        // Without SGR, buttons 6 and 7 would encode as 66/67, which already mean button 3
        // pressed and released, so the application saw a phantom click. The vertical
        // wheel is 64/65, which no real button uses, and stays legal.
        Assert.Null(TerminalMouseEncoder.Encode(3, 6, TerminalMouseButton.WheelLeft, TerminalMouseAction.Wheel, modes));
        Assert.Null(TerminalMouseEncoder.Encode(3, 6, TerminalMouseButton.WheelRight, TerminalMouseAction.Wheel, modes));
        Assert.NotNull(TerminalMouseEncoder.Encode(3, 6, TerminalMouseButton.WheelUp, TerminalMouseAction.Wheel, modes));
        Assert.NotNull(TerminalMouseEncoder.Encode(3, 6, TerminalMouseButton.WheelDown, TerminalMouseAction.Wheel, modes));
    }

    private static string Encode(TestTerminal terminal, TerminalMouseButton button)
    {
        byte[]? bytes = TerminalMouseEncoder.Encode(6, 4, button, TerminalMouseAction.Wheel, terminal.Modes);
        Assert.NotNull(bytes);
        return System.Text.Encoding.ASCII.GetString(bytes!);
    }
}
