using System.Text;

namespace SurgeLink.Terminal.Tests;

public sealed class ReviewRegressionTests
{
    [Fact]
    public void ExcessCombiningMarksAreBoundedWithoutLosingFollowingCharacters()
    {
        var terminal = new TerminalEmulator(80, 24);
        var bytes = Encoding.UTF8.GetBytes("A" + new string('\u0301', 30000) + "B");
        var before = GC.GetAllocatedBytesForCurrentThread();
        terminal.Feed(bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(terminal.GetCell(0, 0).Text.Length, 2, 64);
        Assert.Equal("B", terminal.GetCell(0, 1).Text);
        Assert.InRange(allocated, 0, 5_000_000);
    }

    [Fact]
    public void CombiningLimitAppliesAcrossFeedBoundariesAndToWideCells()
    {
        var terminal = new TerminalEmulator(10, 2);
        terminal.Feed(Encoding.UTF8.GetBytes("中"));
        for (var index = 0; index < 1000; index++) terminal.Feed(Encoding.UTF8.GetBytes("\u0301"));
        Assert.InRange(terminal.GetCell(0, 0).Text.Length, 2, 64);
        Assert.Equal(CellWidth.Continuation, terminal.GetCell(0, 1).Width);
        terminal.Feed(Encoding.UTF8.GetBytes("X"));
        Assert.Equal("X", terminal.GetCell(0, 2).Text);
    }

    [Theory]
    [InlineData("\u001b[K", 0, 4)]
    [InlineData("\u001b[1K", 0, 0)]
    [InlineData("\u001b[2K", 0, 4)]
    [InlineData("\u001b[2J", 1, 4)]
    [InlineData("\u001b[2X", 0, 1)]
    [InlineData("\u001b[2@", 0, 1)]
    [InlineData("\u001b[2P", 0, 4)]
    [InlineData("\u001b[L", 0, 4)]
    [InlineData("\u001b[M", 1, 4)]
    [InlineData("\u001b[S", 1, 4)]
    [InlineData("\u001b[T", 0, 4)]
    [InlineData("\u001b[2;1H\n", 1, 4)]
    public void EraseAndEditingBlankCellsUseCurrentBackground(string operation, int row, int column)
    {
        var terminal = new TerminalEmulator(5, 2);
        terminal.Feed(Encoding.UTF8.GetBytes("abcde\r\nfghij\u001b[H\u001b[1;41m" + operation));
        var cell = terminal.GetCell(row, column);
        Assert.Equal(" ", cell.Text);
        Assert.Equal(TerminalColor.FromIndex(1), cell.Background);
        Assert.Equal(CellAttributes.None, cell.Attributes);
    }

    [Fact]
    public void EraseUsesUpdatedAndRestoredColours()
    {
        var terminal = new TerminalEmulator(5, 2);
        terminal.Feed(Encoding.UTF8.GetBytes("\u001b[41m\u001b7\u001b[44m\u001b[2K\u001b8\u001b[2J"));
        Assert.Equal(TerminalColor.FromIndex(1), terminal.GetCell(1, 4).Background);
        terminal.Feed(Encoding.UTF8.GetBytes("\u001b[0m\u001b[2J"));
        Assert.Equal(TerminalColor.Default, terminal.GetCell(1, 4).Background);
    }

    [Fact]
    public void ChangingHistoryLimitKeepsNewestLinesAndClampsViewport()
    {
        var terminal = new TerminalEmulator(10, 2, 10);
        terminal.Feed(Encoding.UTF8.GetBytes("1\r\n2\r\n3\r\n4\r\n5\r\n6"));
        terminal.ScrollViewport(4);
        terminal.SetScrollbackLimit(2);
        Assert.Equal(2, terminal.ScrollbackLimit);
        Assert.Equal(2, terminal.ScrollbackLength);
        Assert.Equal(2, terminal.ViewportOffset);
        Assert.Equal("3\n4", terminal.GetVisibleText());
        terminal.SetScrollbackLimit(0);
        Assert.Equal(0, terminal.ScrollbackLength);
        Assert.Equal(0, terminal.ViewportOffset);
        terminal.SetScrollbackLimit(5);
        terminal.Feed(Encoding.UTF8.GetBytes("\r\n7\r\n8\r\n9"));
        Assert.Equal(3, terminal.ScrollbackLength);
    }
}
