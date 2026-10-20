namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// POSIX path helpers for the SFTP browser. Paths always use forward slashes and
/// a leading slash, so the UI can show <c>/home/demo/Documents</c> verbatim.
/// </summary>
internal static class SftpPath
{
    /// <summary>Joins a directory and a single child name.</summary>
    public static string Combine(string directory, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Normalize(directory);
        }

        if (name[0] == '/')
        {
            return Normalize(name);
        }

        var parent = Normalize(directory);
        return parent == "/" ? "/" + name : parent + "/" + name;
    }

    /// <summary>Returns the parent directory; the root is its own parent.</summary>
    public static string Parent(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "/")
        {
            return "/";
        }

        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }

    /// <summary>Returns the last path segment; the root keeps <c>/</c>.</summary>
    public static string Name(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "/")
        {
            return "/";
        }

        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    /// <summary>Collapses duplicate and trailing slashes and guarantees a leading slash.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    /// <summary>Builds the breadcrumb chain from the root down to <paramref name="path"/>.</summary>
    public static IReadOnlyList<SftpPathSegment> Segments(string path)
    {
        var normalized = Normalize(path);
        var result = new List<SftpPathSegment> { new("/", "/") };
        if (normalized == "/")
        {
            return result;
        }

        var current = string.Empty;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            result.Add(new SftpPathSegment(segment, current));
        }

        return result;
    }
}

/// <summary>One breadcrumb hop: the label to show and the path it navigates to.</summary>
internal sealed record SftpPathSegment(string Label, string Path);
