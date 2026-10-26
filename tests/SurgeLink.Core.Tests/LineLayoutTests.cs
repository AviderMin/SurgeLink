using SurgeLink.Core.Files;

namespace SurgeLink.Core.Tests;

public sealed class LineLayoutTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("one line", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\nb\nc", 3)]
    [InlineData("a\nb\n", 3)]          // A trailing break opens the final empty line.
    [InlineData("a\n\n", 3)]           // The blank line between is still a line.
    [InlineData("\n", 2)]
    [InlineData("\r\n", 2)]
    [InlineData("a\r\nb\r\n", 3)]
    public void CountLinesCountsTheTrailingEmptyLine(string? text, int expected)
    {
        Assert.Equal(expected, LineLayout.CountLines(text));
    }

    [Theory]
    [InlineData("a\nb\nc", 0, 1)]
    [InlineData("a\nb\nc", 1, 1)]
    [InlineData("a\nb\nc", 2, 2)]
    [InlineData("a\nb\nc", 4, 3)]
    [InlineData("a\nb\nc", 99, 3)]
    [InlineData(null, 5, 1)]
    public void LineAtClampsTheCaretToTheDocument(string? text, int offset, int expected)
    {
        Assert.Equal(expected, LineLayout.LineAt(text, offset));
    }

    [Fact]
    public void GutterTextHasOneLabelPerLine()
    {
        var gutter = LineLayout.GutterText(4);
        Assert.Equal("1\n2\n3\n4", gutter);
        Assert.Equal(4, gutter.Split('\n').Length);
    }

    [Fact]
    public void GutterTextAndCountLinesAgree()
    {
        const string document = "first\nsecond\nthird\n";
        var lineCount = LineLayout.CountLines(document);
        var gutter = LineLayout.GutterText(lineCount);
        Assert.Equal(["1", "2", "3", "4"], gutter.Split('\n'));
        // The gutter and the caret mapping must agree about the final line.
        Assert.Equal(lineCount, LineLayout.LineAt(document, document.Length));
    }

    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\rb", "a b")]
    [InlineData("", "")]
    public void CollapseForSingleLineKeepsEveryCharacterRepresented(string text, string expected)
    {
        var collapsed = LineLayout.CollapseForSingleLine(text);
        Assert.Equal(expected, collapsed);
        Assert.DoesNotContain('\n', collapsed);
        Assert.DoesNotContain('\r', collapsed);
    }
}
