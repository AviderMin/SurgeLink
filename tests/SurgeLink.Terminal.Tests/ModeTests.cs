namespace SurgeLink.Terminal.Tests;

public sealed class ModeTests
{
    [Fact]
    public void ApplicationCursorKeysModeToggles()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[?1h");
        Assert.True(terminal.Modes.ApplicationCursorKeys);
        terminal.Feed("\u001b[?1l");
        Assert.False(terminal.Modes.ApplicationCursorKeys);
    }

    [Fact]
    public void OriginModeAddressesRelativeToScrollRegion()
    {
        var terminal = new TestTerminal(10, 5);
        terminal.Feed("\u001b[2;4r\u001b[?6h");
        Assert.True(terminal.Modes.OriginMode);
        Assert.Equal(1, terminal.Cursor.Row);
        terminal.Feed("\u001b[1;1H");
        Assert.Equal(1, terminal.Cursor.Row);
        terminal.Feed("\u001b[3;1H");
        Assert.Equal(3, terminal.Cursor.Row);
        terminal.Feed("\u001b[?6l");
        Assert.Equal(0, terminal.Cursor.Row);
    }

    [Fact]
    public void AutoWrapOffOverwritesLastColumn()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abcde\u001b[?7lX");
        Assert.Equal("abcdX", terminal.Line(0));
        Assert.Equal(0, terminal.Cursor.Row);
    }

    [Fact]
    public void AutoWrapOnWrapsToNextLine()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abcdeX");
        Assert.Equal("abcde", terminal.Line(0));
        Assert.Equal("X", terminal.Line(1));
    }

    [Fact]
    public void ReenablingAutoWrapRestoresDeferredWrap()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abcde\u001b[?7lX\u001b[?7hY");
        Assert.Equal("abcdX", terminal.Line(0));
        Assert.Equal("Y", terminal.Line(1));
    }

    [Fact]
    public void CursorVisibleModeToggles()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[?25l");
        Assert.False(terminal.Modes.CursorVisible);
        Assert.False(terminal.Cursor.Visible);
        terminal.Feed("\u001b[?25h");
        Assert.True(terminal.Cursor.Visible);
    }

    [Fact]
    public void CursorBlinkModeToggles()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[?12h");
        Assert.True(terminal.Modes.CursorBlink);
        terminal.Feed("\u001b[?12l");
        Assert.False(terminal.Modes.CursorBlink);
    }

    [Fact]
    public void BracketedPasteModeToggles()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[?2004h");
        Assert.True(terminal.Modes.BracketedPaste);
        terminal.Feed("\u001b[?2004l");
        Assert.False(terminal.Modes.BracketedPaste);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void MouseTrackingModesToggleTracking(int mode)
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed($"\u001b[?{mode}h");
        Assert.True(terminal.Modes.MouseTracking);
        terminal.Feed($"\u001b[?{mode}l");
        Assert.False(terminal.Modes.MouseTracking);
    }

    [Fact]
    public void MultiplePrivateModesInOneSequence()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[?1;25l");
        Assert.False(terminal.Modes.ApplicationCursorKeys);
        Assert.False(terminal.Modes.CursorVisible);
        terminal.Feed("\u001b[?1;25h");
        Assert.True(terminal.Modes.ApplicationCursorKeys);
        Assert.True(terminal.Modes.CursorVisible);
    }

    [Fact]
    public void InsertModeShiftsExistingCells()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("abc\r\u001b[4hX");
        Assert.True(terminal.Modes.InsertMode);
        Assert.Equal("Xabc", terminal.Line(0));
    }

    [Fact]
    public void NewLineModeMakesLineFeedReturnToColumnZero()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[20hab\nX");
        Assert.True(terminal.Modes.NewLineMode);
        Assert.Equal("X", terminal.Line(1));
    }

    [Fact]
    public void LineFeedWithoutNewLineModeKeepsColumn()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("ab\nX");
        Assert.Equal("  X", terminal.Line(1));
    }

    [Theory]
    [InlineData(4, TerminalCursorShape.Underline, false)]
    [InlineData(3, TerminalCursorShape.Underline, true)]
    [InlineData(5, TerminalCursorShape.Bar, true)]
    [InlineData(2, TerminalCursorShape.Block, false)]
    public void CursorStyleSequenceChangesShape(int style, TerminalCursorShape shape, bool blink)
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed($"\u001b[{style} q");
        Assert.Equal(shape, terminal.Modes.CursorShape);
        Assert.Equal(blink, terminal.Modes.CursorBlink);
    }

    [Fact]
    public void SoftResetRestoresDefaults()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[1;31m\u001b[?1h\u001b[?6h");
        terminal.Feed("\u001b[!p");
        Assert.False(terminal.Modes.ApplicationCursorKeys);
        Assert.False(terminal.Modes.OriginMode);
        Assert.True(terminal.Modes.AutoWrap);
        Assert.True(terminal.Modes.CursorVisible);
        terminal.Feed("X");
        Assert.Equal("X", terminal.Cell(0, 0).Text);
        Assert.Equal(CellAttributes.None, terminal.Cell(0, 0).Attributes);
        Assert.Equal(TerminalColor.Default, terminal.Cell(0, 0).Foreground);
    }

    [Fact]
    public void ScrollRegionModeReportUsesPrivateMarker()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[?1049h");
        Assert.True(terminal.Modes.AlternateScreen);
        terminal.Feed("\u001b[?1049l");
        Assert.False(terminal.Modes.AlternateScreen);
    }
}
