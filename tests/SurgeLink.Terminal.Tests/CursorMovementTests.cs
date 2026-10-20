namespace SurgeLink.Terminal.Tests;

public sealed class CursorMovementTests
{
    private static TestTerminal Create() => new(10, 5);

    [Fact]
    public void CursorPositionIsOneBased()
    {
        var terminal = Create();
        terminal.Feed("\u001b[3;4HX");
        Assert.Equal("   X", terminal.Line(2));
        Assert.Equal(2, terminal.Cursor.Row);
        Assert.Equal(4, terminal.Cursor.Column);
    }

    [Fact]
    public void CursorPositionDefaultsToHome()
    {
        var terminal = Create();
        terminal.Feed("\u001b[HX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void CursorPositionClampsToScreen()
    {
        var terminal = Create();
        terminal.Feed("\u001b[99;99HX");
        Assert.Equal("         X", terminal.Line(4));
    }

    [Fact]
    public void CursorUpMovesAndClampsAtTop()
    {
        var terminal = Create();
        terminal.Feed("\u001b[5;5H\u001b[2AX");
        Assert.Equal("    X", terminal.Line(2));
        terminal.Feed("\u001b[99AX");
        Assert.Equal("     X", terminal.Line(0));
    }

    [Fact]
    public void CursorDownMovesAndClampsAtBottom()
    {
        var terminal = Create();
        terminal.Feed("\u001b[1;1H\u001b[2BX");
        Assert.Equal("X", terminal.Line(2));
        terminal.Feed("\u001b[99BX");
        Assert.Equal(" X", terminal.Line(4));
    }

    [Fact]
    public void CursorForwardMovesRight()
    {
        var terminal = Create();
        terminal.Feed("\u001b[3CX");
        Assert.Equal("   X", terminal.Line(0));
    }

    [Fact]
    public void CursorBackwardClampsAtZero()
    {
        var terminal = Create();
        terminal.Feed("\u001b[5C\u001b[99DX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void CursorNextLineMovesDownAndToColumnZero()
    {
        var terminal = Create();
        terminal.Feed("\u001b[3;5H\u001b[2EX");
        Assert.Equal("X", terminal.Line(4));
    }

    [Fact]
    public void CursorPreviousLineMovesUpAndToColumnZero()
    {
        var terminal = Create();
        terminal.Feed("\u001b[5;5H\u001b[2FX");
        Assert.Equal("X", terminal.Line(2));
    }

    [Fact]
    public void ColumnPositionAbsoluteMovesWithinRow()
    {
        var terminal = Create();
        terminal.Feed("\u001b[3;2H\u001b[5GX");
        Assert.Equal("    X", terminal.Line(2));
    }

    [Fact]
    public void VerticalPositionAbsoluteMovesWithinColumn()
    {
        var terminal = Create();
        terminal.Feed("\u001b[3d");
        Assert.Equal(0, terminal.Cursor.Column);
        terminal.Feed("X");
        Assert.Equal("X", terminal.Line(2));
    }

    [Fact]
    public void CursorStateReportsPositionAndAppearance()
    {
        var terminal = Create();
        terminal.Feed("\u001b[4;7H");
        Assert.Equal(new TerminalCursor(3, 6, true, TerminalCursorShape.Block), terminal.Cursor);
    }

    [Fact]
    public void HorizontalPositionAbsoluteAliasWorks()
    {
        var terminal = Create();
        terminal.Feed("\u001b[4\u0060X");
        Assert.Equal("   X", terminal.Line(0));
    }
}
