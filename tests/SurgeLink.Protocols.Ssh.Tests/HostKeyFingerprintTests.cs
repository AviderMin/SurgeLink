using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class HostKeyFingerprintTests
{
    [Fact]
    public void EmptyBlobMatchesEmptySha256()
    {
        Assert.Equal("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU", HostKeyFingerprint.ComputeSha256(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void FingerprintIsUnpaddedBase64OfSha256()
    {
        const string sha256Hex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        var expected = Convert.ToBase64String(Convert.FromHexString(sha256Hex)).TrimEnd('=');

        var fingerprint = HostKeyFingerprint.ComputeSha256("abc"u8);

        Assert.Equal(expected, fingerprint);
        Assert.False(fingerprint.Contains('=', StringComparison.Ordinal));
        Assert.Equal(43, fingerprint.Length);
    }

    [Fact]
    public void DifferentKeysProduceDifferentFingerprints()
    {
        var first = HostKeyFingerprint.ComputeSha256(new byte[] { 1, 2, 3, 4 });
        var second = HostKeyFingerprint.ComputeSha256(new byte[] { 4, 3, 2, 1 });

        Assert.NotEqual(first, second);
    }
}