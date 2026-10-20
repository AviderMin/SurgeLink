using System.Security.Cryptography;
using SurgeLink.Core.Security;

namespace SurgeLink.Core.Tests.Fakes;

/// <summary>Reversible fake protector used to prove secrets are encrypted at rest.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    private const byte Mask = 0x5A;

    /// <summary>Total number of <see cref="Protect"/> calls.</summary>
    public int ProtectCalls { get; private set; }

    /// <summary>Total number of <see cref="Unprotect"/> calls.</summary>
    public int UnprotectCalls { get; private set; }

    /// <summary>When set, <see cref="Unprotect"/> throws like DPAPI would for foreign blobs.</summary>
    public bool ThrowOnUnprotect { get; set; }

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ProtectCalls++;
        var protectedData = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++)
        {
            protectedData[i] = (byte)(plaintext[i] ^ Mask);
        }

        return protectedData;
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData)
    {
        UnprotectCalls++;
        if (ThrowOnUnprotect)
        {
            throw new CryptographicException("The payload could not be decrypted.");
        }

        var plaintext = new byte[protectedData.Length];
        for (int i = 0; i < protectedData.Length; i++)
        {
            plaintext[i] = (byte)(protectedData[i] ^ Mask);
        }

        return plaintext;
    }
}
