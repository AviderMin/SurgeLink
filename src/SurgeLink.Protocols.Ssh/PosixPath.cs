namespace SurgeLink.Protocols.Ssh;

/// <summary>POSIX path helpers shared by the SFTP file system and remote probes.</summary>
public static class PosixPath
{
    /// <summary>
    /// Normalises a POSIX path: resolves <c>.</c>, <c>..</c>, duplicate separators and
    /// relative paths, and optionally expands a leading <c>~</c>.
    /// </summary>
    /// <param name="path">Path to normalise; may be relative.</param>
    /// <param name="currentDirectory">Base directory for relative paths; defaults to <c>/</c>.</param>
    /// <param name="homeDirectory">Remote home directory used to expand a leading <c>~</c>.</param>
    /// <returns>An absolute, normalised path without a trailing separator (except for the root).</returns>
    public static string Normalize(string? path, string? currentDirectory = null, string? homeDirectory = null)
    {
        var raw = Resolve(path, currentDirectory, homeDirectory);

        var segments = new List<string>();
        foreach (var segment in raw.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    /// <summary>
    /// Combines a base directory with a possibly relative path, then normalises the result.
    /// An absolute <paramref name="path"/> replaces the base directory.
    /// </summary>
    /// <param name="baseDirectory">Directory used for relative paths.</param>
    /// <param name="path">Path to append; may be absolute.</param>
    public static string Combine(string? baseDirectory, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Normalize(baseDirectory);
        }

        return path!.StartsWith('/')
            ? Normalize(path)
            : Normalize(path, Normalize(baseDirectory));
    }

    /// <summary>Returns the last segment of a normalised path; the root is returned unchanged.</summary>
    /// <param name="path">Path to inspect.</param>
    public static string GetName(string? path)
    {
        var normalised = Normalize(path);
        if (normalised == "/")
        {
            return "/";
        }

        var index = normalised.LastIndexOf('/');
        return index < 0 ? normalised : normalised[(index + 1)..];
    }

    /// <summary>Resolves a remote path without changing literal names or evaluating
    /// dot segments locally: the server must resolve those through symbolic links.</summary>
    public static string Resolve(string? path, string? currentDirectory = null, string? homeDirectory = null)
    {
        var raw = ExpandHome(path ?? string.Empty, homeDirectory);
        if (raw.Contains('\0')) throw new ArgumentException("Remote paths cannot contain NUL.", nameof(path));
        var directory = string.IsNullOrEmpty(currentDirectory) ? "/" : currentDirectory;
        if (!directory.StartsWith('/')) directory = "/" + directory;
        if (raw.Length == 0) return directory;
        return raw.StartsWith('/') ? raw : directory.TrimEnd('/') + "/" + raw;
    }

    private static string ExpandHome(string raw, string? homeDirectory)
    {
        if (string.IsNullOrEmpty(homeDirectory))
        {
            return raw;
        }

        var home = homeDirectory!.TrimEnd('/');
        if (home.Length == 0) home = "/";
        if (raw == "~")
        {
            return home;
        }

        if (raw.StartsWith("~/", StringComparison.Ordinal))
        {
            return home.TrimEnd('/') + raw[1..];
        }

        return raw;
    }
}
