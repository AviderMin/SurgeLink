using System.Security.Cryptography;
using SurgeLink.Core.Files;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class AgentPipeServerTests
{
    [Fact]
    public async Task SlowFileRequestDoesNotBlockOtherConnections()
    {
        var approvalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishApproval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new AgentGateway(async (_, ct) =>
        {
            approvalStarted.SetResult();
            using var registration = ct.Register(() => finishApproval.TrySetCanceled(ct));
            return await finishApproval.Task;
        });
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession { FileSystem = new EmptyFiles() }, null);
        var name = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var server = new AgentPipeServer(name, gateway);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var slow = new AgentClient(name).SendAsync(new("files.read", handle, "/config"), timeout.Token);
        await approvalStarted.Task.WaitAsync(timeout.Token);

        for (var i = 0; i < 4; i++)
        {
            var response = await new AgentClient(name).SendAsync(new("sessions.list"), timeout.Token).WaitAsync(timeout.Token);
            Assert.Equal(handle, Assert.Single(response.Data!.Value.EnumerateArray()).GetProperty("handle").GetString());
        }
        Assert.False(slow.IsCompleted);

        finishApproval.SetResult(true);
        Assert.True((await slow.WaitAsync(timeout.Token)).Success);
    }

    [Fact]
    public async Task HeldConnectionsDoNotStopTheListenerPermanently()
    {
        var gateway = new AgentGateway();
        var name = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var server = new AgentPipeServer(name, gateway);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Hold idle connections open so accepted pipes keep occupying server
        // instances. The listener must keep serving requests while they are held
        // and must still accept clients after they are released.
        var held = new List<System.IO.Pipes.NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name,
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, timeout.Token);
                held.Add(pipe);
            }

            var whileHeld = await new AgentClient(name).SendAsync(new("sessions.list"), timeout.Token);
            Assert.True(whileHeld.Success);

            foreach (var pipe in held) { await pipe.DisposeAsync(); }
            held.Clear();
            await Task.Delay(500, timeout.Token);

            var after = await new AgentClient(name).SendAsync(new("sessions.list"), timeout.Token);
            Assert.True(after.Success);
        }
        finally
        {
            foreach (var pipe in held) { await pipe.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task ClosingServerCancelsRequestsAndWaitsForTheirCleanup()
    {
        var approvalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new AgentGateway(async (_, ct) =>
        {
            approvalStarted.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return true; }
            finally { cleaned.SetResult(); }
        });
        var handle = gateway.GrantSession("tab", "Demo", new AgentGatewayTests.TestSession { FileSystem = new EmptyFiles() }, null);
        var name = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        var server = new AgentPipeServer(name, gateway);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = new AgentClient(name).SendAsync(new("files.read", handle, "/config"), timeout.Token);
        await approvalStarted.Task.WaitAsync(timeout.Token);

        await server.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        await cleaned.Task.WaitAsync(timeout.Token);
        var failure = await Record.ExceptionAsync(() => pending);
        Assert.True(failure is IOException or OperationCanceledException, $"Unexpected client completion: {failure}");
    }

    private sealed class EmptyFiles : IRemoteFileSystem
    {
        public bool SupportsPermissions => false;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteFileEntry>>([]);
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream([1]));
        public Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(string source, string destination, CancellationToken ct) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => throw new NotSupportedException();
    }
}
