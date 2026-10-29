using SurgeLink.Core.Files;

namespace SurgeLink.Core.Tests;

public sealed class EditorLineIndexTests
{
    [Theory]
    [InlineData("", new[] { 0 })]
    [InlineData("one", new[] { 0 })]
    [InlineData("a\r\nb\rc\nd\r\n", new[] { 0, 3, 5, 7, 10 })]
    [InlineData("一\r\n😀\n", new[] { 0, 3, 6 })]
    [InlineData("\n\n", new[] { 0, 1, 2 })]
    public void IndexUsesNativeUtf16OffsetsAndRetainsTrailingEmptyLine(string text, int[] expected)
    {
        var lines = new EditorLineIndex(text);
        Assert.Equal(LineLayout.CountLines(text), lines.Count);
        Assert.Equal(expected, Enumerable.Range(0, lines.Count).Select(line => lines[line]));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(19, 0)]
    [InlineData(20, 1)]
    [InlineData(59, 1)]
    [InlineData(60, 2)]
    [InlineData(80, 3)]
    [InlineData(100, 3)]
    public void VisibleLabelsRespectPartialRowsAndWrappedLines(double viewportTop, int expected)
    {
        var lines = new EditorLineIndex("a\r\nwrapped\r\nz");
        // The second logical line wraps to two visual rows. Its label is on the first.
        var bottoms = new Dictionary<int, double> { [0] = 20, [3] = 60, [12] = 80 };
        Assert.Equal(expected, lines.FirstVisibleLine(offset => bottoms[offset], viewportTop));
    }

    [Fact]
    public void LargeDocumentScrollQueriesLogarithmicNumberOfLines()
    {
        var lines = new EditorLineIndex(string.Concat(Enumerable.Repeat("line\r\n", 100_000)));
        var queries = 0;
        var first = lines.FirstVisibleLine(offset =>
        {
            queries++;
            return (offset / 6 + 1) * 20;
        }, 1_900_000);

        Assert.Equal(95_000, first);
        Assert.InRange(queries, 1, 17);
        Assert.Equal(600_000, lines[100_000]);
    }
}
