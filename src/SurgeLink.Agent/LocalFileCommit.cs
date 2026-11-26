using System.Globalization;
using SurgeLink.Core.Files;

namespace SurgeLink.Agent;

/// <summary>
/// Publishes a fully written sibling temporary file at an unused name.
/// </summary>
internal static class LocalFileCommit
{
    private const int MaximumSuffix = 1000;

    /// <summary>Publishes the staged file and returns the actual path.</summary>
    public static string Publish(string temporary, string destination, bool allowAlternateName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporary);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        var directory = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileName(destination);
        var extension = Path.GetExtension(name);
        if (extension.Length == name.Length) { extension = string.Empty; }
        var stem = name[..(name.Length - extension.Length)];
        for (var suffix = 0; suffix <= MaximumSuffix; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = suffix == 0 ? destination
                : LocalDownloadPath.Resolve(directory, stem + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")" + extension);
            try
            {
                File.Move(temporary, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException ex) when (IsNameCollision(ex, candidate))
            {
                if (!allowAlternateName) { throw new LocalFileAlreadyExistsException(destination); }
            }
        }

        throw new IOException("No unused numbered name is available. Choose a different download file name.");
    }

    private static bool IsNameCollision(IOException error, string path) =>
        (OperatingSystem.IsWindows() && error.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7))
        || File.Exists(path) || Directory.Exists(path);
}

/// <summary>The destination appeared while the download was in flight.</summary>
internal sealed class LocalFileAlreadyExistsException(string path)
    : IOException($"The local destination already exists: {path}");
