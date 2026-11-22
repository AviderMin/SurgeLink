using SurgeLink.Core.Files;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

/// <summary>
/// Real OpenSSH coverage for the behaviour the in-memory fixtures cannot prove:
/// overwrite protection, transfer cancellation, deep trees and scan cost. Skipped
/// unless the environment variables in <see cref="SshIntegrationEnvironment"/> point
/// at a test server; .github/workflows/ci.yml provisions one on every run.
/// </summary>
public sealed class SshIntegrationTests
{
    [SshIntegrationFact]
    public async Task CopyRefusesToOverwriteAnExistingRemoteFile()
    {
        await using var target = await SshIntegrationTarget.ConnectAsync();
        var source = await target.CreateFileAsync("source.txt", "source");
        var existing = await target.CreateFileAsync("existing.txt", "keep");

        await Assert.ThrowsAsync<IOException>(
            () => RemoteFileCopy.CopyAsync(target.Files, source, existing, CancellationToken.None));

        Assert.Equal("keep", await target.ReadTextAsync(existing));
        Assert.Equal("source", await target.ReadTextAsync(source));
    }

    [SshIntegrationFact]
    public async Task RenameRefusesToOverwriteAnExistingRemoteFile()
    {
        await using var target = await SshIntegrationTarget.ConnectAsync();
        var source = await target.CreateFileAsync("rename-source.txt", "source");
        var existing = await target.CreateFileAsync("rename-target.txt", "keep");

        // IRemoteFileSystem requires the rename to fail rather than replace the target.
        // OpenSSH SFTP servers implement RENAME with a plain rename(2), so a transport that
        // forwards it blindly would silently destroy the existing file.
        await Assert.ThrowsAnyAsync<Exception>(() => target.Files.RenameAsync(source, existing, CancellationToken.None));

        Assert.Equal("keep", await target.ReadTextAsync(existing));
        Assert.Equal("source", await target.ReadTextAsync(source));
    }

    [SshIntegrationFact]
    public async Task CopyingManyFilesDoesNotScanTheSourceDirectoryPerFile()
    {
        await using var target = await SshIntegrationTarget.ConnectAsync();
        var source = target.Root + "/bulk";
        await target.Files.CreateDirectoryAsync(source, CancellationToken.None);
        for (var index = 0; index < 20; index++)
        {
            await target.CreateFileAtAsync(source + "/file" + index + ".txt", "data-" + index);
        }

        var before = target.DirectoryListRequests;
        await RemoteFileCopy.CopyAsync(target.Files, source, target.Root + "/bulk-copy", CancellationToken.None);
        var scans = target.DirectoryListRequests - before;

        // One scan for the source root, one for the final destination probe and one for
        // the source listing. A per-file parent scan would exceed twenty here.
        Assert.True(scans <= 5, $"Copying 20 files issued {scans} SFTP directory scans.");
        Assert.Equal("data-7", await target.ReadTextAsync(target.Root + "/bulk-copy/file7.txt"));
    }

    [SshIntegrationFact]
    public async Task CancellingDuringTransferRemovesStagingAndKeepsTheConnectionUsable()
    {
        await using var target = await SshIntegrationTarget.ConnectAsync();
        var sourceDirectory = target.Root + "/cancel-source";
        await target.Files.CreateDirectoryAsync(sourceDirectory, CancellationToken.None);
        var sourceFile = await target.CreateFileAtAsync(sourceDirectory + "/big.txt", new string('x', 1024 * 1024));
        var destination = target.Root + "/cancel-destination";

        using var cancellation = new CancellationTokenSource();
        // Cancel only after bytes have actually flowed, so the cleanup path runs mid-transfer.
        var cancelling = new CancelAfterBytesFileSystem(target.Files, bytesBeforeCancel: 64 * 1024, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteFileCopy.CopyAsync(
            cancelling, sourceDirectory, destination, cancellation.Token));

        // Nothing published, no staging tree left, and the session survived the cancellation.
        Assert.Null(await target.Files.TryStatAsync(destination, CancellationToken.None));
        var entries = (await target.Files.ListAsync(target.Root, CancellationToken.None)).Select(entry => entry.Name).ToArray();
        Assert.DoesNotContain(entries, name => name.StartsWith(".surge-", StringComparison.Ordinal));
        Assert.Equal(RemoteEntryKind.File, (await target.Files.StatAsync(sourceFile, CancellationToken.None)).Kind);
    }

    [SshIntegrationFact]
    public async Task DeepDirectoryCopyRoundTripsAboveSixtyFourLevels()
    {
        await using var target = await SshIntegrationTarget.ConnectAsync();
        var source = target.Root + "/deep";
        await target.Files.CreateDirectoryAsync(source, CancellationToken.None);
        var path = source;
        for (var level = 0; level < 70; level++)
        {
            path += "/d" + level;
            await target.Files.CreateDirectoryAsync(path, CancellationToken.None);
        }
        await using (var stream = await target.Files.OpenWriteAsync(path + "/leaf.txt", 4, null, CancellationToken.None))
        {
            await stream.WriteAsync("leaf"u8.ToArray());
        }

        var destination = target.Root + "/deep-copy";
        await RemoteFileCopy.CopyAsync(target.Files, source, destination, CancellationToken.None);

        var relative = string.Join('/', Enumerable.Range(0, 70).Select(level => "d" + level));
        Assert.Equal("leaf", await target.ReadTextAsync(destination + "/" + relative + "/leaf.txt"));
        // The source must still be intact after a successful copy.
        Assert.Equal("leaf", await target.ReadTextAsync(source + "/" + relative + "/leaf.txt"));
    }

    [SshIntegrationFact]
    public async Task DisposedSessionFailsFileOperationsInsteadOfHanging()
    {
        var session = await SshIntegration.ConnectSessionAsync();
        var files = session.FileSystem ?? throw new InvalidOperationException("The SSH session has no file system.");
        _ = await files.GetHomeDirectoryAsync(CancellationToken.None); // open the SFTP transport
        await session.DisposeAsync();

        var failure = Assert.ThrowsAnyAsync<Exception>(() => files.StatAsync("/", CancellationToken.None));
        await failure.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Delegates to the real file system and cancels the copy once enough bytes have been read.</summary>
    private sealed class CancelAfterBytesFileSystem(IRemoteFileSystem inner, long bytesBeforeCancel, CancellationTokenSource cancellation)
        : IRemoteFileSystem
    {
        public bool SupportsPermissions => inner.SupportsPermissions;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => inner.GetHomeDirectoryAsync(ct);
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => inner.GetCurrentDirectoryAsync(ct);
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<RemoteFileEntry?> TryStatAsync(string path, CancellationToken ct) => inner.TryStatAsync(path, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public async Task<Stream> OpenReadAsync(string path, RemoteFileEntry knownEntry, CancellationToken ct) =>
            new CancelAfterBytesStream(await inner.OpenReadAsync(path, knownEntry, ct), bytesBeforeCancel, cancellation);
        public Task<Stream> OpenWriteAsync(string path, long? expectedSize, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            inner.OpenWriteAsync(path, expectedSize, progress, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteAsync(path, recursive, ct);
        public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken ct) => inner.RenameAsync(sourcePath, destinationPath, ct);
        public Task CommitWriteAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken ct) =>
            inner.CommitWriteAsync(temporaryPath, destinationPath, overwrite, ct);
        public Task SetPermissionsAsync(string path, int unixMode, CancellationToken ct) => inner.SetPermissionsAsync(path, unixMode, ct);
    }

    private sealed class CancelAfterBytesStream(Stream inner, long limit, CancellationTokenSource cancellation) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        private int Count(int read)
        {
            _read += read;
            if (_read >= limit) cancellation.Cancel();
            return read;
        }
    }
}