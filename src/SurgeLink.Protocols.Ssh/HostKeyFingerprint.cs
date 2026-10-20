using System.Security.Cryptography;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// Computes OpenSSH style host key fingerprints locally from the raw wire format
/// public key blob, so the value can be compared with the output of <c>ssh-keygen -lf</c>.
/// </summary>
public static class HostKeyFingerprint
{
    /// <summary>Computes a SHA-256 fingerprint: base64 without padding, as OpenSSH prints it.</summary>
    /// <param name="hostKey">Raw public key blob as sent by the server.</param>
    public static string ComputeSha256(ReadOnlySpan<byte> hostKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(hostKey, hash);
        return Convert.ToBase64String(hash).TrimEnd('=');
    }
}
