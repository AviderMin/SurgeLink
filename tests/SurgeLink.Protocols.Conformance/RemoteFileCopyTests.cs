using SurgeLink.Core.Files;
using SurgeLink.Protocols.Demo;

namespace SurgeLink.Protocols.Conformance;

public sealed class RemoteFileCopyTests
{
    [Fact]
    public async Task CopyPreservesBytesAndSource()
    {
        var fs = new DemoFileSystem();
        await RemoteFileCopy.CopyAsync(fs, "/home/demo/demo.txt", "/home/demo/Documents/copied.txt", CancellationToken.None);
        await using var source = await fs.OpenReadAsync("/home/demo/demo.txt", CancellationToken.None);
        await using var target = await fs.OpenReadAsync("/home/demo/Documents/copied.txt", CancellationToken.None);
        Assert.Equal(await new StreamReader(source).ReadToEndAsync(), await new StreamReader(target).ReadToEndAsync());
    }

    [Fact]
    public async Task CopyRecursesThroughDirectories()
    {
        var fs = new DemoFileSystem();
        await RemoteFileCopy.CopyAsync(fs, "/home/demo/Projects", "/home/demo/Documents/Projects", CancellationToken.None);
        var copied = await fs.StatAsync("/home/demo/Documents/Projects/surgelink/README.md", CancellationToken.None);
        Assert.Equal(RemoteEntryKind.File, copied.Kind);
        Assert.True(copied.Size > 0);
    }

    [Fact]
    public async Task ExistingTargetIsPreserved()
    {
        var fs = new DemoFileSystem();
        var original = await fs.StatAsync("/home/demo/Documents/notes.txt", CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => RemoteFileCopy.CopyAsync(fs,
            "/home/demo/demo.txt", original.FullPath, CancellationToken.None));
        Assert.Equal(original.Size, (await fs.StatAsync(original.FullPath, CancellationToken.None)).Size);
    }

    [Theory]
    [InlineData("/home/demo/Projects")]
    [InlineData("/home/demo/Projects/child")]
    public async Task SelfAndDescendantTargetsAreRejected(string destination)
    {
        var fs = new DemoFileSystem();
        await Assert.ThrowsAsync<IOException>(() => RemoteFileCopy.CopyAsync(fs,
            "/home/demo/Projects", destination, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotCreateDestination()
    {
        var fs = new DemoFileSystem();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteFileCopy.CopyAsync(fs,
            "/home/demo/demo.txt", "/home/demo/cancelled.txt", cancellation.Token));
        Assert.DoesNotContain(await fs.ListAsync("/home/demo", CancellationToken.None), entry => entry.Name == "cancelled.txt");
    }
}
