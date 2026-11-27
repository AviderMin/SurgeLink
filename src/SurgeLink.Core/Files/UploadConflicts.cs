namespace SurgeLink.Core.Files;

/// <summary>Collects a folder's conflicts before the user authorizes any replacements.</summary>
public static class UploadConflicts
{
    public static async Task<IReadOnlyList<string>> FindAsync(IRemoteFileSystem fileSystem, string localPath,
        string remotePath, bool isDirectory, CancellationToken cancellationToken)
    {
        var conflicts = new List<string>();
        var listings = new Dictionary<string, Dictionary<string, RemoteFileEntry>>(StringComparer.Ordinal);
        remotePath = remotePath.TrimEnd('/');
        if (remotePath.Length == 0) { remotePath = "/"; }
        await VisitAsync(localPath, remotePath, isDirectory, 0).ConfigureAwait(false);
        return conflicts;

        async Task VisitAsync(string local, string remote, bool directory, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > RemoteFileLimits.MaximumDepth)
                throw new IOException($"Folder uploads cannot exceed {RemoteFileLimits.MaximumDepth} directory levels.");
            if (LocalDownloadDirectory.IsUnsafeReparsePoint(local))
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
                await VisitAsync(entry.FullName, remote.TrimEnd('/') + "/" + entry.Name, entry is DirectoryInfo, depth + 1).ConfigureAwait(false);
        }
    }
}
