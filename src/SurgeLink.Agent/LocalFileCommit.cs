namespace SurgeLink.Agent;

/// <summary>
/// Publishes a fully written sibling temporary file. A new destination is created
/// with a non-overwriting move; an existing destination is replaced only through
/// <see cref="File.Replace(string, string, string?)"/>. On NTFS that preserves the
/// replaced file's access control list and alternate data streams (covered by the
/// download tests). Filesystems or encrypted files that reject replacement fail
/// closed instead of falling back to a metadata-destroying overwrite.
/// </summary>
internal static class LocalFileCommit
{
    private const int MaximumAttempts = 4;

    /// <summary>Publishes <paramref name="temporary"/> at <paramref name="destination"/>.</summary>
    /// <exception cref="LocalFileAlreadyExistsException">Overwrite is disabled and the destination exists.</exception>
    public static void Publish(string temporary, string destination, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporary);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            if (!File.Exists(destination))
            {
                try
                {
                    // Never overwrite an entry that appeared after the caller's check.
                    File.Move(temporary, destination, overwrite: false);
                    return;
                }
                catch (IOException) when (File.Exists(destination))
                {
                    if (!overwrite)
                    {
                        throw new LocalFileAlreadyExistsException(destination);
                    }
                }
            }
            else
            {
                if (!overwrite)
                {
                    throw new LocalFileAlreadyExistsException(destination);
                }

                try
                {
                    if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Symbolic links and junctions are not allowed for local Agent downloads.");

                    // ReplaceFile is the only overwrite path: it merges the
                    // replaced file's access control list and keeps its alternate
                    // data streams on NTFS. Failures (locked, encrypted or
                    // unsupported destinations) are surfaced, never downgraded.
                    File.Replace(temporary, destination, destinationBackupFileName: null);
                    return;
                }
                catch (PlatformNotSupportedException exception)
                {
                    throw new IOException(
                        "Replacing the existing local file is not supported here; the destination was left unchanged.",
                        exception);
                }
                catch (FileNotFoundException)
                {
                    // The destination disappeared after it was observed. Retry the
                    // non-overwriting move path instead of recreating or overwriting.
                }
            }
        }

        // Persistent create/remove churn: fail closed rather than guess which
        // destination the caller intended.
        throw new IOException("The local destination kept changing during the download.");
    }
}

/// <summary>The destination appeared while the download was in flight.</summary>
internal sealed class LocalFileAlreadyExistsException(string path)
    : IOException($"The local destination already exists: {path}");
