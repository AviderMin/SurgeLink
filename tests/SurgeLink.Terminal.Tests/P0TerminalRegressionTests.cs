using System.Text;

namespace SurgeLink.Terminal.Tests;

/// <summary>
/// Regressions for the P0 terminal-compatibility fixes: REP (CSI b), DEC special
/// graphics, SGR underline styles, private-CSI guarding, invalid UTF-8 inside a
/// CSI and CRLF normalisation on paste.
/// </summary>
public sealed class P0TerminalRegressionTests
{
    [Fact]
    public void RepeatPrecedingCharacterRepeatsTheLastGraphic()
    {
        var terminal = new TestTerminal(20, 3);

        // CSI b (REP) is emitted by several TUIs to draw rules and bars.
        terminal.Feed("A\u001b[4b");
        Assert.Equal("AAAAA", terminal.Line(0));
        Assert.Equal(5, terminal.Cursor.Column);
    }

    [Fact]
    public void RepeatPrecedingCharacterDefaultsToOneAndIsClamped()
    {
        var terminal = new TestTerminal(20, 3);

        terminal.Feed("X\u001b[b");
        Assert.Equal("XX", terminal.Line(0));

        // An absurd count must not hang the parser.
        terminal.Feed("\u001b[999999b");
        Assert.Equal(20, terminal.Line(0).Length);
    }

    [Fact]
    public void RepeatPrecedingCharacterDoesNothingBeforeAnyGlyph()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("\u001b[3b");
        Assert.Equal(string.Empty, terminal.Line(0));
        Assert.Equal(0, terminal.Cursor.Column);
    }

    [Fact]
    public void RepeatPrecedingCharacterRepeatsWideGlyphs()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("中\u001b[2b");
        Assert.Equal("中中中", terminal.Line(0));
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 2).Width);
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 3).Width);
    }

    [Fact]
    public void DecSpecialGraphicsMapBoxDrawingWhenG0IsDesignated()
    {
        var terminal = new TestTerminal(20, 3);

        // ESC ( 0 selects the DEC special graphics set into G0, as ncurses does for
        // a UTF-8-less terminal; ESC ( B restores ASCII.
        terminal.Feed("\u001b(0lqk\u001b(B");
        Assert.Equal("┌─┐", terminal.Line(0));

        terminal.Feed("x");
        Assert.Equal("┌─┐x", terminal.Line(0));
    }

    [Fact]
    public void ShiftOutSelectsG1LineDrawingAndShiftInRestoresAscii()
    {
        var terminal = new TestTerminal(20, 3);

        // ESC ) 0 designates G1; SO (0x0E) invokes it and SI (0x0F) returns to G0.
        terminal.Feed("\u001b)0\u000Ex\u000Fy");
        Assert.Equal("│y", terminal.Line(0));
    }

    [Fact]
    public void LineDrawingStateResetsOnReset()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("\u001b(0");
        terminal.Emulator.Reset();
        terminal.Feed("q");
        Assert.Equal("q", terminal.Line(0));
    }

    [Fact]
    public void PrivateCsiSequencesAreNotExecutedAsStandardSequences()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("abcdef");

        // CSI ? 2 J is a private erase-display variant; a standard CSI 2 J would
        // wipe the screen. The guard must keep the private form from doing that.
        terminal.Feed("\u001b[?2J");
        Assert.Equal("abcdef", terminal.Line(0));

        // The same guard covers cursor movement: CSI > 5 C must not move right.
        terminal.Feed("\u001b[>5C");
        Assert.Equal(6, terminal.Cursor.Column);

        // A standard sequence still works.
        terminal.Feed("\u001b[2C");
        Assert.Equal(8, terminal.Cursor.Column);
    }

    [Fact]
    public void IntermediateBytesBlockStandardCsiExecution()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("abc");

        // CSI 2 SP @ carries the SP intermediate; it must not insert characters.
        terminal.Feed("\u001b[2 @");
        Assert.Equal("abc", terminal.Line(0));
    }

    [Fact]
    public void UnderlineStyleSubparameterIsAcceptedWithoutBreakingFollowingSgr()
    {
        var terminal = new TestTerminal(20, 3);

        // 4:3 is a curly underline; the ":3" must be consumed as a subparameter
        // instead of being read as a separate colour/style code.
        terminal.Feed("\u001b[4:3mA");
        Assert.True(terminal.Cell(0, 0).Attributes.HasFlag(CellAttributes.Underline));

        // 4:0 turns the underline back off.
        terminal.Feed("\u001b[4:0mB");
        Assert.False(terminal.Cell(0, 1).Attributes.HasFlag(CellAttributes.Underline));

        // And a normal colour code after the subparameter still applies.
        terminal.Feed("\u001b[4:1;31mC");
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 2).Foreground);
    }

    [Fact]
    public void NonAsciiByteInsideCsiRecoversToGround()
    {
        var terminal = new TestTerminal(20, 3);

        // A truncated escape sequence followed by UTF-8 text used to swallow the
        // whole rest of the line while the parser stayed in the CSI state.
        terminal.Feed("\u001b[3中X");
        Assert.Equal("中X", terminal.Line(0));
    }

    [Fact]
    public void PasteNormalizesCrlfWithoutBracketedPaste()
    {
        TerminalModes modes = TerminalModes.Default;
        byte[] encoded = TerminalInputEncoder.EncodePaste("a\r\nb\r\nc", modes);

        // A shell must not receive CRLF: the carriage return alone is what
        // executes a line, so "a\r\nb" would run the command twice.
        Assert.Equal("a\rb\rc", Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void PasteNormalizesCrlfWithBracketedPaste()
    {
        TerminalModes modes = TerminalModes.Default with { BracketedPaste = true };
        byte[] encoded = TerminalInputEncoder.EncodePaste("a\r\nb", modes);
        Assert.Equal("\u001b[200~a\rb\u001b[201~", Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void PendingWrapIsClearedByEraseOperations()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abcde");

        // The cursor is on the last column with a wrap pending; erasing the line
        // must not leave the next glyph on a new row.
        terminal.Feed("\u001b[K");
        terminal.Feed("X");
        Assert.Equal(0, terminal.Cursor.Row);
    }

    [Fact]
    public void ShrinkingRowsKeepsTheActiveCursorRowVisible()
    {
        var terminal = new TestTerminal(10, 6);
        terminal.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n6");
        Assert.Equal(5, terminal.Cursor.Row);

        terminal.Emulator.Resize(10, 3);
        Assert.Equal(3, terminal.Rows);
        Assert.Equal("4", terminal.Line(0));
        Assert.Equal("5", terminal.Line(1));
        Assert.Equal("6", terminal.Line(2));
        Assert.Equal(2, terminal.Cursor.Row);
        Assert.Equal(3, terminal.Emulator.ScrollbackLength);
    }

    [Fact]
    public void ShrinkingRowsKeepsTheCursorOnItsOriginalContent()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("TOP\r\nROW2\r\nROW3\r\nBOTTOM\u001b[1;1H");

        terminal.Emulator.Resize(10, 2);
        terminal.Feed("X");

        Assert.Equal("XOP", terminal.Line(0));
        Assert.Equal("ROW2", terminal.Line(1));
    }

    [Theory]
    [InlineData("\u200D", "中", 2)]
    [InlineData("A\u200D", "中", 3)]
    [InlineData("👩\u200D", "💻", 2)]
    public void JoinerOnlyCombinesRunesWithinTheSameGrapheme(string prefix, string next, int column)
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed(prefix);
        terminal.Feed(next);

        Assert.Equal(column, terminal.Cursor.Column);
        Assert.Equal(prefix == "\u200D" ? next : prefix + next, terminal.Line(0));
    }

    [Theory]
    [InlineData("?", 0x3F)]
    [InlineData("_", 0x5F)]
    [InlineData("Ctrl+?", 0x7F)]
    [InlineData("Ctrl+/", 0x1F)]
    public void NamedPunctuationEncodesTheRequestedKey(string key, byte expected)
    {
        var chord = TerminalKeyChord.Parse(key);
        Assert.Equal(new byte[] { expected }, TerminalInputEncoder.EncodeKey(
            chord.VirtualKey, chord.Control, chord.Alt, chord.Shift, TerminalModes.Default));
    }

    [Fact]
    public void AlternateScreenShrinkDoesNotPushRowsIntoScrollback()
    {
        var terminal = new TestTerminal(10, 6);
        terminal.Feed("\u001b[?1049h");
        terminal.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n6");

        terminal.Emulator.Resize(10, 3);
        Assert.Equal(3, terminal.Rows);
        Assert.Equal(0, terminal.Emulator.ScrollbackLength);
    }
}
