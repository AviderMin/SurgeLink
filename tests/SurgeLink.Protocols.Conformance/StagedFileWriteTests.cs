using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Protocols.Demo;

namespace SurgeLink.Protocols.Conformance;

public sealed class StagedFileWriteTests
{
    private const string Destination = "/home/demo/demo.txt";

    private static async Task<string> ReadAsync(DemoFileSystem fs, string path)
    {
        await using var stream = await fs.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task FailedWritePreservesOriginalAndCleansStage()
    {
        var fs = new DemoFileSystem();
        var original = await ReadAsync(fs, Destination);
        await Assert.ThrowsAsync<IOException>(() => RemoteFileWrite.WriteAsync(fs, Destination, 3, null, true,
            async (stream, token) =>
            {
                await stream.WriteAsync(new byte[] { 1 }, token);
                throw new IOException("interrupted source");
            }, CancellationToken.None));
        Assert.Equal(original, await ReadAsync(fs, Destination));
        Assert.DoesNotContain(await fs.ListAsync("/home/demo", CancellationToken.None), entry => entry.Name.StartsWith(".surge-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationPreservesOriginal()
    {
        var fs = new DemoFileSystem();
        var original = await ReadAsync(fs, Destination);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteFileWrite.WriteAsync(fs, Destination, 1, null, true,
            async (stream, token) =>
            {
                await stream.WriteAsync(new byte[] { 1 }, token);
                cancellation.Cancel();
            }, cancellation.Token));
        Assert.Equal(original, await ReadAsync(fs, Destination));
    }

    [Fact]
    public async Task ConcurrentDestinationCreationCannotBeOverwritten()
    {
        var fs = new DemoFileSystem();
        const string target = "/home/demo/race.txt";
        await Assert.ThrowsAsync<IOException>(() => RemoteFileWrite.WriteAsync(fs, target, 3, null, false,
            async (stream, token) =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("new"), token);
                await using var competitor = await fs.OpenWriteAsync(target, 3, null, token);
                await competitor.WriteAsync(Encoding.UTF8.GetBytes("old"), token);
            }, CancellationToken.None));
        Assert.Equal("old", await ReadAsync(fs, target));
    }

    [Fact]
    public async Task SuccessfulCommitReplacesContentAndPreservesPermissions()
    {
        var fs = new DemoFileSystem();
        await fs.SetPermissionsAsync(Destination, 0x180, CancellationToken.None);
        await RemoteFileWrite.WriteAsync(fs, Destination, 3, null, true,
            (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes("new"), token).AsTask(), CancellationToken.None);
        Assert.Equal("new", await ReadAsync(fs, Destination));
        Assert.Equal(0x180, (await fs.StatAsync(Destination, CancellationToken.None)).UnixMode);
    }
}
