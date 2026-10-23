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
        _file = AtomicJsonFile.Read(path, new CredentialFile());
        _file.Profiles = (_file.Profiles ?? new Dictionary<string, Entry>())
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public StoredCredential? Get(string profileId)
    {
        lock (_gate)
        {
            if (!_file.Profiles.TryGetValue(profileId, out var entry))
            {
                return null;
            }

            return new StoredCredential
            {
                Password = Unprotect(entry.Password),
                PrivateKeyPassphrase = Unprotect(entry.Passphrase),
            };
        }
    }

    /// <inheritdoc />
    public void Save(string profileId, StoredCredential credential)
    {
        lock (_gate)
        {
            var next = new CredentialFile { Profiles = new(_file.Profiles, StringComparer.Ordinal) };
            next.Profiles[profileId] = new Entry
            {
                Password = Protect(credential.Password),
                Passphrase = Protect(credential.PrivateKeyPassphrase),
            };
            AtomicJsonFile.Write(_path, next);
            _file = next;
        }
    }

    /// <inheritdoc />
    public void Delete(string profileId)
    {
        lock (_gate)
        {
            if (!_file.Profiles.ContainsKey(profileId)) { return; }
            var next = new CredentialFile { Profiles = new(_file.Profiles, StringComparer.Ordinal) };
            if (next.Profiles.Remove(profileId))
            {
                AtomicJsonFile.Write(_path, next);
                _file = next;
            }
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

    private string? Unprotect(string? protectedBase64)
    {
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
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
