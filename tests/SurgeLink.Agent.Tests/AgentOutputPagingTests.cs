using System.Text;
using SurgeLink.Core.Terminal;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class AgentOutputPagingTests
{
    [Fact]
    public async Task TerminalOutputPagesContinuouslyWithoutGapsOrDuplicates()
    {
        const string output = "0123456789abcdefghij";
        var terminal = new BufferedTerminal();
        terminal.Append(output);
        var gateway = new AgentGateway();
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession(), null, terminal);

        var collected = new StringBuilder();
        long cursor = 0;
        for (var page = 0; page < 100; page++)
        {
            var response = await gateway.ExecuteAsync(new("terminal.output", handle, Cursor: cursor, MaxBytes: 3), default);
            Assert.True(response.Success);
            var data = response.Data!.Value;
            collected.Append(data.GetProperty("text").GetString());
            var next = data.GetProperty("cursor").GetInt64();
            Assert.True(next > cursor, $"page {page} did not advance the cursor");
            Assert.True(next <= terminal.Cursor);
            cursor = next;
            if (!data.GetProperty("hasMore").GetBoolean()) break;
        }

        Assert.Equal(output, collected.ToString());
        Assert.Equal(terminal.Cursor, cursor);
    }

    [Fact]
    public async Task MultiByteOutputKeepsCharactersAndSurrogatePairsWhole()
    {
        const string output = "中A😀B中文";
        var terminal = new BufferedTerminal();
        terminal.Append(output);
        var gateway = new AgentGateway();
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession(), null, terminal);

        var collected = new StringBuilder();
        long cursor = 0;
        for (var page = 0; page < 100; page++)
        {
            var response = await gateway.ExecuteAsync(new("terminal.output", handle, Cursor: cursor, MaxBytes: 4), default);
            Assert.True(response.Success);
            var data = response.Data!.Value;
            var text = data.GetProperty("text").GetString()!;
            if (text.Length > 0) Assert.False(char.IsHighSurrogate(text[^1]), $"page {page} split a surrogate pair");
            collected.Append(text);
            cursor = data.GetProperty("cursor").GetInt64();
            if (!data.GetProperty("hasMore").GetBoolean()) break;
        }

        Assert.Equal(output, collected.ToString());
    }

    [Fact]
    public async Task TerminalWaitReturnsTheHeadAndItsCursorContinuesTheRest()
    {
        var output = new string('x', 100);
        var terminal = new BufferedTerminal();
        terminal.Append(output);
        var gateway = new AgentGateway();
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession(), null, terminal);

        var wait = await gateway.ExecuteAsync(new("terminal.wait", handle, Cursor: 0,
            MaxBytes: 10, IdleMilliseconds: 50, TimeoutMilliseconds: 2000), default);

        Assert.True(wait.Success);
        var data = wait.Data!.Value;
        Assert.Equal(10, data.GetProperty("text").GetString()!.Length);
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        var cursor = data.GetProperty("cursor").GetInt64();
        Assert.Equal(10, cursor);
        Assert.Equal(100, data.GetProperty("latestCursor").GetInt64());

        var collected = new StringBuilder(data.GetProperty("text").GetString());
        for (var page = 0; page < 100; page++)
        {
            var response = await gateway.ExecuteAsync(new("terminal.output", handle, Cursor: cursor, MaxBytes: 17), default);
            Assert.True(response.Success);
            var pageData = response.Data!.Value;
            collected.Append(pageData.GetProperty("text").GetString());
            cursor = pageData.GetProperty("cursor").GetInt64();
            if (!pageData.GetProperty("hasMore").GetBoolean()) break;
        }

        Assert.Equal(output, collected.ToString());
    }

    private sealed class BufferedTerminal : ITerminalAutomation
    {
        private readonly TerminalOutputBuffer _output = new();

        public long Cursor => _output.Cursor;

        public void Append(string text) => _output.Append(Encoding.UTF8.GetBytes(text));

        public TerminalScreenSnapshot ReadScreen() =>
            new(string.Empty, 80, 24, 0, 0, true, false, false, false, 0, _output.Cursor, false, false);

        public TerminalOutputPage ReadOutput(long? cursor) => _output.Read(cursor);

        public Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
