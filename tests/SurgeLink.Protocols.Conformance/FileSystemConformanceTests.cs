using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Conformance;

public sealed class FileSystemConformanceTests
{
    [Theory]
    [MemberData(nameof(ProtocolConformanceData.FileSystemFixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task FileSystemSatisfiesOperationalContract(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);
        CancellationToken cancellationToken = CancellationToken.None;

        await using ConformanceSession connection = await ConformanceSession.OpenAsync(fixture);
        IRemoteFileSystem fileSystem = connection.Session.FileSystem!;

        string home = await Bounded(fileSystem.GetHomeDirectoryAsync(cancellationToken));
        Assert.False(string.IsNullOrWhiteSpace(home), "home directory must be reported");
        Assert.StartsWith("/", home);
        string homePrefix = home.TrimEnd('/');

        IReadOnlyList<RemoteFileEntry> initial = await Bounded(fileSystem.ListAsync(home, cancellationToken));
        Assert.NotNull(initial);
        AssertEntriesBelongTo(initial, homePrefix);

        string directoryName = "conformance-" + Guid.NewGuid().ToString("n");
        string directory = homePrefix + "/" + directoryName;
        string filePath = directory + "/payload.txt";
        string renamedPath = directory + "/renamed.txt";
        byte[] content = Encoding.UTF8.GetBytes("conformance payload " + directoryName);

        try
        {
            await Bounded(fileSystem.CreateDirectoryAsync(directory, cancellationToken));
            IReadOnlyList<RemoteFileEntry> afterCreate = await Bounded(fileSystem.ListAsync(home, cancellationToken));
            RemoteFileEntry created = Assert.Single(afterCreate, entry => entry.Name == directoryName);
            Assert.Equal(RemoteEntryKind.Directory, created.Kind);

            await using (Stream write = await Bounded(fileSystem.OpenWriteAsync(filePath, content.Length, progress: null, cancellationToken)))
            {
                await write.WriteAsync(content, cancellationToken);
            }

            await using (Stream read = await Bounded(fileSystem.OpenReadAsync(filePath, cancellationToken)))
            {
                using var buffer = new MemoryStream();
                await read.CopyToAsync(buffer, cancellationToken);
                Assert.Equal(content, buffer.ToArray());
            }

            RemoteFileEntry stat = await Bounded(fileSystem.StatAsync(filePath, cancellationToken));
            Assert.Equal(RemoteEntryKind.File, stat.Kind);
            Assert.Equal(content.Length, stat.Size);
            Assert.Equal(filePath, stat.FullPath);

            await Bounded(fileSystem.RenameAsync(filePath, renamedPath, cancellationToken));
            RemoteFileEntry renamedStat = await Bounded(fileSystem.StatAsync(renamedPath, cancellationToken));
            Assert.Equal(content.Length, renamedStat.Size);
            IReadOnlyList<RemoteFileEntry> afterRename = await Bounded(fileSystem.ListAsync(directory, cancellationToken));
            Assert.Contains(afterRename, entry => entry.Name == "renamed.txt");

            string missingPath = directory + "/missing-" + Guid.NewGuid().ToString("n");
            await Assert.ThrowsAsync<ProtocolException>(
                async () => await fileSystem.DeleteAsync(missingPath, false, cancellationToken));

            await Bounded(fileSystem.DeleteAsync(directory, recursive: true, cancellationToken));
            IReadOnlyList<RemoteFileEntry> afterDelete = await Bounded(fileSystem.ListAsync(home, cancellationToken));
            Assert.DoesNotContain(afterDelete, entry => entry.Name == directoryName);
        }
        finally
        {
            try
            {
                await fileSystem
                    .DeleteAsync(directory, recursive: true, CancellationToken.None)
                    .WaitAsync(ProtocolConformanceSuite.DefaultOperationTimeout);
            }
            catch (Exception)
            {
                // Best effort cleanup: the directory is usually already gone.
            }
        }
    }

    private static void AssertEntriesBelongTo(IReadOnlyList<RemoteFileEntry> entries, string homePrefix)
    {
        foreach (RemoteFileEntry entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name), "listing entry needs a name");
            Assert.DoesNotContain("/", entry.Name);
            Assert.False(string.IsNullOrWhiteSpace(entry.FullPath), "listing entry needs a full path");
            Assert.StartsWith("/", entry.FullPath);
            Assert.Equal(homePrefix + "/" + entry.Name, entry.FullPath);
            Assert.True(entry.Size >= 0, "entry size must not be negative");
        }
    }

    private static Task<T> Bounded<T>(Task<T> task) =>
        task.WaitAsync(ProtocolConformanceSuite.DefaultOperationTimeout);

    private static Task Bounded(Task task) =>
        task.WaitAsync(ProtocolConformanceSuite.DefaultOperationTimeout);
}
