using System.IO.Compression;
using System.Collections.Concurrent;
using System.Text;
using SurgeLink.Core.Files;

namespace SurgeLink.Core.Tests;

public sealed class FileTransferQueueTests
{
    [Fact]
    public async Task FolderDownloadCreatesZipWithNestedFilesAndEmptyFolders()
    {
        using var temp = new TempDirectory();
        var fs = new FakeFileSystem();
        fs.Listings["/remote/folder"] = [
            new() { Name = "nested", FullPath = "/remote/folder/nested", Kind = RemoteEntryKind.Directory },
            new() { Name = "empty", FullPath = "/remote/folder/empty", Kind = RemoteEntryKind.Directory },
        ];
        fs.Listings["/remote/folder/nested"] = [
            new() { Name = "hello.txt", FullPath = "/ignored-metadata", Kind = RemoteEntryKind.File, Size = 5 },
        ];
        fs.Files["/remote/folder/nested/hello.txt"] = Encoding.UTF8.GetBytes("hello");
        var item = FolderDownload(temp.File("folder.zip"));
        await using var queue = new FileTransferQueue();
        queue.Enqueue(item, fs);
        Assert.True(await WaitAsync(() => !item.IsActive));
        Assert.Equal(TransferStatus.Completed, item.Status);
        using var zip = ZipFile.OpenRead(item.LocalPath!);
        Assert.NotNull(zip.GetEntry("folder/empty/"));
        Assert.NotNull(zip.GetEntry("folder/nested/"));
        using var reader = new StreamReader(zip.GetEntry("folder/nested/hello.txt")!.Open());
        Assert.Equal("hello", await reader.ReadToEndAsync());
        Assert.Equal(5, item.DoneBytes);
        Assert.Equal(5, item.TotalBytes);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(item.LocalPath!)!, ".surge-*.tmp"));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("failure")]
    [InlineData("existing")]
    [InlineData("unsafe")]
    public async Task FolderZipDoesNotPublishPartialArchivesOrOverwriteExistingFiles(string mode)
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new FakeFileSystem
        {
            OpenReadOverride = async (_, token) =>
            {
                entered.TrySetResult();
                if (mode == "cancel") { await Task.Delay(Timeout.Infinite, token); }
                if (mode == "failure") { throw new IOException("read failed"); }
                return new MemoryStream(Encoding.UTF8.GetBytes("data"));
            },
        };
        fs.Listings["/remote/folder"] = [
            new() { Name = mode == "unsafe" ? "../escape" : "file.txt", FullPath = "/remote/folder/file.txt", Kind = RemoteEntryKind.File, Size = 4 },
        ];
        var item = FolderDownload(temp.File("folder.zip"));
        if (mode == "existing") { await File.WriteAllTextAsync(item.LocalPath!, "keep"); }
        await using var queue = new FileTransferQueue();
        queue.Enqueue(item, fs);
        if (mode == "cancel")
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Cancel(item.Id);
        }
        Assert.True(await WaitAsync(() => !item.IsActive));
        Assert.Equal(mode == "cancel" ? TransferStatus.Cancelled : TransferStatus.Failed, item.Status);
        if (mode == "existing") { Assert.Equal("keep", await File.ReadAllTextAsync(item.LocalPath!)); }
        else { Assert.False(File.Exists(item.LocalPath)); }
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(item.LocalPath!)!, ".surge-*.tmp"));
    }

    private static TransferItem FolderDownload(string localPath) => new()
    {
        Id = Guid.NewGuid().ToString("n"), Direction = TransferDirection.Download,
        Name = "folder.zip", RemotePath = "/remote/folder", LocalPath = localPath, IsDirectory = true,
    };

    [Fact]
    public async Task FolderUploadPreservesNestedFilesAndEmptyDirectories()
    {
        using var temp = new TempDirectory();
        var root = temp.File("folder");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        await File.WriteAllTextAsync(Path.Combine(root, "first.txt"), "abc");
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "second.txt"), "hello");
        var fileSystem = new FakeFileSystem();
        await using var queue = new FileTransferQueue();
        var item = new TransferItem
        {
            Id = Guid.NewGuid().ToString("n"), Direction = TransferDirection.Upload,
            Name = "folder", LocalPath = root, RemotePath = "/remote/folder", IsDirectory = true,
        };
        queue.Enqueue(item, fileSystem);
        Assert.True(await WaitAsync(() => !item.IsActive), "upload did not finish");
        Assert.Equal(TransferStatus.Completed, item.Status);
        Assert.Contains("/remote/folder/empty", fileSystem.Directories.Keys);
        Assert.Equal("abc", Encoding.UTF8.GetString(fileSystem.Files["/remote/folder/first.txt"]));
        Assert.Equal("hello", Encoding.UTF8.GetString(fileSystem.Files["/remote/folder/nested/second.txt"]));
        Assert.Equal(8, item.TotalBytes);
        Assert.Equal(8, item.DoneBytes);
    }

    [Fact]
    public async Task DownloadRunsToCompletionAndSnapshotIsAvailableWithoutASubscriber()
    {
        using var temp = new TempDirectory();
        var fileSystem = new FakeFileSystem();
        fileSystem.Files["/remote/report.txt"] = Encoding.UTF8.GetBytes("hello queue");

        var queue = new FileTransferQueue();
        var item = Download("/remote/report.txt", temp.File("report.txt"), totalBytes: 11);
        queue.Enqueue(item, fileSystem);

        Assert.True(await WaitAsync(() => item.Status == TransferStatus.Completed), "transfer did not complete");
        Assert.Equal("hello queue", await File.ReadAllTextAsync(item.LocalPath!));
        Assert.Equal(TransferStatus.Completed, queue.Snapshot.Single().Status);

        await queue.DisposeAsync();
    }

    [Fact]
    public async Task JobsRunSequentially()
    {
        using var temp = new TempDirectory();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileSystem = new FakeFileSystem();
        fileSystem.Files["/remote/one"] = Encoding.UTF8.GetBytes("one");
        fileSystem.Files["/remote/two"] = Encoding.UTF8.GetBytes("two");
        fileSystem.ReadGate = path => path == "/remote/one" ? firstGate.Task : Task.CompletedTask;

        var queue = new FileTransferQueue();
        var first = Download("/remote/one", temp.File("one"), 3);
        var second = Download("/remote/two", temp.File("two"), 3);
        queue.Enqueue(first, fileSystem);
        Assert.True(
            await WaitAsync(() => first.Status == TransferStatus.Running && fileSystem.OpenReadCalls("/remote/one") == 1),
            "first did not start");

        queue.Enqueue(second, fileSystem);
        Assert.Equal(TransferStatus.Queued, second.Status);
        Assert.Equal(1, fileSystem.MaxConcurrentReads);

        firstGate.SetResult();
        Assert.True(await WaitAsync(() => second.Status == TransferStatus.Completed), "second did not complete");
        Assert.Equal(1, fileSystem.MaxConcurrentReads);

        await queue.DisposeAsync();
    }

    [Fact]
    public async Task CancelOfQueuedItemNeverOpensTheRemoteFile()
    {
        using var temp = new TempDirectory();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileSystem = new FakeFileSystem();
        fileSystem.Files["/remote/one"] = Encoding.UTF8.GetBytes("one");
        fileSystem.Files["/remote/two"] = Encoding.UTF8.GetBytes("two");
        fileSystem.ReadGate = path => path == "/remote/one" ? firstGate.Task : Task.CompletedTask;

        var queue = new FileTransferQueue();
        var first = Download("/remote/one", temp.File("one"), 3);
        var second = Download("/remote/two", temp.File("two"), 3);
        queue.Enqueue(first, fileSystem);
        Assert.True(
            await WaitAsync(() => first.Status == TransferStatus.Running && fileSystem.OpenReadCalls("/remote/one") == 1),
            "first did not start");
        queue.Enqueue(second, fileSystem);

        queue.Cancel(second.Id);

        Assert.Equal(TransferStatus.Cancelled, second.Status);
        firstGate.SetResult();
        Assert.True(await WaitAsync(() => first.Status == TransferStatus.Completed), "first did not complete");
        Assert.Equal(0, fileSystem.OpenReadCalls("/remote/two"));

        await queue.DisposeAsync();
    }

    [Fact]
    public async Task DisposeCancelsActiveWorkAndWaitsForTheWorker()
    {
        using var temp = new TempDirectory();
        var fileSystem = new FakeFileSystem
        {
            OpenReadOverride = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
        };

        var queue = new FileTransferQueue();
        var item = Download("/remote/stuck", temp.File("stuck"), 1);
        queue.Enqueue(item, fileSystem);
        Assert.True(await WaitAsync(() => item.Status == TransferStatus.Running), "transfer did not start");

        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TransferStatus.Cancelled, item.Status);
        Assert.Empty(queue.Snapshot);
    }

    [Fact]
    public async Task FailureIsReportedAsFailedWithTheProtocolMessage()
    {
        using var temp = new TempDirectory();
        var fileSystem = new FakeFileSystem
        {
            OpenReadOverride = (_, _) => throw new IOException("permission denied"),
        };

        var queue = new FileTransferQueue();
        var item = Download("/remote/secret", temp.File("secret"), 1);
        queue.Enqueue(item, fileSystem);

        Assert.True(await WaitAsync(() => item.Status == TransferStatus.Failed), "transfer did not fail");
        Assert.Equal("permission denied", item.Error);

        await queue.DisposeAsync();
    }

    [Fact]
    public async Task UnsubscribedViewStopsReceivingUpdatesAndNewSubscriberSeesTheSnapshot()
    {
        using var temp = new TempDirectory();
        var fileSystem = new FakeFileSystem();
        fileSystem.Files["/remote/one"] = Encoding.UTF8.GetBytes("one");
        fileSystem.Files["/remote/two"] = Encoding.UTF8.GetBytes("two");

        var queue = new FileTransferQueue();
        var callbackCount = 0;
        var unsubscribe = queue.Subscribe(() => Interlocked.Increment(ref callbackCount));

        var first = Download("/remote/one", temp.File("one"), 3);
        queue.Enqueue(first, fileSystem);
        Assert.True(await WaitAsync(() => Volatile.Read(ref callbackCount) > 0), "first transfer did not publish");

        // A view unmounts mid-transfer; the queue must keep working without it.
        unsubscribe();
        var callsAfterUnsubscribe = Volatile.Read(ref callbackCount);

        var second = Download("/remote/two", temp.File("two"), 3);
        queue.Enqueue(second, fileSystem);
        Assert.True(await WaitAsync(() => second.Status == TransferStatus.Completed), "second did not complete");

        Assert.Equal(callsAfterUnsubscribe, Volatile.Read(ref callbackCount));
        Assert.Equal(2, queue.Snapshot.Count);
        Assert.All(queue.Snapshot, item => Assert.Equal(TransferStatus.Completed, item.Status));

        await queue.DisposeAsync();
    }

    [Fact]
    public async Task ClearFinishedDropsTerminalRowsOnly()
    {
        using var temp = new TempDirectory();
        var fileSystem = new FakeFileSystem();
        fileSystem.Files["/remote/a"] = Encoding.UTF8.GetBytes("a");

        var queue = new FileTransferQueue();
        var item = Download("/remote/a", temp.File("a"), 1);
        queue.Enqueue(item, fileSystem);
        Assert.True(await WaitAsync(() => item.Status == TransferStatus.Completed), "transfer did not complete");

        queue.ClearFinished();

        Assert.Empty(queue.Snapshot);
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task DownloadDoesNotOverwriteExistingLocalFile()
    {
        using var temp = new TempDirectory();
        var destination = temp.File("same.txt");
        await File.WriteAllTextAsync(destination, "original");
        var fs = new FakeFileSystem();
        fs.Files["/remote/new"] = Encoding.UTF8.GetBytes("replacement");
        await using var queue = new FileTransferQueue();
        var item = Download("/remote/new", destination, 11);
        queue.Enqueue(item, fs);
        Assert.True(await WaitAsync(() => item.Status == TransferStatus.Failed));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(temp.Root, ".surge-*.tmp"));
    }

    [Fact]
    public async Task InterruptedDownloadDoesNotLeavePartialDestination()
    {
        using var temp = new TempDirectory();
        var fs = new FakeFileSystem { OpenReadOverride = (_, _) => Task.FromResult<Stream>(new FailingReadStream()) };
        await using var queue = new FileTransferQueue();
        var destination = temp.File("interrupted.txt");
        var item = Download("/remote/file", destination, 1024);
        queue.Enqueue(item, fs);
        Assert.True(await WaitAsync(() => item.Status == TransferStatus.Failed));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(temp.Root, ".surge-*.tmp"));
    }

    private sealed class FailingReadStream : MemoryStream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) { throw new IOException("connection dropped"); }
            _read = true;
            buffer.Span[0] = 42;
            return ValueTask.FromResult(1);
        }
    }

    private static TransferItem Download(string remotePath, string localPath, long totalBytes) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Direction = TransferDirection.Download,
        Name = Path.GetFileName(localPath),
        RemotePath = remotePath,
        LocalPath = localPath,
        TotalBytes = totalBytes,
    };

    private static async Task<bool> WaitAsync(Func<bool> predicate, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return predicate();
    }

    private sealed class FakeFileSystem : IRemoteFileSystem
    {
        private readonly ConcurrentDictionary<string, int> _openReads = new(StringComparer.Ordinal);
        private int _activeReads;
        private int _maxConcurrentReads;

        public ConcurrentDictionary<string, IReadOnlyList<RemoteFileEntry>> Listings { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, bool> Directories { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public Func<string, CancellationToken, Task<Stream>>? OpenReadOverride { get; init; }

        public Func<string, Task>? ReadGate { get; set; }

        public bool SupportsPermissions => true;

        public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);

        public int OpenReadCalls(string path) => _openReads.GetValueOrDefault(path);

        public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult("/home/test");

        public Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult("/");

        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(Listings.TryGetValue(path, out var entries) ? entries : (IReadOnlyList<RemoteFileEntry>)Array.Empty<RemoteFileEntry>());

        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            _openReads.AddOrUpdate(path, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref _activeReads);
            InterlockedExtensions.Max(ref _maxConcurrentReads, active);

            if (OpenReadOverride is { } custom)
            {
                return await custom(path, cancellationToken).ConfigureAwait(false);
            }

            if (ReadGate is { } gate)
            {
                await gate(path).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new TrackingStream(new MemoryStream(Files[path], writable: false), () => Interlocked.Decrement(ref _activeReads));
        }

        public Task<Stream> OpenWriteAsync(
            string path,
            long? expectedSize,
            IProgress<TransferProgress>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new UploadStream(bytes => Files[path] = bytes));

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            Directories[path] = true;
            return Task.CompletedTask;
        }

        public Task CommitWriteAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            Files[destinationPath] = Files[temporaryPath];
            Files.TryRemove(temporaryPath, out _);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UploadStream(Action<byte[]> save) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) { save(ToArray()); }
            base.Dispose(disposing);
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var previous = Interlocked.CompareExchange(ref target, value, current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }
    }

    private sealed class TrackingStream(Stream inner, Action onDispose) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                onDispose();
            }

            base.Dispose(disposing);
        }
    }
}
