using System.Text;
using SurgeLink.Core.Storage;

namespace SurgeLink.Core.Security;

/// <summary>JSON credential store whose values are encrypted with <see cref="ISecretProtector"/>.</summary>
public sealed class EncryptedCredentialStore : ICredentialStore
{
    private sealed class CredentialFile
    {
        public Dictionary<string, Entry> Profiles { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Entry
    {
        public string? Password { get; set; }

        public string? Passphrase { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private CredentialFile _file;

    /// <summary>Creates a store backed by <paramref name="path"/>.</summary>
    public EncryptedCredentialStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        _file = ReadFile();
    }

    private CredentialFile ReadFile(bool tolerateErrors = true)
    {
        var file = AtomicJsonFile.Read(_path, new CredentialFile(), tolerateErrors);
        file.Profiles = (file.Profiles ?? new Dictionary<string, Entry>())
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return file;
    }

    /// <inheritdoc />
    public StoredCredential? Get(string profileId)
    {
        lock (_gate)
        {
            _file = ReadFile();
            if (!_file.Profiles.TryGetValue(profileId, out var entry))
            {
                return null;
            }

            var password = Unprotect(entry.Password, out var passwordFailed);
            var passphrase = Unprotect(entry.Passphrase, out var passphraseFailed);
            return new StoredCredential
            {
                Password = password,
                PrivateKeyPassphrase = passphrase,
                DecryptionFailed = passwordFailed || passphraseFailed,
            };
        }
    }

    /// <inheritdoc />
    public void Save(string profileId, StoredCredential credential)
    {
        if (credential.DecryptionFailed)
            throw new IOException("A saved credential could not be decrypted. Re-enter it or explicitly turn off Remember before saving.");
        lock (_gate)
        {
            var entry = new Entry
            {
                Password = Protect(credential.Password),
                Passphrase = Protect(credential.PrivateKeyPassphrase),
            };
            AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var next = ReadFile(tolerateErrors: false);
                next.Profiles[profileId] = entry;
                AtomicJsonFile.Write(_path, next);
                _file = next;
                return true;
            });
        }
    }

    /// <inheritdoc />
    public void Delete(string profileId)
    {
        lock (_gate)
        {
            AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var next = ReadFile(tolerateErrors: false);
                if (next.Profiles.Remove(profileId)) AtomicJsonFile.Write(_path, next);
                _file = next;
                return true;
            });
        }
    }

    private string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return null;
        }

        return Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(plaintext)));
    }

    private string? Unprotect(string? protectedBase64, out bool failed)
    {
        failed = false;
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(protectedBase64)));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            failed = true;
            return null;
        }
        catch (FormatException)
        {
            failed = true;
            return null;
        }
    }
}
