using System.Text;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Core.Tests;

public sealed class TerminalOutputBufferTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4097)]
    public void WrappedOutputMatchesDecodedTailAndPagesWithoutSplittingSurrogates(int capacity)
    {
        var output = new TerminalOutputBuffer(capacity);
        var decoded = new StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            var chunk = i % 3 == 0 ? new string('x', 5000) + "🙂中" : $"{i}🙂abc";
            decoded.Append(chunk);
            var bytes = Encoding.UTF8.GetBytes(chunk);
            // Also exercise partial UTF-8 sequences between appends.
            for (var offset = 0; offset < bytes.Length; offset += 13)
                output.Append(bytes.AsSpan(offset, Math.Min(13, bytes.Length - offset)));

            var expected = decoded.ToString(Math.Max(0, decoded.Length - capacity), Math.Min(capacity, decoded.Length));
            if (char.IsLowSurrogate(expected[0])) expected = expected[1..];
            var actual = output.Read(null);
            Assert.Equal(expected, actual.Text);
            Assert.Equal(decoded.Length, actual.Cursor);
            Assert.Equal(decoded.Length - expected.Length, actual.OldestCursor);

            var paged = new StringBuilder();
            var page = output.Read(null, 3);
            paged.Append(page.Text);
            while (page.HasMore)
            {
                page = output.Read(page.Cursor, 3);
                paged.Append(page.Text);
            }
            Assert.Equal(expected, paged.ToString());
        }
    }
}
