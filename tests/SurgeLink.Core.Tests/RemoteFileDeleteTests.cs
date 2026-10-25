using SurgeLink.Core.Files;
using SurgeLink.Protocols.Demo;

namespace SurgeLink.Core.Tests;

public sealed class RemoteFileDeleteTests
{
    [Fact]
    public async Task DeletesSelectedFilesAndFoldersButKeepsUnselectedEntries()
    {
        var fs = new DemoFileSystem();
        var entries = await fs.ListAsync("/home/demo", default);
        var targets = entries.Where(entry => entry.Name is "demo.txt" or "Projects").ToArray();
        Assert.Equal(2, targets.Length);
        var result = await RemoteFileDelete.DeleteManyAsync(fs, targets, default);
        Assert.Equal(2, result.Deleted.Count);
        Assert.Empty(result.Failures);
        var remaining = await fs.ListAsync("/home/demo", default);
        Assert.DoesNotContain(remaining, entry => entry.Name is "demo.txt" or "Projects");
        Assert.Contains(remaining, entry => entry.Name == "Documents");
    }

    [Fact]
    public async Task OnlyDirectoriesUseRecursiveDeletion()
    {
        var fs = new RecordingFileSystem();
        var result = await RemoteFileDelete.DeleteManyAsync(fs,
            [Entry("/file"), Entry("/folder", RemoteEntryKind.Directory), Entry("/link", RemoteEntryKind.SymbolicLink)], default);
        Assert.Empty(result.Failures);
        Assert.Equal(new[] { ("/file", false), ("/folder", true), ("/link", false) }, fs.Calls);
    }

    [Fact]
    public async Task ReportsPartialFailureAndStillAttemptsTheRestOfTheSelection()
    {
        var fs = new RecordingFileSystem
        {
            Delete = (path, _, _) => path == "/locked" ? Task.FromException(new IOException("permission denied")) : Task.CompletedTask,
        };
        var result = await RemoteFileDelete.DeleteManyAsync(fs, [Entry("/locked"), Entry("/one"), Entry("/two")], default);
        Assert.Equal(new[] { "/one", "/two" }, result.Deleted.Select(entry => entry.FullPath));
        var failure = Assert.Single(result.Failures);
        Assert.Equal("/locked", failure.Entry.FullPath);
        Assert.Equal("permission denied", failure.Message);
        Assert.Equal(3, fs.Calls.Count);
    }

    [Fact]
    public async Task DeduplicatesExactPathsWhileKeepingDistinctCaseSensitiveNames()
    {
        var fs = new RecordingFileSystem();
        var result = await RemoteFileDelete.DeleteManyAsync(fs, [Entry("/README"), Entry("/readme"), Entry("/README")], default);
        Assert.Equal(2, result.Deleted.Count);
        Assert.Equal(new[] { ("/README", false), ("/readme", false) }, fs.Calls);
    }

    [Fact]
    public async Task FreezesSelectionBeforeTheFirstAwait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new RecordingFileSystem
        {
            Delete = async (path, _, token) =>
            {
                if (path == "/one") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            },
        };
        var selected = new List<RemoteFileEntry> { Entry("/one"), Entry("/two") };
        var deleting = RemoteFileDelete.DeleteManyAsync(fs, selected, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        selected.Clear();
        selected.Add(Entry("/unconfirmed"));
        release.TrySetResult();
        var result = await deleting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "/one", "/two" }, result.Deleted.Select(entry => entry.FullPath));
        Assert.DoesNotContain(fs.Calls, call => call.Path == "/unconfirmed");
    }

    [Fact]
    public async Task CancellationStopsBeforeTheNextEntry()
    {
        using var cancellation = new CancellationTokenSource();
        var fs = new RecordingFileSystem { Delete = (_, _, _) => { cancellation.Cancel(); return Task.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteFileDelete.DeleteManyAsync(fs,
            [Entry("/one"), Entry("/two")], cancellation.Token));
        Assert.Equal(new[] { ("/one", false) }, fs.Calls);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotDeleteAnything()
    {
        var fs = new RecordingFileSystem();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteFileDelete.DeleteManyAsync(fs, [Entry("/file")], cancellation.Token));
        Assert.Empty(fs.Calls);
    }

    [Fact]
    public async Task PreservesLiteralNewlineBackslashAndTrailingSpaceInPaths()
    {
        var fs = new RecordingFileSystem();
        const string path = "/data/ghost\n-rw-r--r-- 1 root root 1 Oct 25 12:00 victim\\ ";
        var result = await RemoteFileDelete.DeleteManyAsync(fs, [Entry(path)], default);
        Assert.Equal(path, Assert.Single(result.Deleted).FullPath);
        Assert.Equal(path, Assert.Single(fs.Calls).Path);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/.")]
    [InlineData("/..")]
    [InlineData("relative")]
    [InlineData("/bad\0path")]
    public async Task UnsafeTargetsNeverReachTheProtocol(string path)
    {
        var fs = new RecordingFileSystem();
        var result = await RemoteFileDelete.DeleteManyAsync(fs, [Entry(path)], default);
        Assert.Empty(result.Deleted);
        Assert.Single(result.Failures);
        Assert.Empty(fs.Calls);
    }

    private static RemoteFileEntry Entry(string path, RemoteEntryKind kind = RemoteEntryKind.File) =>
        new() { Name = path[(path.LastIndexOf('/') + 1)..], FullPath = path, Kind = kind };

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        public List<(string Path, bool Recursive)> Calls { get; } = new();
        public Func<string, bool, CancellationToken, Task>? Delete { get; init; }
        public bool SupportsPermissions => true;
        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
        {
            Calls.Add((path, recursive));
            return Delete?.Invoke(path, recursive, cancellationToken) ?? Task.CompletedTask;
        }
        public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string path, long? expectedSize, IProgress<TransferProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
