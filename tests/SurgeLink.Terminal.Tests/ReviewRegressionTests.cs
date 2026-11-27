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

    [Theory]
    [InlineData("\u001b[1@")]
    [InlineData("\u001b[1P")]
    public void InsertAndDeleteCharactersClearSoftWrapMetadata(string operation)
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("0123456789AB");
        Assert.Equal("0123456789AB", terminal.Emulator.GetSelectionText(new(new(0, 0), new(1, 1))));

        terminal.Feed("\u001b[1;1H" + operation);
        var copied = terminal.Emulator.GetSelectionText(new(new(0, 0), new(1, 1)));
        Assert.Contains('\n', copied);
    }

    [Theory]
    [InlineData("\u001b[1L")]
    [InlineData("\u001b[1M")]
    public void InsertAndDeleteLinesResetCursorColumnAndPendingWrap(string operation)
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("0123456789");
        Assert.Equal(9, terminal.Cursor.Column);

        terminal.Feed(operation + "X");
        Assert.Equal("X", terminal.CellText(0, 0));
        Assert.Equal(1, terminal.Cursor.Column);
        Assert.Equal(0, terminal.Cursor.Row);
    }

    [Fact]
    public void CsiIntermediateGuardsIgnoreMalformedSequences()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("1\r\n2\r\n3\r\n4");
        terminal.Feed("\u001b[1;1H");
        terminal.Feed("\u001b[?31m");   // private marker must not change SGR
        terminal.Feed("\u001b[=1 q");    // private marker must not change DECSCUSR cursor
        terminal.Feed("\u001b[> !p");    // private marker must not trigger DECSTR
        terminal.Feed("\u001b[2;3 r");   // intermediate space must not set DECSTBM
        terminal.Feed("A");

        Assert.Equal(TerminalColor.Default, terminal.Cell(0, 0).Foreground);
        Assert.Equal(TerminalCursorShape.Block, terminal.Modes.CursorShape);
        Assert.False(terminal.Modes.CursorBlink);

        // Verify scrolling still uses the full display (DECSTBM was ignored).
        terminal.Feed("\u001b[4;1H\nZ");
        Assert.Equal("Z", terminal.Line(3));
        Assert.Equal("2", terminal.Line(0));
    }

    [Fact]
    public void AlternateScreenSelectionDoesNotLeakPrimaryScrollback()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("secret\r\nsecond\r\nthird");
        Assert.Equal(1, terminal.Emulator.ScrollbackLength);

        terminal.Feed("\u001b[?1049h\u001b[HALT");
        var copied = terminal.Emulator.GetSelectionText(new(new(-1, 0), new(0, 9)));
        var scrollbackOnly = terminal.Emulator.GetSelectionText(new(new(-2, 0), new(-1, 9)));

        Assert.DoesNotContain("secret", copied);
        Assert.Equal("ALT", copied);
        Assert.Equal(string.Empty, scrollbackOnly);
    }

    [Fact]
    public void SelectionStartingOnWideContinuationCopiesLeadingWideGlyph()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("A中B");

        var copied = terminal.Emulator.GetSelectionText(new(new(0, 2), new(0, 3)));

        Assert.Equal("中B", copied);
    }

    [Fact]
    public void WordSelectionHandlesSingleCharsWideGlyphsAndPunctuation()
    {
        var terminal = new TestTerminal(30, 2);
        terminal.Feed("a 中文字 /var/log/a-b_c:1 ");

        Assert.Equal((0, 0), TerminalWordSelection.FindWordBounds(terminal.Emulator, 0, 0));
        Assert.Null(TerminalWordSelection.FindWordBounds(terminal.Emulator, 0, 1));
        Assert.Equal((2, 7), TerminalWordSelection.FindWordBounds(terminal.Emulator, 0, 5));
        Assert.Equal((9, 24), TerminalWordSelection.FindWordBounds(terminal.Emulator, 0, 19));
        Assert.Null(TerminalWordSelection.FindWordBounds(terminal.Emulator, 0, 25));
        Assert.Null(TerminalWordSelection.FindWordBounds(terminal.Emulator, -1, 0));
        Assert.Null(TerminalWordSelection.FindWordBounds(terminal.Emulator, 0, 99));
    }

    [Fact]
    public void XtgettcapRejectsNonHexAndOddLengthQueriesWithoutEchoingControls()
    {
        var terminal = new TestTerminal(40, 2);
        var replies = new List<string>();
        terminal.Emulator.ResponseRequested += replies.Add;

        terminal.Feed("\u001bP+qid\r;123;524742\u001b\\");

        Assert.Equal(new[] { "\u001bP1+r524742=38\u001b\\" }, replies);
    }

    [Fact]
    public void AbortingOscOrDcsWithIntermediateEscExecutesFollowingSequence()
    {
        var oscTerminal = new TestTerminal(10, 2);
        oscTerminal.Feed("\u001b]0;title\u001b(0lqk");
        Assert.Equal("┌─┐", oscTerminal.Line(0));

        var dcsTerminal = new TestTerminal(4, 2);
        dcsTerminal.Feed("\u001bPfoo\u001b#8");
        Assert.Equal("EEEE", dcsTerminal.Line(0));
        Assert.Equal("EEEE", dcsTerminal.Line(1));
    }

    [Fact]
    public void OverwritingWideWrapPaddingPreservesCharacterAcrossReflowAndCopy()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Emulator.ReflowOnResize = true;
        terminal.Feed("abcd中");
        terminal.Feed("\u001b[1;5HX");

        var copied = terminal.Emulator.GetSelectionText(new(new(0, 0), new(1, 1)));
        Assert.Contains('X', copied);

        terminal.Emulator.Resize(10, 3);
        Assert.Equal("abcdX", terminal.Line(0));
        Assert.Equal("中", terminal.Line(1));
    }

    [Fact]
    public void ClearingPrimaryScreenBreaksScrollbackWrapBeforeReflow()
    {
        var terminal = new TestTerminal(5, 2);
        terminal.Emulator.ReflowOnResize = true;
        terminal.Feed("0123456789\r\nab");
        Assert.True(terminal.Emulator.ScrollbackLength >= 1);

        terminal.Feed("\u001b[2J\u001b[H$ ls");
        terminal.Emulator.Resize(20, 2);

        Assert.Equal("01234", terminal.Line(0));
        Assert.Equal("$ ls", terminal.Line(1));
    }

    [Fact]
    public void CsiSubparametersIntermediatesEraseModesAndDecscusrGuardsWork()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("hello");
        terminal.Feed("\u001b[?25 l");           // intermediate space must not hide cursor
        terminal.Feed("\u001b# 8");              // 2 intermediates must not trigger DECALN
        terminal.Feed("\u001b[5J\u001b[4K");     // invalid ED/EL modes must not erase
        terminal.Feed("\u001b[1:2H");            // subparameter must not move cursor
        terminal.Feed("\u001b[99 q");            // invalid DECSCUSR must be ignored
        terminal.Feed("\u001b[38;5;4:3mA");      // trailing subparam must not enable Italic
        terminal.Feed("\u001b[0m\u001b[38;2;10:1;20;30mB"); // malformed RGB must not leak SGR 30

        Assert.True(terminal.Modes.CursorVisible);
        Assert.Equal(TerminalCursorShape.Block, terminal.Modes.CursorShape);
        Assert.False(terminal.Modes.CursorBlink);
        Assert.StartsWith("hello", terminal.Line(0));
        Assert.Equal(CellAttributes.None, terminal.Cell(0, 5).Attributes);
        Assert.Equal(TerminalColor.FromIndex(4, isExplicit: true), terminal.Cell(0, 5).Foreground);
        Assert.Equal(TerminalColor.Default, terminal.Cell(0, 6).Foreground);
    }

    [Fact]
    public void Utf8DecoderRejectsInvalidSecondBytesPerMaximalSubpart()
    {
        var terminal = new TestTerminal(20, 2);
        terminal.Emulator.Feed([0xED, 0xA0, 0x80]);
        Assert.Equal("\uFFFD\uFFFD\uFFFD", terminal.Line(0));

        var split = new TestTerminal(20, 2);
        split.Emulator.Feed([0xED, 0xA0]);
        split.Emulator.Feed([0xC3, 0xA9]);
        Assert.Equal("\uFFFD\uFFFDé", split.Line(0));
    }

    [Fact]
    public void AnsiParserExecutesEmbeddedC0AndRecoversUtf8AfterEsc()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("row0\u001b[31\r\nmX");
        Assert.Equal("X", terminal.CellText(1, 0));
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(1, 0).Foreground);

        var utf8 = new TestTerminal(10, 2);
        utf8.Feed("\u001b中X");
        Assert.Equal("中X", utf8.Line(0));
    }
}
