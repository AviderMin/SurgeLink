using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Protocols.Demo;

namespace SurgeLink.Protocols.Conformance;

public sealed class StagedFileWriteTests
{
    [Fact]
    public async Task ADirectoryCreatedDuringUploadIsNotRemovedOrReplaced()
    {
        var fs = new DemoFileSystem();
        await Assert.ThrowsAsync<IOException>(() => RemoteFileWrite.WriteAsync(fs, Destination, 3, null, true,
            async (stream, token) =>
            {
                await stream.WriteAsync("new"u8.ToArray(), token);
                await fs.DeleteAsync(Destination, false, token);
                await fs.CreateDirectoryAsync(Destination, token);
                await using var child = await fs.OpenWriteAsync(Destination + "/keep", 4, null, token);
                await child.WriteAsync("keep"u8.ToArray(), token);
            }, default));
        Assert.Equal(RemoteEntryKind.Directory, (await fs.StatAsync(Destination, default)).Kind);
        Assert.Equal("keep", await ReadAsync(fs, Destination + "/keep"));
        Assert.DoesNotContain(await fs.ListAsync("/home/demo", default), entry => entry.Name.StartsWith(".surge-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RemoteEntryKind.SymbolicLink)]
    [InlineData(RemoteEntryKind.Other)]
    public async Task AChangedTargetTypeFailsBeforeCommit(RemoteEntryKind kind)
    {
        var fs = new ProtectedFileSystem();
        var original = await ReadAsync(fs.Inner, Destination);
        await Assert.ThrowsAsync<IOException>(() => RemoteFileWrite.WriteAsync(fs, Destination, 3, null, true,
            async (stream, token) => { await stream.WriteAsync("new"u8.ToArray(), token); fs.TargetKind = kind; }, default));
        Assert.Equal(original, await ReadAsync(fs.Inner, Destination));
    }

    [Theory]
    [InlineData(0x9ED)]
    [InlineData(0x5ED)]
    [InlineData(0xDED)]
    [InlineData(0x3ED)]
    public async Task ContentReplacementDoesNotInheritPrivilegeBits(int mode)
    {
        var fs = new DemoFileSystem();
        await fs.SetPermissionsAsync(Destination, mode, default);
        await RemoteFileWrite.WriteAsync(fs, Destination, 3, null, true,
            (stream, token) => stream.WriteAsync("new"u8.ToArray(), token).AsTask(), default);
        Assert.Equal(mode & 0x1FF, (await fs.StatAsync(Destination, default)).UnixMode);
    }

    private const string Destination = "/home/demo/demo.txt";

    [Fact]
    public async Task PayloadIsWrittenOnlyInsideAPrivateDirectoryAndCleanupRemovesIt()
    {
        var fs = new ProtectedFileSystem();
        await fs.Inner.SetPermissionsAsync(Destination, 0x180, default);
        await RemoteFileWrite.WriteAsync(fs, Destination, 6, null, true,
            (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes("secret"), token).AsTask(), default);
        Assert.True(fs.PayloadOpened);
        Assert.Equal(0x180, (await fs.StatAsync(Destination, default)).UnixMode);
        Assert.DoesNotContain(await fs.ListAsync("/home/demo", default), entry => entry.Name.StartsWith(".surge-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailureToRestrictStagingDirectoryNeverWritesPayloadOrReplacesOriginal()
    {
        var fs = new ProtectedFileSystem { RejectProtection = true };
        var original = await ReadAsync(fs.Inner, Destination);
        var writeCalled = false;
        await Assert.ThrowsAsync<IOException>(() => RemoteFileWrite.WriteAsync(fs, Destination, 6, null, true,
            (_, _) => { writeCalled = true; return Task.CompletedTask; }, default));
        Assert.False(writeCalled);
        Assert.False(fs.PayloadOpened);
        Assert.Equal(original, await ReadAsync(fs.Inner, Destination));
        Assert.DoesNotContain(await fs.ListAsync("/home/demo", default), entry => entry.Name.StartsWith(".surge-", StringComparison.Ordinal));
    }

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

    private sealed class ProtectedFileSystem : IRemoteFileSystem
    {
        public DemoFileSystem Inner { get; } = new();
        public bool RejectProtection { get; init; }
        public bool PayloadOpened { get; private set; }
        public RemoteEntryKind? TargetKind { get; set; }
        public bool SupportsPermissions => true;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => Inner.GetHomeDirectoryAsync(ct);
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => Inner.GetCurrentDirectoryAsync(ct);
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => Inner.ListAsync(path, ct);
        public async Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct)
        {
            var entry = await Inner.StatAsync(path, ct);
            return path == Destination && TargetKind is { } kind ? entry with { Kind = kind } : entry;
        }
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Inner.OpenReadAsync(path, ct);
        public async Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            var parent = path[..path.LastIndexOf('/')];
            Assert.Equal(0x1C0, (await Inner.StatAsync(parent, ct)).UnixMode);
            PayloadOpened = true;
            return await Inner.OpenWriteAsync(path, size, progress, ct);
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => Inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => Inner.DeleteAsync(path, recursive, ct);
        public Task RenameAsync(string from, string to, CancellationToken ct) => Inner.RenameAsync(from, to, ct);
        public Task CommitWriteAsync(string from, string to, bool overwrite, CancellationToken ct) => Inner.CommitWriteAsync(from, to, overwrite, ct);
        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => RejectProtection && mode == 0x1C0
            ? Task.FromException(new IOException("chmod unavailable")) : Inner.SetPermissionsAsync(path, mode, ct);
    }
}
