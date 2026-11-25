namespace SurgeLink.Core.Files;

/// <summary>Validates remote names before interpreting them as Windows download paths.</summary>
public static class LocalDownloadPath
{
    /// <summary>Creates and secures the destination directory for the entire write/publish/cleanup lifetime.</summary>
    public static IDisposable LockDirectory(string directory) => LocalDownloadDirectory.Open(directory);

    /// <summary>Opens a local file without following Windows symbolic links or junctions.</summary>
    public static FileStream OpenExistingFileForRead(string path) => LocalDownloadDirectory.OpenExistingFileForRead(path);

    /// <summary>Checks a destination's own file type. Callers secure its parent directory separately.</summary>
    public static bool IsSafeExistingFile(string path) => LocalDownloadDirectory.IsSafeExistingFile(path);

    public static string Resolve(string directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
        {
            throw new IOException("The remote file name cannot be safely downloaded on Windows.");
        }
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && "123456789¹²³".Contains(stem[3])))
        {
            throw new IOException("The remote file name is reserved by Windows.");
        }
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { throw new IOException("Download path escapes the selected directory."); }
        return path;
    }
}

/// <summary>The local path contains an unsupported file type or redirecting reparse point.</summary>
public sealed class UnsafeLocalPathException(string message) : IOException(message);
