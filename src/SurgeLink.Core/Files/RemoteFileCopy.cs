namespace SurgeLink.Core.Files;

/// <summary>Remote streaming copy with directory recursion and destination conflict checks.</summary>
public static class RemoteFileCopy
{
    public static async Task CopyAsync(IRemoteFileSystem fileSystem, string source, string destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        source = source.TrimEnd('/');
        destination = destination.TrimEnd('/');
        if (!source.StartsWith('/') || !destination.StartsWith('/') ||
            source.Split('/').Any(segment => segment is "." or "..") ||
            destination.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Copy paths must be absolute and normalized.");
        }
        if (source.Length == 0 || destination == source || destination.StartsWith(source + "/", StringComparison.Ordinal))
        {
            throw new IOException("Cannot copy an item into itself or its own subfolder.");
        }
        await CopyEntryAsync(source, destination, 0).ConfigureAwait(false);

        async Task CopyEntryAsync(string from, string to, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 128) { throw new IOException("Directory nesting exceeds the copy limit."); }
            var entry = await fileSystem.StatAsync(from, cancellationToken).ConfigureAwait(false);
            if (entry.Kind is not (RemoteEntryKind.File or RemoteEntryKind.Directory))
            {
                throw new IOException("Copying symbolic links or special files is not supported.");
            }
            var separator = to.LastIndexOf('/');
            var parent = separator == 0 ? "/" : to[..separator];
            var name = to[(separator + 1)..];
            var siblings = await fileSystem.ListAsync(parent, cancellationToken).ConfigureAwait(false);
            if (siblings.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException($"Destination already exists: {to}");
            }
            if (entry.IsDirectory)
            {
                var children = await fileSystem.ListAsync(from, cancellationToken).ConfigureAwait(false);
                await fileSystem.CreateDirectoryAsync(to, cancellationToken).ConfigureAwait(false);
                foreach (var child in children)
                {
                    if (string.IsNullOrEmpty(child.Name) || child.Name is "." or ".." || child.Name.Contains('/') || child.Name.Contains('\0'))
                    {
                        throw new IOException("The server returned an invalid child name.");
                    }
                    await CopyEntryAsync(from + "/" + child.Name, to + "/" + child.Name, depth + 1).ConfigureAwait(false);
                }
            }
            else
            {
                await using var input = await fileSystem.OpenReadAsync(from, cancellationToken).ConfigureAwait(false);
                await using var output = await fileSystem.OpenWriteAsync(to, entry.Size, null, cancellationToken).ConfigureAwait(false);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
