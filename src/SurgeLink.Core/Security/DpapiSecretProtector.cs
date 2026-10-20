using System.Security.Cryptography;
using System.Text;

namespace SurgeLink.Core.Security;

/// <summary>DPAPI implementation of <see cref="ISecretProtector"/> scoped to the current user.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SurgeLink.v1");

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}
