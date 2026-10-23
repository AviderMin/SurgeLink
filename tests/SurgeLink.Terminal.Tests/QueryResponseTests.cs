using System.Text;
using SurgeLink.Terminal;

namespace SurgeLink.Terminal.Tests;

public sealed class QueryResponseTests
{
    [Theory]
    [InlineData("\u001b[5n", "\u001b[0n")]
    [InlineData("\u001b[3;8H\u001b[6n", "\u001b[3;8R")]
    [InlineData("\u001b[3;8H\u001b[?6n", "\u001b[?3;8R")]
    [InlineData("\u001b[c", "\u001b[?1;0c")]
    [InlineData("\u001b[0c", "\u001b[?1;0c")]
    [InlineData("\u001b[>c", "\u001b[>0;1;0c")]
    [InlineData("\u001bZ", "\u001b[?1;0c")]
    [InlineData("\u001b[4;20r\u001b[?6h\u001b[2;5H\u001b[6n", "\u001b[2;5R")]
    public void QueriesReplyEvenWhenReceivedOneByteAtATime(string input, string expected)
    {
        var terminal = new TerminalEmulator();
        var responses = new List<string>();
        terminal.ResponseRequested += responses.Add;
        foreach (var value in Encoding.ASCII.GetBytes(input)) { terminal.Feed(new byte[] { value }); }
        Assert.Equal(expected, Assert.Single(responses));
        Assert.DoesNotContain(expected, terminal.GetVisibleText());
    }

    [Fact]
    public void UnsupportedQueriesDoNotProduceSpuriousReplies()
    {
        var terminal = new TerminalEmulator();
        var responses = new List<string>();
        terminal.ResponseRequested += responses.Add;
        terminal.Feed(Encoding.ASCII.GetBytes("\u001b[>6n\u001b[?5n\u001b[1c\u001b[?c"));
        Assert.Empty(responses);
    }
}
