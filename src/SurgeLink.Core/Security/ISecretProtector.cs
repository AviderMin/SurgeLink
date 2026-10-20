namespace SurgeLink.Core.Security;

/// <summary>Encrypts secrets for the current Windows user so they are useless off-machine.</summary>
public interface ISecretProtector
{
    /// <summary>Encrypts the payload; output is safe to store as base64.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Decrypts a payload produced by <see cref="Protect"/>.</summary>
    byte[] Unprotect(byte[] protectedData);
}
