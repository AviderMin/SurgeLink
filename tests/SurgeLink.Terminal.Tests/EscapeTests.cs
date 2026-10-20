namespace SurgeLink.Terminal.Tests;

public sealed class EscapeTests
{
    [Fact]
    public void SaveRestoreCursorMovesBack()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[3;4H\u001b7\u001b[1;1H\u001b8X");
        Assert.Equal("   X", terminal.Line(2));
    }

    [Fact]
    public void SaveRestoreCursorRestoresPen()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[31m\u001b7\u001b[0m\u001b8X");
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 0).Foreground);
    }

    [Fact]
    public void RestoreWithoutSaveResetsToHome()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[3;3H\u001b[31m\u001b8X");
        Assert.Equal("X", terminal.Line(0));
        Assert.Equal(TerminalColor.Default, terminal.Cell(0, 0).Foreground);
    }

    [Fact]
    public void IndexPreservesColumn()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("ab\u001bDX");
        Assert.Equal("ab", terminal.Line(0));
        Assert.Equal("  X", terminal.Line(1));
    }

    [Fact]
    public void NextLineReturnsToColumnZero()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("ab\u001bEX");
        Assert.Equal("X", terminal.Line(1));
    }

    [Fact]
    public void ReverseIndexAtTopScrollsDown()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("0\r\n1\r\n2");
        terminal.Feed("\u001b[1;1H\u001bM");
        Assert.Equal(string.Empty, terminal.Line(0));
        Assert.Equal("0", terminal.Line(1));
        Assert.Equal("1", terminal.Line(2));
    }

    [Fact]
    public void HorizontalTabSetAddsCustomStop()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[1;4H\u001bH\u001b[1;1H\tX");
        Assert.Equal("   X", terminal.Line(0));
    }

    [Fact]
    public void TabClearRemovesCurrentStop()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[1;9H\u001b[g\u001b[1;1H\tX");
        Assert.Equal("                X", terminal.Line(0));
    }

    [Fact]
    public void TabClearAllRemovesEveryStop()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[3g\u001b[1;1H\tX");
        Assert.Equal("                   X", terminal.Line(0));
    }

    [Fact]
    public void ResetClearsScreenAndModesButKeepsScrollback()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("a\r\nb\r\nc\r\nd");
        Assert.Equal(1, terminal.Emulator.ScrollbackLength);
        terminal.Feed("\u001b[?1h\u001b[31m");
        terminal.Feed("\u001bc");
        Assert.All(terminal.Lines, line => Assert.Equal(string.Empty, line));
        Assert.False(terminal.Modes.ApplicationCursorKeys);
        Assert.True(terminal.Modes.AutoWrap);
        Assert.Equal(1, terminal.Emulator.ScrollbackLength);
        Assert.Equal(0, terminal.Cursor.Row);
    }

    [Fact]
    public void CharacterSetDesignationIsConsumed()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b(Babc");
        terminal.Feed("\u001b)0def");
        terminal.Feed("\u001b*Bgh");
        Assert.Equal("abcdefgh", terminal.Line(0));
    }

    [Fact]
    public void ScreenAlignmentPatternFillsWithE()
    {
        var terminal = new TestTerminal(10, 5);
        terminal.Feed("\u001b#8");
        Assert.Equal("EEEEEEEEEE", terminal.Line(0));
        Assert.Equal("EEEEEEEEEE", terminal.Line(4));
    }

    [Fact]
    public void KeypadModesAreIgnored()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b=abc\u001b>def");
        Assert.Equal("abcdef", terminal.Line(0));
    }

    [Fact]
    public void SingleShiftSequencesAreIgnored()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bNabc\u001bOdef");
        Assert.Equal("abcdef", terminal.Line(0));
    }
}
