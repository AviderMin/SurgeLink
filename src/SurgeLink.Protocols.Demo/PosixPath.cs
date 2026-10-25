namespace SurgeLink.Protocols.Demo;

/// <summary>Small helper for the in-memory demo file system.</summary>
internal static class PosixPath
{
    /// <summary>Normalises a POSIX path against <paramref name="currentDirectory"/>.</summary>
    public static string Normalize(string path, string currentDirectory)
    {
        if (string.IsNullOrEmpty(path))
        {
            return currentDirectory;
        }

        var combined = path.StartsWith('/') ? path : currentDirectory.TrimEnd('/') + "/" + path;
        var segments = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>Returns the parent directory of a normalised path.</summary>
    public static string Parent(string path)
    {
        var index = path.TrimEnd('/').LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    /// <summary>Returns the last segment of a normalised path.</summary>
    public static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }
}
