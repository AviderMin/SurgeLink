using SurgeLink.Core.Security;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class EncryptedCredentialStoreTests
{
    [Fact]
    public void SaveThenGetReturnsBothSecrets()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedCredentialStore(temp.File("credentials.json"), new FakeSecretProtector());

        store.Save("profile-1", new StoredCredential
        {
            Password = "hunter2",
            PrivateKeyPassphrase = "key phrase",
        });

        StoredCredential? stored = store.Get("profile-1");
        Assert.NotNull(stored);
        Assert.Equal("hunter2", stored!.Password);
        Assert.Equal("key phrase", stored.PrivateKeyPassphrase);
    }

    [Fact]
    public void SecretsAreNotStoredAsPlaintext()
    {
        using var temp = new TempDirectory();
        string path = temp.File("credentials.json");
        var store = new EncryptedCredentialStore(path, new FakeSecretProtector());

        store.Save("profile-1", new StoredCredential
        {
            Password = "super-secret-password",
            PrivateKeyPassphrase = "super-secret-passphrase",
        });

        string json = File.ReadAllText(path);
        Assert.Contains("profiles", json);
        Assert.DoesNotContain("super-secret-password", json);
        Assert.DoesNotContain("super-secret-passphrase", json);
    }

    [Fact]
    public void GetUnknownProfileReturnsNull()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedCredentialStore(temp.File("credentials.json"), new FakeSecretProtector());

        Assert.Null(store.Get("missing"));
    }

    [Fact]
    public void SaveNullSecretsYieldsEmptyCredential()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedCredentialStore(temp.File("credentials.json"), new FakeSecretProtector());

        store.Save("profile-1", new StoredCredential());

        StoredCredential? stored = store.Get("profile-1");
        Assert.NotNull(stored);
        Assert.Null(stored!.Password);
        Assert.Null(stored.PrivateKeyPassphrase);
    }

    [Fact]
    public void EmptyStringsAreNormalisedToNull()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedCredentialStore(temp.File("credentials.json"), new FakeSecretProtector());

        store.Save("profile-1", new StoredCredential { Password = string.Empty, PrivateKeyPassphrase = string.Empty });

        StoredCredential? stored = store.Get("profile-1");
        Assert.NotNull(stored);
        Assert.Null(stored!.Password);
        Assert.Null(stored.PrivateKeyPassphrase);
    }

    [Fact]
    public void SaveReplacesExistingCredential()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedCredentialStore(temp.File("credentials.json"), new FakeSecretProtector());
        store.Save("profile-1", new StoredCredential { Password = "old" });

        store.Save("profile-1", new StoredCredential { Password = "new" });

        Assert.Equal("new", store.Get("profile-1")!.Password);
    }

    [Fact]
    public void DeleteRemovesCredential()
    {
        using var temp = new TempDirectory();
        string path = temp.File("credentials.json");
        var store = new EncryptedCredentialStore(path, new FakeSecretProtector());
        store.Save("profile-1", new StoredCredential { Password = "hunter2" });

        store.Delete("profile-1");

        Assert.Null(store.Get("profile-1"));
        Assert.Null(new EncryptedCredentialStore(path, new FakeSecretProtector()).Get("profile-1"));
    }

    [Fact]
    public void DeleteMissingProfileIsANoOp()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedCredentialStore(temp.File("credentials.json"), new FakeSecretProtector());

        store.Delete("missing");

        Assert.Null(store.Get("missing"));
    }

    [Fact]
    public void CredentialsPersistAcrossInstances()
    {
        using var temp = new TempDirectory();
        string path = temp.File("credentials.json");
        var protector = new FakeSecretProtector();
        var first = new EncryptedCredentialStore(path, protector);
        first.Save("profile-1", new StoredCredential { Password = "hunter2", PrivateKeyPassphrase = "phrase" });

        var second = new EncryptedCredentialStore(path, protector);

        StoredCredential? stored = second.Get("profile-1");
        Assert.Equal("hunter2", stored!.Password);
        Assert.Equal("phrase", stored.PrivateKeyPassphrase);
    }

    [Fact]
    public void ProfilesAreIsolatedFromEachOther()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedCredentialStore(temp.File("credentials.json"), new FakeSecretProtector());
        store.Save("profile-1", new StoredCredential { Password = "one" });
        store.Save("profile-2", new StoredCredential { Password = "two" });

        store.Delete("profile-1");

        Assert.Null(store.Get("profile-1"));
        Assert.Equal("two", store.Get("profile-2")!.Password);
    }

    [Fact]
    public void CorruptBase64BlobReturnsNullInsteadOfThrowing()
    {
        using var temp = new TempDirectory();
        string path = temp.File("credentials.json");
        File.WriteAllText(path, "{\"profiles\":{\"profile-1\":{\"password\":\"@@not-base64@@\"}}}");
        var store = new EncryptedCredentialStore(path, new FakeSecretProtector());

        StoredCredential? stored = store.Get("profile-1");

        Assert.NotNull(stored);
        Assert.Null(stored!.Password);
    }

    [Fact]
    public void CorruptJsonFileLoadsAsEmptyStore()
    {
        using var temp = new TempDirectory();
        string path = temp.File("credentials.json");
        File.WriteAllText(path, "{ broken json");
        var store = new EncryptedCredentialStore(path, new FakeSecretProtector());

        Assert.Null(store.Get("profile-1"));
    }

    [Fact]
    public void UnprotectFailureReturnsNullInsteadOfThrowing()
    {
        using var temp = new TempDirectory();
        var protector = new FakeSecretProtector();
        var store = new EncryptedCredentialStore(temp.File("credentials.json"), protector);
        store.Save("profile-1", new StoredCredential { Password = "hunter2" });
        protector.ThrowOnUnprotect = true;

        StoredCredential? stored = store.Get("profile-1");

        Assert.NotNull(stored);
        Assert.Null(stored!.Password);
        Assert.True(stored.DecryptionFailed);
        Assert.Throws<IOException>(() => store.Save("profile-1", stored));
        protector.ThrowOnUnprotect = false;
        Assert.Equal("hunter2", store.Get("profile-1")!.Password);
    }
}
