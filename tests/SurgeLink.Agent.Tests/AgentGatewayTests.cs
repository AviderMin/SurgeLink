using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class AgentGatewayTests
{
    [Fact]
    public async Task UnsharedSessionsAreInvisibleAndRandomHandlesCannotBeUsed()
    {
        var gateway = new AgentGateway((_, _) => throw new InvalidOperationException("No approval expected"));
        Assert.Empty((await gateway.ExecuteAsync(new("sessions.list"), default)).Data!.Value.EnumerateArray());
        Assert.Equal("session_unavailable", (await gateway.ExecuteAsync(new("terminal.screen", "unknown"), default)).ErrorCode);
    }

    [Fact]
    public async Task RevokedHandleStaysInvalidAfterSharingSameConnectionAgain()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(false));
        var session = new TestSession();
        var handle = gateway.GrantSession("tab", "Demo", session, () => "你好");
        var screen = await gateway.ExecuteAsync(new("terminal.screen", handle), default);
        Assert.Equal("你好", screen.Data!.Value.GetProperty("text").GetString());
        await gateway.RevokeAsync("tab");
        var replacement = gateway.GrantSession("tab", "Demo", session, () => "new connection");
        Assert.NotEqual(handle, replacement);
        Assert.Equal("session_unavailable", (await gateway.ExecuteAsync(new("sessions.info", handle), default)).ErrorCode);
        Assert.True((await gateway.ExecuteAsync(new("sessions.info", replacement), default)).Success);
    }

    [Theory]
    [InlineData("files.list")]
    [InlineData("files.read")]
    public async Task DenialPerformsNoFileIo(string method)
    {
        AgentFileAccess? requested = null;
        var gateway = new AgentGateway((access, _) => { requested = access; return Task.FromResult(false); });
        var files = new CountingFiles();
        var handle = gateway.GrantSession("tab", "Demo", new TestSession { FileSystem = files }, null);
        Assert.Equal("denied", (await gateway.ExecuteAsync(new(method, handle, "/private/config"), default)).ErrorCode);
        Assert.Equal("/private/config", requested!.Path);
        Assert.Equal(method, requested.Operation);
        Assert.Equal(0, files.ReadCount);
    }

    [Fact]
    public async Task EveryFileOperationNeedsItsOwnApproval()
    {
        var approvals = 0;
        var gateway = new AgentGateway((_, _) => Task.FromResult(++approvals == 1));
        var files = new CountingFiles();
        var handle = gateway.GrantSession("tab", "Demo", new TestSession { FileSystem = files }, null);
        Assert.True((await gateway.ExecuteAsync(new("files.read", handle, "/config"), default)).Success);
        Assert.Equal("denied", (await gateway.ExecuteAsync(new("files.read", handle, "/config"), default)).ErrorCode);
        Assert.Equal(1, files.ReadCount);
        Assert.Equal(2, approvals);
    }

    [Fact]
    public async Task RevocationCancelsPendingApprovalAndPreventsReadEvenIfApprovalIgnoresCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new CountingFiles();
        var gateway = new AgentGateway((_, _) => { started.SetResult(); return answer.Task; });
        var handle = gateway.GrantSession("tab", "Demo", new TestSession { FileSystem = files }, null);
        var pending = gateway.ExecuteAsync(new("files.read", handle, "/config"), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revoking = gateway.RevokeAsync("tab");
        Assert.False(gateway.IsShared("tab"));
        Assert.False(revoking.IsCompleted);
        answer.SetResult(true);
        await revoking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", (await pending).ErrorCode);
        Assert.Equal(0, files.ReadCount);
    }

    [Fact]
    public async Task RevokeDrainsRunningOperationBeforeSessionDisposal()
    {
        var monitor = new BlockingMonitor();
        var gateway = new AgentGateway((_, _) => Task.FromResult(false));
        var handle = gateway.GrantSession("tab", "Demo", new TestSession { Monitor = monitor }, null);
        var pending = gateway.ExecuteAsync(new("monitor.snapshot", handle), default);
        await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revoking = gateway.RevokeAsync("tab");
        Assert.False(revoking.IsCompleted);
        monitor.Finish.SetResult(new SystemSnapshot());
        await revoking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", (await pending).ErrorCode);
    }

    [Fact]
    public async Task FileReadIsBoundedOnActualBytes()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var files = new CountingFiles { Content = new string('x', 128 * 1024 + 1) };
        var handle = gateway.GrantSession("tab", "Demo", new TestSession { FileSystem = files }, null);
        Assert.Equal("read_failed", (await gateway.ExecuteAsync(new("files.read", handle, "/large"), default)).ErrorCode);
    }

    [Fact]
    public async Task ScreenIsBoundedAndUnsupportedCapabilityIsExplicit()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(false));
        var handle = gateway.GrantSession("tab", "Demo", new TestSession(), () => new string('x', 40000));
        var result = await gateway.ExecuteAsync(new("terminal.screen", handle), default);
        Assert.True(result.Data!.Value.GetProperty("truncated").GetBoolean());
        Assert.Equal(32768, result.Data.Value.GetProperty("text").GetString()!.Length);
        Assert.Equal("unsupported", (await gateway.ExecuteAsync(new("monitor.snapshot", handle), default)).ErrorCode);
    }

    [Theory]
    [InlineData("files.delete", 1, "unknown_method")]
    [InlineData("run_command", 1, "unknown_method")]
    [InlineData("sessions.list", 999, "version_mismatch")]
    public async Task UnsupportedRequestsFailClosed(string method, int version, string expected)
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(false));
        Assert.Equal(expected, (await gateway.ExecuteAsync(new(method, Version: version), default)).ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/tmp/a\nb")]
    [InlineData("/tmp/a\0b")]
    public async Task InvalidFilePathDoesNotPromptOrRead(string path)
    {
        var gateway = new AgentGateway((_, _) => throw new InvalidOperationException("No approval expected"));
        var handle = gateway.GrantSession("tab", "Demo", new TestSession(), null);
        Assert.Equal("invalid_path", (await gateway.ExecuteAsync(new("files.read", handle, path), default)).ErrorCode);
    }

    [Fact]
    public async Task DisconnectedSessionCannotBeUsedOrListed()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(false));
        var session = new TestSession();
        var handle = gateway.GrantSession("tab", "Demo", session, () => "screen");
        await session.DisposeAsync();
        Assert.Equal("session_unavailable", (await gateway.ExecuteAsync(new("terminal.screen", handle), default)).ErrorCode);
        Assert.Empty((await gateway.ExecuteAsync(new("sessions.list"), default)).Data!.Value.EnumerateArray());
        Assert.Throws<InvalidOperationException>(() => gateway.GrantSession("other", "Demo", session, null));
    }

    [Fact]
    public async Task NamedPipeRoundTripSurvivesMalformedClientAndReturnsSharedData()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var handle = gateway.GrantSession("tab", "Demo", new TestSession { FileSystem = new CountingFiles() }, () => "screen");
        var name = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var server = new AgentPipeServer(name, gateway);
        await using (var malformed = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await malformed.ConnectAsync(3000);
            await malformed.WriteAsync(BitConverter.GetBytes(-1));
        }
        var client = new AgentClient(name);
        var list = await client.SendAsync(new("sessions.list"));
        Assert.Equal(handle, Assert.Single(list.Data!.Value.EnumerateArray()).GetProperty("handle").GetString());
        var file = await client.SendAsync(new("files.read", handle, "/config"));
        Assert.Equal("hello", file.Data!.Value.GetProperty("text").GetString());
        await gateway.RevokeAsync("tab");
        Assert.Equal("session_unavailable", (await client.SendAsync(new("sessions.info", handle))).ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1048577)]
    public async Task InvalidFrameLengthIsRejectedBeforePayloadRead(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<IOException>(() => AgentWire.ReadAsync<AgentRequest>(stream, default));
    }

    internal sealed class TestSession : IProtocolSession
    {
        public string SessionId { get; } = Guid.NewGuid().ToString();
        public ProtocolDescriptor Protocol { get; } = new("fake", "Fake", 0, SessionCapabilities.Terminal);
        public ConnectionState State { get; private set; } = ConnectionState.Connected;
        public RemoteHostInfo HostInfo { get; } = new() { OperatingSystem = "Test" };
        public IRemoteTerminal? Terminal => null;
        public IRemoteFileSystem? FileSystem { get; init; }
        public IRemoteSystemMonitor? Monitor { get; init; }
#pragma warning disable CS0067
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<string>? Faulted;
#pragma warning restore CS0067
        public Task<RemoteHostInfo> LoadHostInfoAsync(CancellationToken cancellationToken) => Task.FromResult(HostInfo);
        public ValueTask DisposeAsync() { State = ConnectionState.Disconnected; return ValueTask.CompletedTask; }
    }

    private sealed class CountingFiles : IRemoteFileSystem
    {
        public int ReadCount { get; private set; }
        public string Content { get; init; } = "hello";
        public bool SupportsPermissions => false;
        public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
        { ReadCount++; return Task.FromResult<IReadOnlyList<RemoteFileEntry>>([]); }
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        { ReadCount++; return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Content))); }
        public Task<Stream> OpenWriteAsync(string path, long? expectedSize, IProgress<TransferProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, int mode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class BlockingMonitor : IRemoteSystemMonitor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SystemSnapshot> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(1);
        public Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken) { Started.SetResult(); return Finish.Task; }
        public Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
