using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Terminal;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class AgentErgonomicsTests
{
    [Fact]
    public async Task TerminalOutputSupportsAnsiCleaningLineLimitAndContinuationCursor()
    {
        var terminal = new RichTerminal("one\n\u001b[31mtwo\u001b[0m\nthree\nfour", cursor: 100);
        var gateway = new AgentGateway();
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession(), null, terminal);

        var response = await gateway.ExecuteAsync(new("terminal.output", handle, Cursor: 0, StripAnsi: true, MaxLines: 2), default);

        Assert.True(response.Success);
        var data = response.Data!.Value;
        var text = data.GetProperty("text").GetString()!;
        Assert.DoesNotContain('\u001b', text);
        Assert.Equal("three\nfour", text);
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.True(data.GetProperty("cursor").GetInt64() < 100);
    }

    [Fact]
    public async Task TerminalWaitReturnsWhenOutputBecomesQuiet()
    {
        var terminal = new RichTerminal("hello", cursor: 5) { EmptyAfterFirstPage = true };
        var gateway = new AgentGateway();
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession(), null, terminal);

        var response = await gateway.ExecuteAsync(new("terminal.wait", handle,
            Cursor: 0, IdleMilliseconds: 100, TimeoutMilliseconds: 3000), default);

        Assert.True(response.Success);
        var data = response.Data!.Value;
        Assert.Equal("hello", data.GetProperty("text").GetString());
        Assert.True(data.GetProperty("idle").GetBoolean());
        Assert.False(data.GetProperty("timedOut").GetBoolean());
        Assert.False(data.GetProperty("commandCompletionKnown").GetBoolean());
    }

    [Fact]
    public async Task ReadingAccessCannotWriteFiles()
    {
        var files = new MemoryFiles();
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession { FileSystem = files }, null);

        var response = await gateway.ExecuteAsync(new("files.write", handle, "/tmp/test.txt", Text: "hello"), default);

        Assert.Equal("control_required", response.ErrorCode);
        Assert.Empty(files.Files);
    }

    [Fact]
    public async Task FileWriteUsesStagedWriteWhenControlIsGranted()
    {
        var files = new MemoryFiles();
        var terminal = new RichTerminal("", 0);
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var session = new AgentGatewayTests.TestSession { FileSystem = files };
        var handle = gateway.GrantSession("tab", "Demo", session, null, terminal, true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));

        var response = await gateway.ExecuteAsync(new("files.write", handle, "/tmp/test.txt", Text: "hello"), default);

        Assert.True(response.Success);
        Assert.Equal("hello", Encoding.UTF8.GetString(files.Files["/tmp/test.txt"]));
        Assert.DoesNotContain(files.Files.Keys, path => path.Contains(".surge-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileReadSupportsOffsetAndMaximumBytes()
    {
        var files = new MemoryFiles();
        files.Files["/tmp/test.txt"] = Encoding.UTF8.GetBytes("0123456789");
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession { FileSystem = files }, null);

        var response = await gateway.ExecuteAsync(new("files.read", handle, "/tmp/test.txt", Offset: 2, MaxBytes: 4), default);

        Assert.True(response.Success);
        Assert.Equal("2345", response.Data!.Value.GetProperty("text").GetString());
        Assert.Equal(2, response.Data.Value.GetProperty("offset").GetInt64());
        Assert.Equal(4, response.Data.Value.GetProperty("bytesRead").GetInt32());
        Assert.True(response.Data.Value.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task LocalFileUploadAndDownloadRoundTripWhenControlIsGranted()
    {
        var source = Path.GetTempFileName();
        var destination = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(source, "round-trip");
            var files = new MemoryFiles();
            var gateway = new AgentGateway((_, _) => Task.FromResult(true));
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var handle = gateway.GrantSession("tab", "Demo", session, null, new RichTerminal("", 0), true,
                target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));

            var uploaded = await gateway.ExecuteAsync(new("files.upload", handle, "/remote.txt",
                LocalPath: source, Overwrite: true), default);
            Assert.True(uploaded.Success);
            Assert.Equal("round-trip", Encoding.UTF8.GetString(files.Files["/remote.txt"]));

            var downloaded = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: true), default);
            Assert.True(downloaded.Success);
            Assert.Equal("round-trip", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task BriefCheckReturnsOnlyTheScreenTail()
    {
        var terminal = new RichTerminal(string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line-{i}")), 0);
        var gateway = new AgentGateway();
        var session = new AgentGatewayTests.TestSession();
        var handle = gateway.GrantSession("tab", "Demo", session, null, terminal, true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));

        var response = await gateway.ExecuteAsync(new("sessions.check", handle, Brief: true), default);

        Assert.True(response.Success);
        var screen = response.Data!.Value.GetProperty("screen").GetProperty("text").GetString()!;
        Assert.DoesNotContain("line-1\n", screen);
        Assert.Contains("line-20", screen);
        Assert.True(response.Data.Value.GetProperty("brief").GetBoolean());
    }

    private sealed class RichTerminal(string text, long cursor) : ITerminalAutomation
    {
        private int _reads;
        public bool EmptyAfterFirstPage { get; init; }
        public TerminalScreenSnapshot ReadScreen() => new(text, 80, 24, 0, 0, true, false, false, false, 0, cursor, false, false);
        public TerminalOutputPage ReadOutput(long? requested)
        {
            if (EmptyAfterFirstPage && _reads++ > 0) return new("", cursor, 0, cursor, false, false);
            return new(text, cursor, 0, cursor, false, false);
        }
        public Task<TerminalInputReceipt> SendTextAsync(string value, bool submit, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new TerminalInputReceipt(expectedRevision + 1, cursor, value.Length, submit));
        public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new TerminalInputReceipt(expectedRevision + 1, cursor, 1, true));
    }

    private sealed class MemoryFiles : IRemoteFileSystem
    {
        private readonly Dictionary<string, MemoryStream> _staging = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public bool SupportsPermissions => false;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteFileEntry>>([]);
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemoteFileEntry?> TryStatAsync(string path, CancellationToken ct) =>
            Task.FromResult<RemoteFileEntry?>(Files.ContainsKey(path)
                ? new RemoteFileEntry { Name = path, FullPath = path, Kind = RemoteEntryKind.File }
                : null);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(Files[path], writable: false));
        public Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            var stream = new MemoryStream();
            _staging[path] = stream;
            return Task.FromResult<Stream>(stream);
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string path, bool recursive, CancellationToken ct)
        {
            Files.Remove(path);
            return Task.CompletedTask;
        }
        public Task RenameAsync(string source, string destination, CancellationToken ct)
        {
            if (Files.Remove(source, out var content)) Files[destination] = content;
            return Task.CompletedTask;
        }
        public Task CommitWriteAsync(string temporary, string destination, bool overwrite, CancellationToken ct)
        {
            Files[destination] = _staging[temporary].ToArray();
            _staging.Remove(temporary);
            return Task.CompletedTask;
        }
        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => Task.CompletedTask;
    }
}
