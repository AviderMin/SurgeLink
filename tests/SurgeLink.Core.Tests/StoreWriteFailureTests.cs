using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class StoreWriteFailureTests
{
    [Theory]
    [InlineData("save")]
    [InlineData("update")]
    [InlineData("insert")]
    [InlineData("delete")]
    public void SessionWriteFailurePreservesMemoryAndDisk(string operation)
    {
        using var temp = new TempDirectory();
        var path = temp.File("sessions.json");
        var store = new JsonSessionStore(path);
        var original = SessionProfile.New("ssh", "example.test", 22, "user");
        store.Upsert(original);
        var before = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");

        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            switch (operation)
            {
                case "save": store.Save([]); break;
                case "update": store.Upsert(original with { Name = "changed" }); break;
                case "insert": store.Upsert(original with { Id = "another" }); break;
                case "delete": store.Delete(original.Id); break;
            }
        });

        Assert.Equal(original, Assert.Single(store.Load()));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CredentialWriteFailurePreservesMemoryAndDisk(bool delete)
    {
        using var temp = new TempDirectory();
        var path = temp.File("credentials.json");
        var store = new EncryptedCredentialStore(path, new FakeSecretProtector());
        store.Save("profile", new StoredCredential { Password = "original" });
        var before = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");

        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            if (delete) { store.Delete("profile"); }
            else { store.Save("profile", new StoredCredential { Password = "changed" }); }
        });

        Assert.Equal("original", store.Get("profile")?.Password);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostKeyWriteFailurePreservesMemoryAndDisk(bool remove)
    {
        using var temp = new TempDirectory();
        var path = temp.File("known-hosts.json");
        var store = new JsonHostKeyStore(path);
        var original = new KnownHostEntry
        {
            Endpoint = "example.test:22", Algorithm = "ssh-ed25519",
            FingerprintSha256 = "SHA256:original", TrustedUtc = DateTimeOffset.UtcNow,
        };
        store.Save(original);
        var before = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");

        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            if (remove) { store.Remove(original.Endpoint); }
            else { store.Save(original with { FingerprintSha256 = "SHA256:changed" }); }
        });

        Assert.True(store.TryGetFingerprint(original.Endpoint, out var entry));
        Assert.Equal(original, entry);
        Assert.Equal(before, File.ReadAllText(path));
    }
}
