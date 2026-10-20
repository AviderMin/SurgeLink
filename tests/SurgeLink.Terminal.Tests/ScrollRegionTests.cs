namespace SurgeLink.Terminal.Tests;

public sealed class ScrollRegionTests
{
    private static TestTerminal WithLabelledRows()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[1;1H0\u001b[2;1H1\u001b[3;1H2\u001b[4;1H3\u001b[5;1H4");
        return terminal;
    }

    [Fact]
    public void LineFeedAtBottomMarginScrollsRegionOnly()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[2;4r\u001b[4;1H\n");
        Assert.Equal("0", terminal.Line(0));
        Assert.Equal("2", terminal.Line(1));
        Assert.Equal("3", terminal.Line(2));
        Assert.Equal(string.Empty, terminal.Line(3));
        Assert.Equal("4", terminal.Line(4));
        Assert.Equal(0, terminal.Emulator.ScrollbackLength);
    }

    [Fact]
    public void ScrollUpSequenceIsRestrictedToRegion()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[2;4r\u001b[1S");
        Assert.Equal("0", terminal.Line(0));
        Assert.Equal("2", terminal.Line(1));
        Assert.Equal("3", terminal.Line(2));
        Assert.Equal(string.Empty, terminal.Line(3));
        Assert.Equal("4", terminal.Line(4));
    }

    [Fact]
    public void ScrollDownSequenceIsRestrictedToRegion()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[2;4r\u001b[1T");
        Assert.Equal("0", terminal.Line(0));
        Assert.Equal(string.Empty, terminal.Line(1));
        Assert.Equal("1", terminal.Line(2));
        Assert.Equal("2", terminal.Line(3));
        Assert.Equal("4", terminal.Line(4));
    }

    [Fact]
    public void ReverseIndexAtTopMarginScrollsRegionDown()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[2;4r\u001b[2;1H\u001bM");
        Assert.Equal("0", terminal.Line(0));
        Assert.Equal(string.Empty, terminal.Line(1));
        Assert.Equal("1", terminal.Line(2));
        Assert.Equal("2", terminal.Line(3));
        Assert.Equal("4", terminal.Line(4));
    }

    [Fact]
    public void LineFeedAboveRegionMovesCursorWithoutScrolling()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[2;4r\u001b[1;1H\n");
        Assert.Equal(1, terminal.Cursor.Row);
        Assert.Equal("0", terminal.Line(0));
        Assert.Equal("1", terminal.Line(1));
        Assert.Equal("4", terminal.Line(4));
    }

    [Fact]
    public void CursorDownStopsAtBottomMargin()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[2;4r\u001b[2;1H\u001b[99B");
        Assert.Equal(3, terminal.Cursor.Row);
    }

    [Fact]
    public void CursorUpStopsAtTopMargin()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[2;4r\u001b[4;1H\u001b[99A");
        Assert.Equal(1, terminal.Cursor.Row);
    }

    [Fact]
    public void ResetScrollRegionRestoresFullScreenScrolling()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[2;4r\u001b[r\u001b[5;1H\n");
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal("2", terminal.Line(1));
        Assert.Equal("3", terminal.Line(2));
        Assert.Equal("4", terminal.Line(3));
        Assert.Equal(string.Empty, terminal.Line(4));
    }

    [Fact]
    public void InvalidScrollRegionIsIgnored()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[4;2r\u001b[5;1H\n");
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal("4", terminal.Line(3));
    }

    [Fact]
    public void SettingScrollRegionHomesCursor()
    {
        var terminal = WithLabelledRows();
        terminal.Feed("\u001b[3;3H\u001b[2;4r");
        Assert.Equal(0, terminal.Cursor.Row);
        Assert.Equal(0, terminal.Cursor.Column);
    }
}
