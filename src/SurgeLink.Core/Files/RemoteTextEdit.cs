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
            var currentSha256 = await ComputeBoundedSha256Async(input, maximumBytes, token).ConfigureAwait(false);
            if (!string.Equals(currentSha256, baseline.Sha256, StringComparison.Ordinal)) throw new RemoteFileConflictException();
        }
        await RemoteFileWrite.WriteAsync(fileSystem, path, bytes.Length, null, true,
            (output, token) => output.WriteAsync(bytes, token).AsTask(), ct, ValidateAsync).ConfigureAwait(false);
        return new(text, baseline.Encoding, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task<string?> ComputeBoundedSha256Async(Stream input, int maximumBytes, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Math.Min(16384, Math.Max(1, maximumBytes + 1))];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return Convert.ToHexString(hasher.GetHashAndReset());
            }

            total += read;
            if (total > maximumBytes)
            {
                return null;
            }

            hasher.AppendData(buffer, 0, read);
        }
    }
}
