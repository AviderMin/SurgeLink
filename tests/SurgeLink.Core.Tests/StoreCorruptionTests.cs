using SurgeLink.Core.Security;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class StoreCorruptionTests
{
    [Fact]
    public void NullSessionEntryDoesNotReachTheWorkspace()
    {
        using var temp = new TempDirectory();
        var path = temp.File("sessions.json");
        File.WriteAllText(path, "[null]");
        Assert.Empty(new JsonSessionStore(path).Load());
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"endpoint\":null}]")]
    public void InvalidHostKeyEntryDoesNotCrashStartup(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.File("known-hosts.json");
        File.WriteAllText(path, json);
        Assert.Empty(new JsonHostKeyStore(path).List());
    }

    [Theory]
    [InlineData("{\"profiles\":null}")]
    [InlineData("{\"profiles\":{\"profile\":null}}")]
    public void NullCredentialDataIsTreatedAsMissing(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.File("credentials.json");
        File.WriteAllText(path, json);
        var store = new EncryptedCredentialStore(path, new FakeSecretProtector());
        Assert.Null(store.Get("profile"));
        store.Save("new", new StoredCredential { Password = "test" });
        Assert.Equal("test", store.Get("new")?.Password);
    }
}
