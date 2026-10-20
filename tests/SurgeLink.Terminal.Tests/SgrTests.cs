namespace SurgeLink.Terminal.Tests;

public sealed class SgrTests
{
    private static TerminalCell CellAfter(string sequence)
    {
        var terminal = new TestTerminal(20, 4);
        terminal.Feed(sequence + "X");
        return terminal.Cell(0, 0);
    }

    [Theory]
    [InlineData("\u001b[1m", CellAttributes.Bold)]
    [InlineData("\u001b[2m", CellAttributes.Dim)]
    [InlineData("\u001b[3m", CellAttributes.Italic)]
    [InlineData("\u001b[4m", CellAttributes.Underline)]
    [InlineData("\u001b[5m", CellAttributes.Blink)]
    [InlineData("\u001b[7m", CellAttributes.Inverse)]
    [InlineData("\u001b[8m", CellAttributes.Hidden)]
    [InlineData("\u001b[9m", CellAttributes.Strikethrough)]
    [InlineData("\u001b[21m", CellAttributes.Underline)]
    public void SimpleAttributesAreApplied(string sequence, CellAttributes expected)
    {
        Assert.Equal(expected, CellAfter(sequence).Attributes);
    }

    [Theory]
    [InlineData("\u001b[1;2m\u001b[22m")]
    [InlineData("\u001b[3m\u001b[23m")]
    [InlineData("\u001b[4m\u001b[24m")]
    [InlineData("\u001b[5m\u001b[25m")]
    [InlineData("\u001b[7m\u001b[27m")]
    [InlineData("\u001b[8m\u001b[28m")]
    [InlineData("\u001b[9m\u001b[29m")]
    public void AttributeResetCodesClearTheirAttribute(string sequence)
    {
        Assert.Equal(CellAttributes.None, CellAfter(sequence).Attributes);
    }

    [Theory]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(37, 7)]
    [InlineData(90, 8)]
    [InlineData(97, 15)]
    public void ForegroundIndexIsApplied(int code, int expectedIndex)
    {
        TerminalCell cell = CellAfter($"\u001b[{code}m");
        Assert.Equal(TerminalColorKind.Indexed, cell.Foreground.Kind);
        Assert.Equal((byte)expectedIndex, cell.Foreground.Index);
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(42, 2)]
    [InlineData(47, 7)]
    [InlineData(100, 8)]
    [InlineData(107, 15)]
    public void BackgroundIndexIsApplied(int code, int expectedIndex)
    {
        TerminalCell cell = CellAfter($"\u001b[{code}m");
        Assert.Equal(TerminalColorKind.Indexed, cell.Background.Kind);
        Assert.Equal((byte)expectedIndex, cell.Background.Index);
    }

    [Fact]
    public void DefaultColourCodesRestoreThemeDefaults()
    {
        TerminalCell cell = CellAfter("\u001b[31;42m\u001b[39;49m");
        Assert.Equal(TerminalColor.Default, cell.Foreground);
        Assert.Equal(TerminalColor.Default, cell.Background);
    }

    [Fact]
    public void Indexed256Foreground()
    {
        TerminalCell cell = CellAfter("\u001b[38;5;196m");
        Assert.Equal(TerminalColorKind.Indexed, cell.Foreground.Kind);
        Assert.Equal((byte)196, cell.Foreground.Index);
    }

    [Fact]
    public void Indexed256Background()
    {
        TerminalCell cell = CellAfter("\u001b[48;5;17m");
        Assert.Equal(TerminalColorKind.Indexed, cell.Background.Kind);
        Assert.Equal((byte)17, cell.Background.Index);
    }

    [Fact]
    public void TrueColourForeground()
    {
        TerminalCell cell = CellAfter("\u001b[38;2;10;20;30m");
        Assert.Equal(TerminalColorKind.Rgb, cell.Foreground.Kind);
        Assert.Equal(0x0A141Eu, cell.Foreground.Rgb);
    }

    [Fact]
    public void TrueColourBackground()
    {
        TerminalCell cell = CellAfter("\u001b[48;2;255;0;128m");
        Assert.Equal(TerminalColorKind.Rgb, cell.Background.Kind);
        Assert.Equal(0xFF0080u, cell.Background.Rgb);
    }

    [Fact]
    public void ColonIndexedForeground()
    {
        TerminalCell cell = CellAfter("\u001b[38:5:196m");
        Assert.Equal(TerminalColorKind.Indexed, cell.Foreground.Kind);
        Assert.Equal((byte)196, cell.Foreground.Index);
    }

    [Fact]
    public void ColonTrueColourForeground()
    {
        TerminalCell cell = CellAfter("\u001b[38:2:10:20:30m");
        Assert.Equal(TerminalColorKind.Rgb, cell.Foreground.Kind);
        Assert.Equal(0x0A141Eu, cell.Foreground.Rgb);
    }

    [Fact]
    public void ColonTrueColourSkipsColourSpaceIdentifier()
    {
        TerminalCell cell = CellAfter("\u001b[38:2:0:1:2:3m");
        Assert.Equal(TerminalColorKind.Rgb, cell.Foreground.Kind);
        Assert.Equal(0x010203u, cell.Foreground.Rgb);
    }

    [Fact]
    public void ResetCodeRestoresDefaults()
    {
        TerminalCell cell = CellAfter("\u001b[1;4;31m\u001b[0m");
        Assert.Equal(CellAttributes.None, cell.Attributes);
        Assert.Equal(TerminalColor.Default, cell.Foreground);
        Assert.Equal(TerminalColor.Default, cell.Background);
    }

    [Fact]
    public void MultipleCodesInOneSequenceAreApplied()
    {
        TerminalCell cell = CellAfter("\u001b[1;4;31m");
        Assert.True((cell.Attributes & CellAttributes.Bold) != 0);
        Assert.True((cell.Attributes & CellAttributes.Underline) != 0);
        Assert.Equal(TerminalColor.FromIndex(1), cell.Foreground);
    }

    [Fact]
    public void UnderlineColourIsConsumedWithoutBecomingBlink()
    {
        TerminalCell cell = CellAfter("\u001b[58;5;1m");
        Assert.Equal(CellAttributes.None, cell.Attributes);
        Assert.Equal(TerminalColor.Default, cell.Foreground);
        Assert.Equal(TerminalColor.Default, cell.Background);
    }

    [Fact]
    public void ExtendedColourWithoutArgumentsIsIgnored()
    {
        TerminalCell cell = CellAfter("\u001b[38m");
        Assert.Equal(TerminalColor.Default, cell.Foreground);
    }

    [Fact]
    public void SgrStatePersistsAcrossLines()
    {
        var terminal = new TestTerminal(20, 4);
        terminal.Feed("\u001b[31mred\r\nstill");
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 0).Foreground);
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(1, 0).Foreground);
    }

    [Fact]
    public void EmptySgrResetsPen()
    {
        TerminalCell cell = CellAfter("\u001b[1m\u001b[m");
        Assert.Equal(CellAttributes.None, cell.Attributes);
    }
}
