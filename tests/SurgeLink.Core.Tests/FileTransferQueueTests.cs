using System.IO.Compression;
using System.Collections.Concurrent;
using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Core.Tests;

public sealed class FileTransferQueueTests
{
    [Fact]
    public async Task ConcurrentShutdownCallersBothWaitForActiveTransferCleanup()
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new FakeFileSystem
        {
            OpenReadOverride = async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled.TrySetResult(); await cleanup.Task; }
                throw new InvalidOperationException("unreachable");
            },
        };
        await using var queue = new FileTransferQueue();
        var item = Download("/file", temp.File("file"), 1);
        queue.Enqueue(item, fs);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = queue.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { cleanup.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TransferStatus.Cancelled, item.Status);
        Assert.Empty(queue.Snapshot);
        Assert.False(File.Exists(item.LocalPath));
    }

    [Theory]
    [InlineData(RemoteEntryKind.Other)]
    [InlineData(RemoteEntryKind.SymbolicLink)]
    [InlineData(RemoteEntryKind.Directory)]
    public async Task PlainDownloadsNeverOpenSpecialTargets(RemoteEntryKind kind)
    {
        using var temp = new TempDirectory();
        var fs = new FakeFileSystem();
        fs.Listings["/remote"] = [new() { Name = "special", FullPath = "/remote/special", Kind = kind }];
        var item = Download("/remote/special", temp.File("special"), 1);
        await using var queue = new FileTransferQueue();
        queue.Enqueue(item, fs);
        Assert.True(await WaitAsync(() => !item.IsActive));
        Assert.Equal(TransferStatus.Failed, item.Status);
        Assert.Equal(0, fs.OpenReadCalls(item.RemotePath));
        Assert.False(File.Exists(item.LocalPath));
    }

    [Theory]
    [InlineData("/remote/folder", RemoteEntryKind.SymbolicLink)]
    [InlineData("/remote/folder/nested", RemoteEntryKind.SymbolicLink)]
    [InlineData("/remote/folder/nested/file", RemoteEntryKind.SymbolicLink)]
    [InlineData("/remote/folder/nested/file", RemoteEntryKind.Other)]
    public async Task FolderDownloadRechecksEntryTypesBeforeTraversalAndReading(string changedPath, RemoteEntryKind kind)
    {
        using var temp = new TempDirectory();
        var fs = new FakeFileSystem
        {
            StatOverride = path => path == changedPath
                ? new RemoteFileEntry { Name = path[(path.LastIndexOf('/') + 1)..], FullPath = path, Kind = kind }
                : null,
        };
        fs.Listings["/remote/folder"] = [new() { Name = "nested", FullPath = "/remote/folder/nested", Kind = RemoteEntryKind.Directory }];
        fs.Listings["/remote/folder/nested"] = [new() { Name = "file", FullPath = "/remote/folder/nested/file", Kind = RemoteEntryKind.File }];
        fs.Files["/remote/folder/nested/file"] = "outside folder"u8.ToArray();
        var item = FolderDownload(temp.File("folder.zip"));
        await using var queue = new FileTransferQueue();
        queue.Enqueue(item, fs);
        Assert.True(await WaitAsync(() => !item.IsActive));
        Assert.Equal(TransferStatus.Failed, item.Status);
        Assert.Equal(0, fs.OpenReadCalls("/remote/folder/nested/file"));
        if (changedPath != "/remote/folder/nested/file") { Assert.DoesNotContain(changedPath, fs.ListCalls); }
        Assert.False(File.Exists(item.LocalPath));
        Assert.Empty(Directory.GetFiles(temp.Root, ".surge-*.tmp"));
    }

    [Fact]
    public async Task ClosedQueueRejectsNewWorkWithAnExplicitError()
    {
        using var temp = new TempDirectory();
        var queue = new FileTransferQueue();
        await queue.DisposeAsync();
        var item = Download("/file", temp.File("file"), 1);
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(item, new FakeFileSystem()));
        Assert.Equal(TransferStatus.Failed, item.Status);
        Assert.Contains("closed", item.Error);
        Assert.Empty(queue.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingUploadRequiresExplicitAuthorizationForThatPath(bool authorized)
    {
        using var temp = new TempDirectory();
        var local = temp.File("config");
        await File.WriteAllTextAsync(local, "replacement");
        var fs = new FakeFileSystem();
        fs.Files["/config"] = Encoding.UTF8.GetBytes("original");
        fs.Listings["/"] = [new() { Name = "config", FullPath = "/config", Kind = RemoteEntryKind.File }];
        var conflicts = await UploadConflicts.FindAsync(fs, local, "/config", false, CancellationToken.None);
        Assert.Equal(new[] { "/config" }, conflicts);
        var item = new TransferItem
        {
            Id = Guid.NewGuid().ToString("N"), Direction = TransferDirection.Upload, Name = "config", LocalPath = local, RemotePath = "/config",
            OverwritePaths = (authorized ? conflicts : Array.Empty<string>()).ToHashSet(StringComparer.Ordinal),
        };
        await using var queue = new FileTransferQueue();
        queue.Enqueue(item, fs);
        Assert.True(await WaitAsync(() => !item.IsActive));
        Assert.Equal(authorized ? TransferStatus.Completed : TransferStatus.Failed, item.Status);
        Assert.Equal(authorized ? "replacement" : "original", Encoding.UTF8.GetString(fs.Files["/config"]));
    }

    [Fact]
    public async Task FolderPreflightCollectsNestedConflictsWithoutWriting()
    {
        using var temp = new TempDirectory();
        var folder = temp.File("folder");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "config"), "new");
        await File.WriteAllTextAsync(Path.Combine(folder, "nested", "data"), "new");
        var fs = new FakeFileSystem();
        fs.Listings["/"] = [new() { Name = "folder", FullPath = "/folder", Kind = RemoteEntryKind.Directory }];
        fs.Listings["/folder"] = [new() { Name = "config", FullPath = "/folder/config" }, new() { Name = "nested", FullPath = "/folder/nested", Kind = RemoteEntryKind.Directory }];
        fs.Listings["/folder/nested"] = [new() { Name = "data", FullPath = "/folder/nested/data" }];
        var conflicts = await UploadConflicts.FindAsync(fs, folder, "/folder", true, CancellationToken.None);
        Assert.Equal(new[] { "/folder/config", "/folder/nested/data" }, conflicts.Order(StringComparer.Ordinal));
        Assert.Empty(fs.Files);
        Assert.Empty(fs.Directories);
    }

    [Fact]
    public async Task AuthorizationForAnotherPathCannotReplaceAConcurrentUploadTarget()
    {
        using var temp = new TempDirectory();
        var local = temp.File("config");
        await File.WriteAllTextAsync(local, "replacement");
        var fs = new FakeFileSystem();
        // Simulate a target created after an empty listing/preflight.
        fs.Files["/config"] = Encoding.UTF8.GetBytes("concurrent writer");
        var item = new TransferItem
        {
            Id = Guid.NewGuid().ToString("N"), Direction = TransferDirection.Upload, Name = "config", LocalPath = local, RemotePath = "/config",
            OverwritePaths = new HashSet<string>(StringComparer.Ordinal) { "/different-config" },
        };
        await using var queue = new FileTransferQueue();
        queue.Enqueue(item, fs);
        Assert.True(await WaitAsync(() => !item.IsActive));
        Assert.Equal(TransferStatus.Failed, item.Status);
        Assert.Equal("concurrent writer", Encoding.UTF8.GetString(fs.Files["/config"]));
    }

    [Fact]
    public async Task UploadConflictsNormalizesTrailingSlash()
    {
        using var temp = new TempDirectory();
        var folder = temp.File("folder");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "config"), "new");
        var fs = new FakeFileSystem();
        fs.Listings["/"] = [new() { Name = "folder", FullPath = "/folder", Kind = RemoteEntryKind.Directory }];
        fs.Listings["/folder"] = [new() { Name = "config", FullPath = "/folder/config" }];

        var conflicts = await UploadConflicts.FindAsync(fs, folder, "/folder/", true, CancellationToken.None);

        Assert.Equal(new[] { "/folder/config" }, conflicts);
    }

    [Fact]
    public async Task UploadConflictsEnforcesMaximumDepth()
    {
        using var temp = new TempDirectory();
        var root = temp.File("deep");
        var current = root;
        for (var i = 0; i <= RemoteFileLimits.MaximumDepth + 1; i++)
        {
            current = Path.Combine(current, "d");
        }
        Directory.CreateDirectory(current);

        var fs = new FakeFileSystem();
        var remote = "/deep";
        var parent = "/";
        fs.Listings[parent] = [new() { Name = "deep", FullPath = remote, Kind = RemoteEntryKind.Directory }];
        for (var i = 0; i <= RemoteFileLimits.MaximumDepth + 1; i++)
        {
            fs.Listings[remote] = [new() { Name = "d", FullPath = remote + "/d", Kind = RemoteEntryKind.Directory }];
            remote += "/d";
        }

        await Assert.ThrowsAsync<IOException>(() => UploadConflicts.FindAsync(fs, root, "/deep", true, CancellationToken.None));
    }

    [Fact]
    public async Task RemoteFileCopyRejectsEmptySegmentsToPreventSelfCopyBypass()
    {
        var fs = new FakeFileSystem();
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteFileCopy.CopyAsync(fs, "/a/b", "/a//b/sub", CancellationToken.None));
        Assert.Empty(fs.ListCalls);
    }

    [Fact]
    public async Task SubscriberFailuresDoNotStopFollowingTransfersOrOtherSubscribers()
    {
        using var temp = new TempDirectory();
        var fs = new FakeFileSystem();
        fs.Files["/one"] = Encoding.UTF8.GetBytes("one");
        fs.Files["/two"] = Encoding.UTF8.GetBytes("two");
        await using var queue = new FileTransferQueue();
        var finished = new ConcurrentQueue<string>();
        var changes = 0;
        queue.Changed += () => throw new InvalidOperationException("view failed");
        queue.Changed += () => Interlocked.Increment(ref changes);
        queue.TransferFinished += _ => throw new InvalidOperationException("completion failed");
        queue.TransferFinished += item => finished.Enqueue(item.Id);
        var first = Download("/one", temp.File("one"), 3);
        var second = Download("/two", temp.File("two"), 3);
        queue.Enqueue(first, fs);
        queue.Enqueue(second, fs);
        Assert.True(await WaitAsync(() => finished.Count == 2));
        Assert.Equal(new[] { first.Id, second.Id }, finished.ToArray());
        Assert.Equal(TransferStatus.Completed, first.Status);
        Assert.Equal(TransferStatus.Completed, second.Status);
        Assert.True(Volatile.Read(ref changes) > 0);
    }

    [Fact]
    public async Task ShutdownMarksPendingWorkCancelledAndNotifiesCompletionOnce()
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new FakeFileSystem
        {
            OpenReadOverride = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
        };
        await using var queue = new FileTransferQueue();
        var finished = new ConcurrentQueue<string>();
        queue.TransferFinished += item => finished.Enqueue(item.Id);
        var active = Download("/one", temp.File("one"), 1);
        var pending = Download("/two", temp.File("two"), 1);
        queue.Enqueue(active, fs);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Enqueue(pending, fs);
        await queue.DisposeAsync();
        Assert.Equal(TransferStatus.Cancelled, active.Status);
        Assert.Equal(TransferStatus.Cancelled, pending.Status);
        Assert.Equal(0, fs.OpenReadCalls("/two"));
        Assert.Equal(2, finished.Count);
        Assert.Equal(2, finished.Distinct().Count());
    }

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
        fileSystem.BeforeRename = () =>
        {
            if (fileSystem.Directories.ContainsKey(item.RemotePath))
                throw new IOException("The destination appeared before the whole upload finished.");
        };
        queue.Enqueue(item, fileSystem);
        Assert.True(await WaitAsync(() => !item.IsActive), "upload did not finish");
        Assert.Equal(TransferStatus.Completed, item.Status);
        Assert.Contains("/remote/folder/empty", fileSystem.Directories.Keys);
        Assert.Equal("abc", Encoding.UTF8.GetString(fileSystem.Files["/remote/folder/first.txt"]));
        Assert.Equal("hello", Encoding.UTF8.GetString(fileSystem.Files["/remote/folder/nested/second.txt"]));
        Assert.Equal(8, item.TotalBytes);
        Assert.Equal(8, item.DoneBytes);
        Assert.Equal(4, fileSystem.DirectoryCreates);
        Assert.DoesNotContain(fileSystem.Directories.Keys, path => path.Contains(".surge-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InterruptedFolderUploadKeepsOnlyAlreadyPublishedMergeFiles(bool merge, bool cancel)
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(temp.File("folder")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "one"), "one");
        await File.WriteAllTextAsync(Path.Combine(root, "two"), "two");
        var fileSystem = new FakeFileSystem();
        const string destination = "/remote/folder";
        if (merge)
        {
            fileSystem.Directories[destination] = true;
            fileSystem.Listings["/remote"] =
                [new() { Name = "folder", FullPath = destination, Kind = RemoteEntryKind.Directory }];
        }
        var item = new TransferItem
        {
            Id = "folder", Direction = TransferDirection.Upload, Name = "folder",
            LocalPath = root, RemotePath = destination, IsDirectory = true,
        };
        var writes = 0;
        string? firstFile = null;
        void InterruptSecondFile(string path)
        {
            if (++writes == 1) { firstFile = path; return; }
            if (cancel)
            {
                item.Cancellation.Cancel();
                item.Cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new IOException("Connection interrupted.");
        }
        if (merge) { fileSystem.BeforeCommit = InterruptSecondFile; }
        else { fileSystem.BeforeOpenWrite = InterruptSecondFile; }

        await using var queue = new FileTransferQueue();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TransferFinished += _ => completed.TrySetResult();
        queue.Enqueue(item, fileSystem);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(cancel ? TransferStatus.Cancelled : TransferStatus.Failed, item.Status);
        Assert.Equal(merge, fileSystem.Directories.ContainsKey(destination));
        if (merge)
        {
            Assert.Equal(firstFile, Assert.Single(item.PublishedFiles));
            Assert.Equal(firstFile, Assert.Single(fileSystem.Files.Keys));
        }
        else
        {
            Assert.Empty(item.PublishedFiles);
            Assert.Empty(fileSystem.Directories);
            Assert.Empty(fileSystem.Files);
        }
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

        public Func<string, RemoteFileEntry?>? StatOverride { get; init; }
        public Action<string>? BeforeCommit { get; set; }
        public Action<string>? BeforeOpenWrite { get; set; }
        public Action? BeforeRename { get; set; }
        public int DirectoryCreates { get; private set; }

        public ConcurrentQueue<string> ListCalls { get; } = new();

        public bool SupportsPermissions => true;

        public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);

        public int OpenReadCalls(string path) => _openReads.GetValueOrDefault(path);

        public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult("/home/test");

        public Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult("/");

        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
        {
            ListCalls.Enqueue(path);
            return Task.FromResult(Listings.TryGetValue(path, out var entries) ? entries : (IReadOnlyList<RemoteFileEntry>)Array.Empty<RemoteFileEntry>());
        }

        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StatOverride?.Invoke(path) is { } overridden) { return Task.FromResult(overridden); }
            var entry = Listings.Values.SelectMany(entries => entries).FirstOrDefault(item => item.FullPath == path);
            return Task.FromResult(entry ?? new RemoteFileEntry
            {
                Name = path[(path.LastIndexOf('/') + 1)..], FullPath = path,
                Kind = Directories.ContainsKey(path) || Listings.ContainsKey(path) ? RemoteEntryKind.Directory : RemoteEntryKind.File,
                Size = Files.TryGetValue(path, out var bytes) ? bytes.Length : 0,
            });
        }

        public Task<RemoteFileEntry?> TryStatAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StatOverride?.Invoke(path) is { } overridden) { return Task.FromResult<RemoteFileEntry?>(overridden); }
            var entry = Listings.Values.SelectMany(entries => entries).FirstOrDefault(item => item.FullPath == path);
            if (entry is not null) { return Task.FromResult<RemoteFileEntry?>(entry); }
            if (Directories.ContainsKey(path) || Files.ContainsKey(path)) { return Task.FromResult<RemoteFileEntry?>(null); }
            return Task.FromResult<RemoteFileEntry?>(null);
        }

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
            CancellationToken cancellationToken)
        {
            BeforeOpenWrite?.Invoke(path);
            return Task.FromResult<Stream>(new UploadStream(bytes => Files[path] = bytes));
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            DirectoryCreates++;
            Directories[path] = true;
            return Task.CompletedTask;
        }

        public Task CommitWriteAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            BeforeCommit?.Invoke(destinationPath);
            if (!overwrite && Files.ContainsKey(destinationPath)) { throw new IOException("Destination already exists"); }
            Files[destinationPath] = Files[temporaryPath];
            Files.TryRemove(temporaryPath, out _);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
        {
            foreach (var file in Files.Keys.Where(key => Matches(key, path, recursive))) { Files.TryRemove(file, out _); }
            foreach (var directory in Directories.Keys.Where(key => Matches(key, path, recursive))) { Directories.TryRemove(directory, out _); }
            return Task.CompletedTask;
        }

        public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            BeforeRename?.Invoke();
            if (Directories.ContainsKey(destinationPath) || Files.ContainsKey(destinationPath))
                throw new IOException("Destination already exists");
            foreach (var file in Files.Keys.Where(key => Matches(key, sourcePath, recursive: true)))
                if (Files.TryRemove(file, out var bytes)) { Files[destinationPath + file[sourcePath.Length..]] = bytes; }
            foreach (var directory in Directories.Keys.Where(key => Matches(key, sourcePath, recursive: true)))
                if (Directories.TryRemove(directory, out _)) { Directories[destinationPath + directory[sourcePath.Length..]] = true; }
            return Task.CompletedTask;
        }

        private static bool Matches(string candidate, string path, bool recursive) =>
            candidate == path || (recursive && candidate.StartsWith(path + "/", StringComparison.Ordinal));

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
