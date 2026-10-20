using System.Text;

namespace SurgeLink.Terminal.Tests;

public sealed class Utf8DecodingTests
{
    [Fact]
    public void TwoByteSequenceAcrossFeeds()
    {
        var terminal = new TestTerminal();
        terminal.Feed(0xC3);
        Assert.Equal(string.Empty, terminal.Line(0));
        terminal.Feed(0xA9);
        Assert.Equal("é", terminal.Line(0));
    }

    [Fact]
    public void ThreeByteSequenceSplitEveryByte()
    {
        var terminal = new TestTerminal();
        terminal.Feed(0xE2);
        terminal.Feed(0x82);
        Assert.Equal(string.Empty, terminal.Line(0));
        terminal.Feed(0xAC);
        Assert.Equal("€", terminal.Line(0));
    }

    [Fact]
    public void FourByteEmojiIsWide()
    {
        var terminal = new TestTerminal();
        terminal.Feed(Encoding.UTF8.GetBytes("😀"));
        Assert.Equal("😀", terminal.Line(0));
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 0).Width);
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 1).Width);
        Assert.Equal(2, terminal.Cursor.Column);
    }

    [Fact]
    public void LoneContinuationByteBecomesReplacement()
    {
        var terminal = new TestTerminal();
        terminal.Feed(0x80);
        Assert.Equal("\uFFFD", terminal.Line(0));
    }

    [Fact]
    public void InvalidStartByteBecomesReplacement()
    {
        var terminal = new TestTerminal();
        terminal.Feed(0xFF);
        Assert.Equal("\uFFFD", terminal.Line(0));
    }

    [Fact]
    public void PendingSequenceIsFlushedWhenAsciiArrives()
    {
        var terminal = new TestTerminal();
        terminal.Feed(0xE2);
        terminal.Feed("A");
        Assert.Equal("\uFFFDA", terminal.Line(0));
    }

    [Fact]
    public void PendingSequenceIsFlushedWhenEscapeArrives()
    {
        var terminal = new TestTerminal();
        terminal.Feed(0xE2);
        terminal.Feed("\u001b[31mX");
        Assert.Equal("\uFFFDX", terminal.Line(0));
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 1).Foreground);
    }

    [Fact]
    public void InvalidContinuationBytesBecomeReplacements()
    {
        var terminal = new TestTerminal();
        terminal.Feed(0xE2);
        terminal.Feed("(");
        terminal.Feed(0x8C);
        terminal.Feed("(");
        Assert.Equal("\uFFFD(\uFFFD(", terminal.Line(0));
    }

    [Fact]
    public void CombiningMarkAppendsToPreviousCell()
    {
        var terminal = new TestTerminal();
        terminal.Feed("e\u0301");
        Assert.Equal("e\u0301", terminal.CellText(0, 0));
        Assert.Equal(TerminalCell.Blank, terminal.Cell(0, 1));
        Assert.Equal(1, terminal.Cursor.Column);
    }

    [Fact]
    public void C1ControlDecodedFromUtf8IsIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed(new byte[] { 0xC2, 0x85 });
        Assert.Equal(string.Empty, terminal.Line(0));
        Assert.Equal(0, terminal.Cursor.Column);
    }

    [Fact]
    public void MultipleScriptsRenderWithCorrectWidths()
    {
        var terminal = new TestTerminal();
        terminal.Feed("aé中😀");
        Assert.Equal("aé中😀", terminal.Line(0));
        Assert.Equal(CellWidth.Narrow, terminal.Cell(0, 1).Width);
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 2).Width);
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 3).Width);
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 4).Width);
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 5).Width);
    }

    [Fact]
    public void Utf8SequenceSplitAcrossManyFeedsRendersOnce()
    {
        var terminal = new TestTerminal();
        byte[] bytes = Encoding.UTF8.GetBytes("中");
        foreach (byte value in bytes)
        {
            terminal.Feed(value);
        }

        Assert.Equal("中", terminal.Line(0));
    }
}
