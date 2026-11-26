using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Terminal;
using Xunit;

namespace SurgeLink.Agent.Tests;

/// <summary>
/// P0 hardening of the local half of <c>files.upload</c> / <c>files.download</c>:
/// confirmation-dialog spoofing, UNC and device paths, alternate data streams,
/// symbolic links and the "cancelled after the write" outcome.
/// </summary>
public sealed class AgentLocalPathSecurityTests
{
    [WindowsFact]
    public void AgentTransferRejectsMultiplyLinkedLocalFiles()
    {
        var directory = Directory.CreateTempSubdirectory("surge-link-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "file.txt");
            var link = Path.Combine(directory.FullName, "second-name.txt");
            File.WriteAllText(path, "fixture");
            Assert.True(CreateHardLink(link, path, 0));

            Assert.Throws<UnsafeLocalPathException>(() => LocalDownloadPath.OpenExistingFileForRead(path, rejectHardLinks: true));
            Assert.Throws<UnsafeLocalPathException>(() => LocalDownloadPath.IsSafeExistingFile(link));
            using var ordinaryUpload = LocalDownloadPath.OpenExistingFileForRead(path);
            Assert.Equal((long)"fixture".Length, ordinaryUpload.Length);
        }
        finally { directory.Delete(recursive: true); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string path, nint securityAttributes);

    private static (AgentGateway Gateway, string Handle) GrantControl(AgentGatewayTests.TestSession session,
        ITerminalAutomation? terminal = null)
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var handle = gateway.GrantSession("tab", "Demo", session, null, terminal ?? new SilentTerminal(), true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "alice"));
        return (gateway, handle);
    }

    [Theory]
    [InlineData(@"\\attacker\share\payload.txt")]
    [InlineData("//attacker/share/payload.txt")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\Windows\System32\drivers\etc\hosts")]
    public async Task UncAndDevicePathsAreRejectedForUpload(string localPath)
    {
        var files = new MemoryFiles();
        var session = new AgentGatewayTests.TestSession { FileSystem = files };
        var (gateway, handle) = GrantControl(session);

        var response = await gateway.ExecuteAsync(new("files.upload", handle, "/remote.txt",
            LocalPath: localPath, Overwrite: true), default);

        Assert.False(response.Success);
        Assert.Equal("invalid_path", response.ErrorCode);
        Assert.Empty(files.Files);
    }

    [Theory]
    [InlineData(@"\\attacker\share\exfil.txt")]
    [InlineData("//attacker/share/exfil.txt")]
    public async Task UncPathsAreRejectedForDownload(string localPath)
    {
        var files = new MemoryFiles();
        files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("secret");
        var session = new AgentGatewayTests.TestSession { FileSystem = files };
        var (gateway, handle) = GrantControl(session);

        var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
            LocalPath: localPath, Overwrite: true), default);

        Assert.False(response.Success);
        Assert.Equal("invalid_path", response.ErrorCode);
    }

    [Fact]
    public async Task AlternateDataStreamTargetsAreRejected()
    {
        var directory = NewDirectory();
        try
        {
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("payload");
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var (gateway, handle) = GrantControl(session);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: Path.Combine(directory, "note.txt:evil"), Overwrite: true), default);

            Assert.False(response.Success);
            Assert.Equal("invalid_path", response.ErrorCode);
            Assert.False(File.Exists(Path.Combine(directory, "note.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("pay\u202Eload.txt")]
    [InlineData("pay\u202Aload.txt")]
    [InlineData("pay\u2066load.txt")]
    [InlineData("pay\u200Eload.txt")]
    public async Task BidirectionalOverrideCharactersAreRejected(string name)
    {
        var directory = NewDirectory();
        try
        {
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("payload");
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var (gateway, handle) = GrantControl(session);

            // The confirmation dialog prints this path; a right-to-left override can
            // make "gpj.exe" render as "exe.jpg", so the user approves a different file.
            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: Path.Combine(directory, name), Overwrite: true), default);

            Assert.False(response.Success);
            Assert.Equal("invalid_path", response.ErrorCode);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ControlCharactersAreRejected()
    {
        var files = new MemoryFiles();
        files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("payload");
        var session = new AgentGatewayTests.TestSession { FileSystem = files };
        var (gateway, handle) = GrantControl(session);

        var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
            LocalPath: "C:\\temp\\evil\u0007name.txt", Overwrite: true), default);

        Assert.False(response.Success);
        Assert.Equal("invalid_path", response.ErrorCode);
    }

    [Fact]
    public async Task WindowsReservedFileNamesAreRejected()
    {
        var directory = NewDirectory();
        try
        {
            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("payload");
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var (gateway, handle) = GrantControl(session);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: Path.Combine(directory, "CON"), Overwrite: true), default);

            Assert.False(response.Success);
            Assert.Equal("invalid_path", response.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadIntoASymbolicLinkDirectoryIsRejected()
    {
        var root = NewDirectory();
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var link = Path.Combine(root, "link");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, real);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Creating a symlink needs Developer Mode or elevation on Windows.
                return;
            }

            var files = new MemoryFiles();
            files.Files["/remote.txt"] = Encoding.UTF8.GetBytes("payload");
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var (gateway, handle) = GrantControl(session);

            var response = await gateway.ExecuteAsync(new("files.download", handle, "/remote.txt",
                LocalPath: Path.Combine(link, "written.txt"), Overwrite: true), default);

            Assert.False(response.Success);
            Assert.Equal("invalid_path", response.ErrorCode);
            Assert.False(File.Exists(Path.Combine(real, "written.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadFromASymbolicLinkIsRejected()
    {
        var root = NewDirectory();
        var target = Path.Combine(root, "secret.txt");
        var link = Path.Combine(root, "innocent.txt");
        try
        {
            await File.WriteAllTextAsync(target, "credentials");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var files = new MemoryFiles();
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var (gateway, handle) = GrantControl(session);

            var response = await gateway.ExecuteAsync(new("files.upload", handle, "/remote.txt",
                LocalPath: link, Overwrite: true), default);

            Assert.False(response.Success);
            Assert.Equal("invalid_path", response.ErrorCode);
            Assert.Empty(files.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadOfAMissingFileReportsItAsMissingNotAsAnInvalidPath()
    {
        var directory = NewDirectory();
        try
        {
            var files = new MemoryFiles();
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var (gateway, handle) = GrantControl(session);

            var response = await gateway.ExecuteAsync(new("files.upload", handle, "/remote.txt",
                LocalPath: Path.Combine(directory, "absent.txt"), Overwrite: true), default);

            Assert.False(response.Success);
            Assert.Equal("local_file_not_found", response.ErrorCode);
            Assert.Empty(files.Files);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UploadReportsTheResolvedAbsolutePathInItsResponse()
    {
        var directory = NewDirectory();
        try
        {
            var source = Path.Combine(directory, "source.txt");
            await File.WriteAllTextAsync(source, "data");
            var files = new MemoryFiles();
            var session = new AgentGatewayTests.TestSession { FileSystem = files };
            var (gateway, handle) = GrantControl(session);

            var response = await gateway.ExecuteAsync(new("files.upload", handle, "/remote.txt",
                LocalPath: source, Overwrite: true), default);

            Assert.True(response.Success, response.Message);
            Assert.Equal(Path.GetFullPath(source),
                response.Data!.Value.GetProperty("localPath").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecReportsAnUnknownOutcomeWhenTheWaitIsCancelledAfterTheWrite()
    {
        var files = new MemoryFiles();
        var session = new AgentGatewayTests.TestSession { FileSystem = files };
        var terminal = new SilentTerminal();
        var (gateway, handle) = GrantControl(session, terminal);

        var input = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        var exec = input with { Method = "terminal.exec", Text = "sleep 1000", OperationId = "exec-cancel" };

        using var cancel = new CancellationTokenSource();
        // The command never prints its completion marker; cancelling the wait used to
        // be reported as "cancelled before writing" even though the PTY had the bytes.
        var pending = gateway.ExecuteAsync(exec, cancel.Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
        var response = await pending;

        Assert.False(response.Success);
        Assert.Equal("input_outcome_unknown", response.ErrorCode);
        Assert.Contains("do not resend", response.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, terminal.Writes);

        // The gateway keeps refusing further input until a human renews control.
        var next = await gateway.ExecuteAsync(input with { OperationId = "after-cancel" }, default);
        Assert.Equal("input_outcome_unknown", next.ErrorCode);
        Assert.Equal(1, terminal.Writes);
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "surge-localpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>A terminal that accepts writes but never emits command output.</summary>
    private sealed class SilentTerminal : ITerminalAutomation
    {
        private long _revision;

        public int Writes { get; private set; }

        public TerminalScreenSnapshot ReadScreen() =>
            new("test", 80, 24, 0, 0, true, false, false, false, _revision, 0, false, false);

        public TerminalOutputPage ReadOutput(long? cursor) => new(string.Empty, 0, 0, 0, false, false);

        public Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct)
        {
            if (_revision != expectedRevision) throw new TerminalInputConflictException();
            Writes++;
            _revision++;
            return Task.FromResult(new TerminalInputReceipt(_revision, 0, text.Length, submit));
        }

        public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct) =>
            SendTextAsync(key, false, expectedRevision, ct);
    }

    private sealed class MemoryFiles : IRemoteFileSystem
    {
        private readonly Dictionary<string, MemoryStream> _staging = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public bool SupportsPermissions => false;

        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteFileEntry>>([]);

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
