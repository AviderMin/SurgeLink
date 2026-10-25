using SurgeLink.Core.Files;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class RemoteReadStreamTests
{
    [Fact]
    public async Task EmptyRemoteFileReturnsEndOfStream()
    {
        await using var input = new RemoteReadStream((_, _) => Task.CompletedTask, default);
        Assert.Equal(0, await input.ReadAsync(new byte[10]));
    }

    [Fact]
    public async Task CancellationIsObservedForEmptyProducerWrites()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await using var input = new RemoteReadStream((target, token) =>
        {
            started.SetResult();
            token.WaitHandle.WaitOne();
            try { target.Write(Array.Empty<byte>(), 0, 0); observed.SetResult(false); }
            catch (OperationCanceledException) { observed.SetResult(true); }
            return Task.CompletedTask;
        }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LargeProducerIsBackpressuredAndStoppingABoundedReadCancelsIt()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long produced = 0;
        var stream = new RemoteReadStream((target, token) =>
        {
            try
            {
                var chunk = new byte[RemoteReadStream.ChunkSize];
                Array.Fill(chunk, (byte)'A');
                for (var index = 0; index < 1024; index++)
                {
                    target.Write(chunk);
                    Interlocked.Add(ref produced, chunk.Length);
                }
                return Task.CompletedTask;
            }
            finally { stopped.TrySetResult(); }
        }, default);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => BoundedTextFile.ReadAsync(stream, 1024, default));
            Assert.False(stopped.Task.IsCompleted);
        }
        finally { await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(stopped.Task.IsCompleted);
        Assert.InRange(produced, 0, RemoteReadStream.ChunkSize * 3L);
    }

    [Fact]
    public async Task StreamingPreservesBytesAcrossDifferentReadAndWriteSizes()
    {
        var expected = Enumerable.Range(0, RemoteReadStream.ChunkSize * 5 + 13).Select(i => (byte)i).ToArray();
        await using var input = new RemoteReadStream((target, _) =>
        {
            target.Write(expected);
            return Task.CompletedTask;
        }, default);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, 137);
        Assert.Equal(expected, output.ToArray());
    }

    [Fact]
    public async Task CancellationInterruptsAnAlreadyRunningDownload()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await using var input = new RemoteReadStream(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.SetResult(); }
        }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var read = input.ReadAsync(new byte[1]).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadCancellationAlsoCancelsTheProducer()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var input = new RemoteReadStream(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.SetResult(); }
        }, default);
        using var cancellation = new CancellationTokenSource();
        var read = input.ReadAsync(new byte[1], cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProducerFailureIsNotReportedAsSuccessfulEndOfFile()
    {
        await using var input = new RemoteReadStream((target, _) =>
        {
            target.Write(new byte[] { 1, 2, 3 });
            throw new IOException("remote transfer failed");
        }, default);
        var buffer = new byte[10];
        Assert.Equal(3, await input.ReadAsync(buffer));
        var failure = await Assert.ThrowsAsync<IOException>(() => input.ReadAsync(buffer).AsTask());
        Assert.Equal("remote transfer failed", failure.Message);
    }
}

