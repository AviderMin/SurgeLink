using System.Security.Cryptography;

namespace SurgeLink.Core.Files;

public sealed class RemoteFileConflictException() : IOException("The remote file changed after it was loaded. Reload before saving.");

/// <summary>Detects intervening edits and coordinates local writers; SFTP has no server-side compare-and-swap.</summary>
public static class RemoteTextEdit
{
    public static async Task<TextFileDocument> SaveAsync(IRemoteFileSystem fileSystem, string path,
        TextFileDocument baseline, string text, int maximumBytes, CancellationToken ct)
    {
        if (baseline.Sha256 is null) throw new InvalidOperationException("Reload the document to establish its content baseline.");
        var bytes = baseline.Encode(text);
        if (bytes.Length > maximumBytes) throw new IOException("This file exceeds the editor size limit.");
        async Task ValidateAsync(CancellationToken token)
        {
            await RemoteFileAccess.RequireRegularFileAsync(fileSystem, path, token).ConfigureAwait(false);
            await using var input = await fileSystem.OpenReadAsync(path, token).ConfigureAwait(false);
            var current = await BoundedTextFile.ReadDocumentAsync(input, maximumBytes, token).ConfigureAwait(false);
            if (!string.Equals(current.Sha256, baseline.Sha256, StringComparison.Ordinal)) throw new RemoteFileConflictException();
        }
        await RemoteFileWrite.WriteAsync(fileSystem, path, bytes.Length, null, true,
            (output, token) => output.WriteAsync(bytes, token).AsTask(), ct, ValidateAsync).ConfigureAwait(false);
        return new(text, baseline.Encoding, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
