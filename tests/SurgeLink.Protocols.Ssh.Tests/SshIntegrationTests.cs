using SurgeLink.Core.Files;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

/// <summary>
/// Real OpenSSH coverage for the behaviour the in-memory fixtures cannot prove:
/// overwrite protection, cancellation and deep trees. Skipped unless the environment
/// variables in <see cref="SshIntegrationEnvironment"/> point at a test server.
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
    public async Task CancelledCopyCreatesNoDestinationOnARealServer()
    {
        await using var target = await SshIntegrationTarget.ConnectAsync();
        var sourceDirectory = target.Root + "/cancel-source";
        await target.Files.CreateDirectoryAsync(sourceDirectory, CancellationToken.None);
        await using (var stream = await target.Files.OpenWriteAsync(sourceDirectory + "/a.txt", 1, null, CancellationToken.None))
        {
            await stream.WriteAsync(new byte[] { 42 });
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteFileCopy.CopyAsync(
            target.Files, sourceDirectory, target.Root + "/cancel-destination", cancellation.Token));

        Assert.Null(await target.Files.TryStatAsync(target.Root + "/cancel-destination", CancellationToken.None));
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
}