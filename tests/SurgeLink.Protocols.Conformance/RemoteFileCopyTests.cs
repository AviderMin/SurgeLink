using SurgeLink.Core.Files;
using SurgeLink.Protocols.Demo;

namespace SurgeLink.Protocols.Conformance;

public sealed class RemoteFileCopyTests
{
    [Theory]
    [InlineData(0x180)]
    [InlineData(0x1E8)]
    [InlineData(0x9ED)]
    public async Task CopyPreservesOrdinaryPermissionsWithoutPropagatingSpecialBits(int mode)
    {
        var fs = new DemoFileSystem();
        await fs.SetPermissionsAsync("/home/demo/demo.txt", mode, default);
        await RemoteFileCopy.CopyAsync(fs, "/home/demo/demo.txt", "/home/demo/copy.txt", default);
        Assert.Equal(mode & 0x1FF, (await fs.StatAsync("/home/demo/copy.txt", default)).UnixMode);
    }

    [Fact]
    public async Task CopyKeepsPrivateDirectoryPermissionsAndCaseDistinctChildren()
    {
        var fs = new DemoFileSystem();
        await fs.CreateDirectoryAsync("/home/demo/private", default);
        await fs.SetPermissionsAsync("/home/demo/private", 0x140, default); // r-x------
        foreach (var name in new[] { "README", "readme", "file ", "..\\literal" })
        {
            await using var output = await fs.OpenWriteAsync("/home/demo/private/" + name, 1, null, default);
            await output.WriteAsync(new byte[] { 42 });
        }
        await RemoteFileCopy.CopyAsync(fs, "/home/demo/private", "/home/demo/copied", default);
        Assert.Equal(0x140, (await fs.StatAsync("/home/demo/copied", default)).UnixMode);
        Assert.Equal(new[] { "..\\literal", "README", "file ", "readme" },
            (await fs.ListAsync("/home/demo/copied", default)).Select(entry => entry.Name));
    }

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
    public async Task CopyingManyFilesDoesNotRelistTheDestinationDirectoryPerFile()
    {
        var inner = new DemoFileSystem();
        await inner.CreateDirectoryAsync("/home/demo/bulk", CancellationToken.None);
        for (int index = 0; index < 100; index++)
        {
            await using var output = await inner.OpenWriteAsync($"/home/demo/bulk/file{index}.txt", 1, null, CancellationToken.None);
            await output.WriteAsync(new byte[] { 42 });
        }

        var counting = new CountingFileSystem(inner);
        await RemoteFileCopy.CopyAsync(counting, "/home/demo/bulk", "/home/demo/bulk-copy", CancellationToken.None);

        Assert.Equal(1, counting.ListCalls);
        Assert.Equal(100, counting.ListedEntries);
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

    [Fact]
    public async Task FailedFolderCopyRemovesPartialDestinationSoRetryCanSucceed()
    {
        var fs = new DemoFileSystem();
        var failing = new FailingReadFileSystem(fs, "/home/demo/Projects/surgelink/README.md");
        await Assert.ThrowsAsync<IOException>(() => RemoteFileCopy.CopyAsync(failing, "/home/demo/Projects", "/home/demo/Projects-copy", CancellationToken.None));

        Assert.DoesNotContain(await fs.ListAsync("/home/demo", CancellationToken.None), entry => entry.Name == "Projects-copy");

        // The rollback must leave no destination behind, so a retry is possible.
        await RemoteFileCopy.CopyAsync(fs, "/home/demo/Projects", "/home/demo/Projects-copy", CancellationToken.None);
        Assert.Equal(RemoteEntryKind.File,
            (await fs.StatAsync("/home/demo/Projects-copy/surgelink/README.md", CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task FailedFolderCopyCanDeleteFilesFromReadOnlySubdirectories()
    {
        var fs = new DemoFileSystem();
        await fs.CreateDirectoryAsync("/home/demo/tree", CancellationToken.None);
        await fs.CreateDirectoryAsync("/home/demo/tree/aaa-readonly", CancellationToken.None);
        await fs.SetPermissionsAsync("/home/demo/tree/aaa-readonly", 0x140, CancellationToken.None);
        foreach (var path in new[] { "/home/demo/tree/aaa-readonly/inside.txt", "/home/demo/tree/z-failing.txt" })
        {
            await using var output = await fs.OpenWriteAsync(path, 1, null, CancellationToken.None);
            await output.WriteAsync(new byte[] { 42 });
        }

        var failing = new FailingReadFileSystem(new PermissionEnforcingFileSystem(fs), "/home/demo/tree/z-failing.txt");
        await Assert.ThrowsAsync<IOException>(() => RemoteFileCopy.CopyAsync(failing,
            "/home/demo/tree", "/home/demo/tree-copy", CancellationToken.None));

        Assert.DoesNotContain(await fs.ListAsync("/home/demo", CancellationToken.None), entry => entry.Name == "tree-copy");
        await RemoteFileCopy.CopyAsync(new PermissionEnforcingFileSystem(fs), "/home/demo/tree", "/home/demo/tree-copy", CancellationToken.None);
        Assert.Equal(0x140, (await fs.StatAsync("/home/demo/tree-copy/aaa-readonly", CancellationToken.None)).UnixMode);
    }

    [Fact]
    public async Task FailedPermissionFinalizationRollsBackANestedReadOnlyTree()
    {
        var fs = new DemoFileSystem();
        await fs.CreateDirectoryAsync("/home/demo/tree", CancellationToken.None);
        await fs.CreateDirectoryAsync("/home/demo/tree/a", CancellationToken.None);
        await fs.CreateDirectoryAsync("/home/demo/tree/a/b", CancellationToken.None);
        await fs.SetPermissionsAsync("/home/demo/tree/a", 0x100, CancellationToken.None);
        await fs.SetPermissionsAsync("/home/demo/tree/a/b", 0x100, CancellationToken.None);

        // The root chmod fails after the deeper directories were made read-only.
        var failing = new TraversingPermissionFileSystem(fs, failFinalModeAt: "/home/demo/tree-copy");
        await Assert.ThrowsAsync<IOException>(() => RemoteFileCopy.CopyAsync(failing,
            "/home/demo/tree", "/home/demo/tree-copy", CancellationToken.None));

        Assert.DoesNotContain(await fs.ListAsync("/home/demo", CancellationToken.None), entry => entry.Name == "tree-copy");

        await RemoteFileCopy.CopyAsync(new TraversingPermissionFileSystem(fs),
            "/home/demo/tree", "/home/demo/tree-copy", CancellationToken.None);
        Assert.Equal(0x100, (await fs.StatAsync("/home/demo/tree-copy/a", CancellationToken.None)).UnixMode);
    }

    private sealed class FailingReadFileSystem(IRemoteFileSystem inner, string failingPath) : IRemoteFileSystem
    {
        public bool SupportsPermissions => inner.SupportsPermissions;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => inner.GetHomeDirectoryAsync(ct);
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => inner.GetCurrentDirectoryAsync(ct);
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<RemoteFileEntry?> TryStatAsync(string path, CancellationToken ct) => inner.TryStatAsync(path, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
            path == failingPath ? Task.FromException<Stream>(new IOException("injected read failure")) : inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            inner.OpenWriteAsync(path, size, progress, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteAsync(path, recursive, ct);
        public Task RenameAsync(string source, string destination, CancellationToken ct) => inner.RenameAsync(source, destination, ct);
        public Task CommitWriteAsync(string temporary, string destination, bool overwrite, CancellationToken ct) =>
            inner.CommitWriteAsync(temporary, destination, overwrite, ct);
        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => inner.SetPermissionsAsync(path, mode, ct);
    }

    private sealed class PermissionEnforcingFileSystem(IRemoteFileSystem inner) : IRemoteFileSystem
    {
        public bool SupportsPermissions => true;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => inner.GetHomeDirectoryAsync(ct);
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => inner.GetCurrentDirectoryAsync(ct);
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<RemoteFileEntry?> TryStatAsync(string path, CancellationToken ct) => inner.TryStatAsync(path, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            inner.OpenWriteAsync(path, size, progress, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

        public async Task DeleteAsync(string path, bool recursive, CancellationToken ct)
        {
            // A real POSIX server cannot unlink children of a directory without
            // owner write permission, even when the caller owns the files.
            if (recursive) await RequireWritableTreeAsync(path, ct);
            await inner.DeleteAsync(path, recursive, ct);
        }

        private async Task RequireWritableTreeAsync(string path, CancellationToken ct)
        {
            var entry = await inner.StatAsync(path, ct);
            if (entry.Kind != RemoteEntryKind.Directory) return;
            if ((entry.UnixMode & 0x1C0) != 0x1C0)
                throw new IOException("Permission denied while deleting files under " + path);
            foreach (var child in await inner.ListAsync(path, ct))
                await RequireWritableTreeAsync(child.FullPath, ct);
        }

        public Task RenameAsync(string source, string destination, CancellationToken ct) => inner.RenameAsync(source, destination, ct);
        public Task CommitWriteAsync(string temporary, string destination, bool overwrite, CancellationToken ct) =>
            inner.CommitWriteAsync(temporary, destination, overwrite, ct);
        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => inner.SetPermissionsAsync(path, mode, ct);
    }

    private sealed class TraversingPermissionFileSystem(IRemoteFileSystem inner, string? failFinalModeAt = null) : IRemoteFileSystem
    {
        public bool SupportsPermissions => true;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => inner.GetHomeDirectoryAsync(ct);
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => inner.GetCurrentDirectoryAsync(ct);
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<RemoteFileEntry?> TryStatAsync(string path, CancellationToken ct) => inner.TryStatAsync(path, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            inner.OpenWriteAsync(path, size, progress, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task RenameAsync(string source, string destination, CancellationToken ct) => inner.RenameAsync(source, destination, ct);
        public Task CommitWriteAsync(string temporary, string destination, bool overwrite, CancellationToken ct) =>
            inner.CommitWriteAsync(temporary, destination, overwrite, ct);

        public async Task SetPermissionsAsync(string path, int mode, CancellationToken ct)
        {
            if (path == failFinalModeAt && mode != 0x1C0)
                throw new IOException("injected final permission failure");
            await RequireTraversableAsync(path, ct);
            await inner.SetPermissionsAsync(path, mode, ct);
        }

        public async Task DeleteAsync(string path, bool recursive, CancellationToken ct)
        {
            await RequireTraversableAsync(path, ct);
            if (recursive) await RequireWritableTreeAsync(path, ct);
            await inner.DeleteAsync(path, recursive, ct);
        }

        // A POSIX chmod or unlink resolves every ancestor and needs execute on it.
        private async Task RequireTraversableAsync(string path, CancellationToken ct)
        {
            var index = path.IndexOf('/', 1);
            while (index > 0)
            {
                var ancestor = path[..index];
                var entry = await inner.StatAsync(ancestor, ct);
                if (entry.Kind == RemoteEntryKind.Directory && ((entry.UnixMode ?? 0) & 0x40) == 0)
                    throw new IOException("Permission denied traversing " + ancestor);
                index = path.IndexOf('/', index + 1);
            }
        }

        private async Task RequireWritableTreeAsync(string path, CancellationToken ct)
        {
            var entry = await inner.StatAsync(path, ct);
            if (entry.Kind != RemoteEntryKind.Directory) return;
            if (((entry.UnixMode ?? 0) & 0xC0) != 0xC0)
                throw new IOException("Permission denied deleting under " + path);
            foreach (var child in await inner.ListAsync(path, ct))
                await RequireWritableTreeAsync(child.FullPath, ct);
        }
    }

    private sealed class CountingFileSystem(IRemoteFileSystem inner) : IRemoteFileSystem
    {
        public int ListCalls { get; private set; }
        public int ListedEntries { get; private set; }
        public bool SupportsPermissions => inner.SupportsPermissions;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => inner.GetHomeDirectoryAsync(ct);
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => inner.GetCurrentDirectoryAsync(ct);
        public async Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct)
        {
            ListCalls++;
            IReadOnlyList<RemoteFileEntry> entries = await inner.ListAsync(path, ct);
            ListedEntries += entries.Count;
            return entries;
        }
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            inner.OpenWriteAsync(path, size, progress, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteAsync(path, recursive, ct);
        public Task RenameAsync(string source, string destination, CancellationToken ct) => inner.RenameAsync(source, destination, ct);
        public Task CommitWriteAsync(string temporary, string destination, bool overwrite, CancellationToken ct) =>
            inner.CommitWriteAsync(temporary, destination, overwrite, ct);
        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => inner.SetPermissionsAsync(path, mode, ct);
    }
}
