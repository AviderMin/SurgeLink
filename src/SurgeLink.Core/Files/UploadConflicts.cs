namespace SurgeLink.Core.Files;

/// <summary>Collects a folder's conflicts before the user authorizes any replacements.</summary>
public static class UploadConflicts
{
    public static async Task<IReadOnlyList<string>> FindAsync(IRemoteFileSystem fileSystem, string localPath,
        string remotePath, bool isDirectory, CancellationToken cancellationToken)
    {
        var conflicts = new List<string>();
        var listings = new Dictionary<string, Dictionary<string, RemoteFileEntry>>(StringComparer.Ordinal);
        await VisitAsync(localPath, remotePath, isDirectory).ConfigureAwait(false);
        return conflicts;

        async Task VisitAsync(string local, string remote, bool directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Uploads cannot include symbolic links or junctions: " + local);
            var separator = remote.LastIndexOf('/');
            var parent = separator <= 0 ? "/" : remote[..separator];
            if (!listings.TryGetValue(parent, out var siblings))
            {
                var entries = await fileSystem.ListAsync(parent, cancellationToken).ConfigureAwait(false);
                siblings = new Dictionary<string, RemoteFileEntry>(StringComparer.Ordinal);
                foreach (var entry in entries) { siblings.TryAdd(entry.Name, entry); }
                listings.Add(parent, siblings);
            }
            if (!siblings.TryGetValue(remote[(separator + 1)..], out var existing)) { return; }
            if (!directory || existing.Kind != RemoteEntryKind.Directory)
            {
                conflicts.Add(remote);
                return;
            }
            foreach (var entry in new DirectoryInfo(local).EnumerateFileSystemInfos())
                await VisitAsync(entry.FullName, remote.TrimEnd('/') + "/" + entry.Name, entry is DirectoryInfo).ConfigureAwait(false);
        }
    }
}
