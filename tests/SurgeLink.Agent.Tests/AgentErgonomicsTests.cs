using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
        const string raw = "one\n\u001b[31mtwo\u001b[0m\nthree\nfour";
        var terminal = new RichTerminal(raw, cursor: raw.Length);
        var gateway = new AgentGateway();
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession(), null, terminal);

        var response = await gateway.ExecuteAsync(new("terminal.output", handle, Cursor: 0, StripAnsi: true, MaxLines: 2), default);

        Assert.True(response.Success);
        var data = response.Data!.Value;
        var text = data.GetProperty("text").GetString()!;
        Assert.DoesNotContain('\u001b', text);
        Assert.Equal("one\ntwo\n", text);
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        var cursor = data.GetProperty("cursor").GetInt64();
        Assert.Equal(17, cursor);

        var next = await gateway.ExecuteAsync(new("terminal.output", handle, Cursor: cursor, StripAnsi: true, MaxLines: 2), default);
        Assert.True(next.Success);
        Assert.Equal("three\nfour", next.Data!.Value.GetProperty("text").GetString());
        Assert.False(next.Data.Value.GetProperty("hasMore").GetBoolean());
        Assert.False(next.Data.Value.GetProperty("truncated").GetBoolean());
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
    public async Task FailedDownloadLeavesAnExistingLocalFileUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            await File.WriteAllTextAsync(destination, "original");
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            files.FailingReads.Add("/remote.txt");
            var gateway = new AgentGateway((_, _) => Task.FromResult(true));
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var handle = gateway.GrantSession("tab", "Demo", session, null, new RichTerminal("", 0), true,
                target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: true), default);

            Assert.Equal("read_failed", response.ErrorCode);
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(directory, ".surge-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SourceCloseFailureDoesNotReplaceTheDestination()
    {
        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            await File.WriteAllTextAsync(destination, "original");
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            files.FailingDisposals.Add("/remote.txt");
            var gateway = new AgentGateway((_, _) => Task.FromResult(true));
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var handle = gateway.GrantSession("tab", "Demo", session, null, new RichTerminal("", 0), true,
                target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: true), default);

            Assert.Equal("read_failed", response.ErrorCode);
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(directory, ".surge-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadWithoutOverwriteRejectsAnExistingTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            await File.WriteAllTextAsync(destination, "original");
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            var gateway = CreateDownloadGateway(files, out var handle);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: false), default);

            Assert.Equal("local_file_exists", response.ErrorCode);
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(directory, ".surge-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadDoesNotOverwriteATargetThatAppearsDuringTheTransfer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            files.OnFirstRead = () => File.WriteAllText(destination, "appeared");
            var gateway = CreateDownloadGateway(files, out var handle);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: false), default);

            Assert.Equal("local_file_exists", response.ErrorCode);
            Assert.Equal("appeared", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(directory, ".surge-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadOverwriteHandlesATargetThatDisappearsBeforeCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            await File.WriteAllTextAsync(destination, "original");
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            files.OnFirstRead = () => File.Delete(destination);
            var gateway = CreateDownloadGateway(files, out var handle);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: true), default);

            Assert.True(response.Success, response.Message);
            Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(directory, ".surge-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public async Task DownloadOverwritePreservesAlternateDataStreamOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            await File.WriteAllTextAsync(destination, "original");
            AddAlternateStream(destination, "surge-download-test", new byte[] { 1, 2, 3 });
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            var gateway = CreateDownloadGateway(files, out var handle);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: true), default);

            Assert.True(response.Success, response.Message);
            Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
            Assert.Equal(new byte[] { 1, 2, 3 }, ReadAlternateStream(destination, "surge-download-test"));
            Assert.Empty(Directory.GetFiles(directory, ".surge-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public async Task DownloadOverwritePreservesExplicitAclOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            await File.WriteAllTextAsync(destination, "original");
            AddExplicitAclForGuests(destination);
            var before = GetAccessSddl(destination);
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            var gateway = CreateDownloadGateway(files, out var handle);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: true), default);

            Assert.True(response.Success, response.Message);
            Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
            Assert.Equal(before, GetAccessSddl(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public async Task DownloadOverwritePreservesMetadataWhenTargetAppearsDuringTheTransfer()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            files.OnFirstRead = () =>
            {
                File.WriteAllText(destination, "appeared");
                AddAlternateStream(destination, "surge-race-test", new byte[] { 9 });
            };
            var gateway = CreateDownloadGateway(files, out var handle);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: true), default);

            Assert.True(response.Success, response.Message);
            Assert.Equal("replacement", await File.ReadAllTextAsync(destination));
            Assert.Equal(new byte[] { 9 }, ReadAlternateStream(destination, "surge-race-test"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public async Task DownloadOverwriteFailsSafelyWhenTheTargetCannotBeReplaced()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "surge-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "target.txt");
        try
        {
            await File.WriteAllTextAsync(destination, "original");
            using var hold = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("replacement");
            var gateway = CreateDownloadGateway(files, out var handle);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: destination, Overwrite: true), default);

            Assert.Equal("read_failed", response.ErrorCode);
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(directory, ".surge-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MinimalCheckOmitsHostMetadata()
    {
        var gateway = new AgentGateway();
        var terminal = new RichTerminal("line", 0);
        var session = new AgentGatewayTests.TestSession();
        var handle = gateway.GrantSession("tab", "Demo", session, null, terminal, true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));

        var response = await gateway.ExecuteAsync(new("sessions.check", handle, Minimal: true), default);

        Assert.True(response.Success);
        var sessionData = response.Data!.Value.GetProperty("session");
        Assert.False(sessionData.TryGetProperty("host", out _));
        Assert.Equal("host.test", sessionData.GetProperty("target").GetProperty("server").GetString());
        Assert.True(response.Data.Value.GetProperty("minimal").GetBoolean());
    }

    [Fact]
    public async Task TerminalExecReturnsTheShellExitCode()
    {
        var terminal = new ExecTerminal();
        var gateway = new AgentGateway();
        var session = new AgentGatewayTests.TestSession();
        var handle = gateway.GrantSession("tab", "Demo", session, null, terminal, true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));
        var check = await gateway.ExecuteAsync(new("sessions.check", handle), default);
        var checkData = check.Data!.Value;

        var response = await gateway.ExecuteAsync(new("terminal.exec", handle,
            Text: "echo hi", OperationId: "exec-1", StripAnsi: true,
            ExpectedInputRevision: checkData.GetProperty("screen").GetProperty("inputRevision").GetInt64(),
            CheckToken: checkData.GetProperty("checkToken").GetString()), default);

        Assert.True(response.Success);
        var data = response.Data!.Value;
        Assert.True(data.GetProperty("commandCompletionKnown").GetBoolean());
        Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
        var outputText = data.GetProperty("output").GetProperty("text").GetString()!;
        Assert.DoesNotContain("__SURGELINK_EXIT_", outputText);
        Assert.DoesNotContain("echo hi", outputText);
        Assert.Equal(1, terminal.Writes);
    }

    [Fact]
    public async Task TerminalExecWaitsForTheSentinelAcrossAQuietGap()
    {
        var terminal = new SlowSentinelTerminal(delayMilliseconds: 700);
        var gateway = new AgentGateway();
        var session = new AgentGatewayTests.TestSession();
        var handle = gateway.GrantSession("tab", "Demo", session, null, terminal, true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));
        var check = await gateway.ExecuteAsync(new("sessions.check", handle), default);
        var checkData = check.Data!.Value;

        // A command that prints, thinks for a while, then exits. The quiet gap used to end
        // the wait before the sentinel arrived, so the caller was told the outcome was
        // unknown even though the shell had reported an exit code.
        var response = await gateway.ExecuteAsync(new("terminal.exec", handle,
            Text: "slow", OperationId: "exec-slow", IdleMilliseconds: 50, TimeoutMilliseconds: 5000,
            ExpectedInputRevision: checkData.GetProperty("screen").GetProperty("inputRevision").GetInt64(),
            CheckToken: checkData.GetProperty("checkToken").GetString()), default);

        Assert.True(response.Success);
        var data = response.Data!.Value;
        Assert.True(data.GetProperty("commandCompletionKnown").GetBoolean(), data.GetRawText());
        Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
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

    private static AgentGateway CreateDownloadGateway(MemoryFiles files, out string handle)
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var session = new AgentGatewayTests.TestSession { FileSystem = files };
        handle = gateway.GrantSession("tab", "Demo", session, null, new RichTerminal("", 0), true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));
        return gateway;
    }

    private static void AddAlternateStream(string path, string name, byte[] payload)
    {
        using var stream = new FileStream(path + ":" + name, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(payload);
    }

    private static byte[] ReadAlternateStream(string path, string name)
    {
        using var stream = new FileStream(path + ":" + name, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static void AddExplicitAclForGuests(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static string GetAccessSddl(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    private sealed class RichTerminal(string text, long cursor) : ITerminalAutomation
    {
        private int _reads;
        public bool EmptyAfterFirstPage { get; init; }
        public TerminalScreenSnapshot ReadScreen() => new(text, 80, 24, 0, 0, true, false, false, false, 0, cursor, false, false);
        public TerminalOutputPage ReadOutput(long? requested)
        {
            var start = requested ?? 0;
            if (EmptyAfterFirstPage && _reads++ > 0) return new("", text.Length, 0, text.Length, false, false);
            var pageText = start < text.Length ? text[(int)start..] : string.Empty;
            return new(pageText, text.Length, 0, text.Length, false, start + pageText.Length < text.Length);
        }
        public Task<TerminalInputReceipt> SendTextAsync(string value, bool submit, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new TerminalInputReceipt(expectedRevision + 1, cursor, value.Length, submit));
        public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new TerminalInputReceipt(expectedRevision + 1, cursor, 1, true));
    }

    private sealed class ExecTerminal : ITerminalAutomation
    {
        private string _screen = "test";
        private string _output = string.Empty;
        private long _revision;
        public int Writes { get; private set; }
        public TerminalScreenSnapshot ReadScreen() => new(_screen, 80, 24, 0, 0, true, false, false, false, _revision, _output.Length, false, false);
        public TerminalOutputPage ReadOutput(long? cursor)
        {
            var start = cursor ?? 0;
            var text = start < _output.Length ? _output[(int)start..] : string.Empty;
            return new(text, _output.Length, 0, _output.Length, false, false);
        }
        public Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct)
        {
            if (_revision != expectedRevision) throw new TerminalInputConflictException();
            Writes++;
            _revision++;
            _screen = "hi\n";
            _output = "\u001b[?2004l\recho hi\r\n__surge_status=$?; printf '\n__SURGELINK_EXIT_exec-1__:%d\n' \"$__surge_status\"\r\nhi\r\n__SURGELINK_EXIT_exec-1__:0\r\n";
            return Task.FromResult(new TerminalInputReceipt(_revision, 0, text.Length, submit));
        }
        public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct) =>
            SendTextAsync(key, false, expectedRevision, ct);
    }

    /// <summary>Prints, stays quiet, then emits the sentinel a slow command would produce.</summary>
    private sealed class SlowSentinelTerminal(int delayMilliseconds) : ITerminalAutomation
    {
        private readonly StringBuilder _output = new();
        private readonly System.Diagnostics.Stopwatch _elapsed = new();
        private long _revision;
        private bool _sentinel;
        public TerminalScreenSnapshot ReadScreen() =>
            new(_output.ToString(), 80, 24, 0, 0, true, false, false, false, _revision, _output.Length, false, false);
        public TerminalOutputPage ReadOutput(long? cursor)
        {
            if (!_sentinel && _elapsed.ElapsedMilliseconds >= delayMilliseconds)
            {
                _output.Append("__SURGELINK_EXIT_exec-slow__:0\n");
                _sentinel = true;
            }
            var start = (int)(cursor ?? 0);
            var text = start < _output.Length ? _output.ToString(start, _output.Length - start) : string.Empty;
            return new(text, _output.Length, 0, _output.Length, false, false);
        }
        public Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct)
        {
            if (_revision != expectedRevision) throw new TerminalInputConflictException();
            _revision++;
            _output.Clear();
            _output.Append("working\n");
            _elapsed.Restart();
            return Task.FromResult(new TerminalInputReceipt(_revision, 0, text.Length, submit));
        }
        public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct) =>
            SendTextAsync(key, false, expectedRevision, ct);
    }

    private sealed class MemoryFiles : IRemoteFileSystem
    {
        private readonly Dictionary<string, MemoryStream> _staging = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailingReads { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailingDisposals { get; } = new(StringComparer.Ordinal);
        public Action? OnFirstRead { get; set; }
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
            Task.FromResult<Stream>(FailingReads.Contains(path)
                ? new PartialThenFailingStream(Files[path])
                : FailingDisposals.Contains(path)
                    ? new ThrowOnDisposeStream(Files[path])
                    : new HookedStream(Files[path], OnFirstRead));
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

        /// <summary>Runs an action once when the copy starts reading the remote payload.</summary>
        private sealed class HookedStream(byte[] content, Action? onFirstRead) : MemoryStream(content, writable: false)
        {
            private int _reads;
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var result = base.ReadAsync(buffer, cancellationToken);
                if (_reads++ == 0) onFirstRead?.Invoke();
                return result;
            }
        }

        /// <summary>Returns a partial payload, then fails as a dropped connection would.</summary>
        private sealed class PartialThenFailingStream(byte[] content) : MemoryStream(content)
        {
            private int _reads;
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                _reads++ == 0
                    ? base.ReadAsync(buffer, cancellationToken)
                    : ValueTask.FromException<int>(new IOException("injected read failure"));
        }

        /// <summary>Reads successfully but reports a failed remote close.</summary>
        private sealed class ThrowOnDisposeStream(byte[] content) : MemoryStream(content)
        {
            private int _disposed;
            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    base.Dispose(disposing);
                    throw new IOException("injected close failure");
                }
                base.Dispose(disposing);
            }
        }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows NTFS security descriptor and alternate data stream behavior.";
    }
}
