using SurgeLink.Core.Security;

namespace SurgeLink.Core.Tests;

public sealed class JsonHostKeyStoreTests
{
    private static KnownHostEntry Entry(string endpoint, string fingerprint = "SHA256:abc", string algorithm = "ssh-ed25519") => new()
    {
        Endpoint = endpoint,
        Algorithm = algorithm,
        FingerprintSha256 = fingerprint,
        TrustedUtc = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
    };

    [Fact]
    public void TryGetOnEmptyStoreReturnsFalse()
    {
        using var temp = new TempDirectory();
        var store = new JsonHostKeyStore(temp.File("known-hosts.json"));

        bool found = store.TryGetFingerprint("host:22", out KnownHostEntry entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    [Fact]
    public void SavedFingerprintIsFound()
    {
        using var temp = new TempDirectory();
        var store = new JsonHostKeyStore(temp.File("known-hosts.json"));
        store.Save(Entry("host:22"));

        bool found = store.TryGetFingerprint("host:22", out KnownHostEntry entry);

        Assert.True(found);
        Assert.Equal("SHA256:abc", entry.FingerprintSha256);
        Assert.Equal("ssh-ed25519", entry.Algorithm);
    }

    [Fact]
    public void SaveReplacesExistingEndpoint()
    {
        using var temp = new TempDirectory();
        var store = new JsonHostKeyStore(temp.File("known-hosts.json"));
        store.Save(Entry("host:22"));
        store.Save(Entry("host:22", "SHA256:def", "ecdsa-sha2-nistp256"));

        KnownHostEntry entry = Assert.Single(store.List());
        Assert.Equal("SHA256:def", entry.FingerprintSha256);
        Assert.Equal("ecdsa-sha2-nistp256", entry.Algorithm);
    }

    [Fact]
    public void RemoveExistingEndpointReturnsTrueAndForgetsIt()
    {
        using var temp = new TempDirectory();
        var store = new JsonHostKeyStore(temp.File("known-hosts.json"));
        store.Save(Entry("host:22"));

        bool removed = store.Remove("host:22");

        Assert.True(removed);
        Assert.False(store.TryGetFingerprint("host:22", out _));
        Assert.Empty(store.List());
    }

    [Fact]
    public void RemoveUnknownEndpointReturnsFalse()
    {
        using var temp = new TempDirectory();
        var store = new JsonHostKeyStore(temp.File("known-hosts.json"));

        Assert.False(store.Remove("host:22"));
    }

    [Fact]
    public void ListIsSortedByEndpoint()
    {
        using var temp = new TempDirectory();
        var store = new JsonHostKeyStore(temp.File("known-hosts.json"));
        store.Save(Entry("c.example:22"));
        store.Save(Entry("a.example:22"));
        store.Save(Entry("b.example:22"));

        Assert.Equal(
            new[] { "a.example:22", "b.example:22", "c.example:22" },
            store.List().Select(e => e.Endpoint));
    }

    [Fact]
    public void EntriesPersistAcrossInstances()
    {
        using var temp = new TempDirectory();
        string path = temp.File("known-hosts.json");
        var first = new JsonHostKeyStore(path);
        first.Save(Entry("host:22"));
        first.Save(Entry("other:2222"));

        var second = new JsonHostKeyStore(path);

        Assert.Equal(2, second.List().Count);
        Assert.True(second.TryGetFingerprint("other:2222", out KnownHostEntry entry));
        Assert.Equal("SHA256:abc", entry.FingerprintSha256);
    }

    [Fact]
    public void RemovalPersistsAcrossInstances()
    {
        using var temp = new TempDirectory();
        string path = temp.File("known-hosts.json");
        var first = new JsonHostKeyStore(path);
        first.Save(Entry("host:22"));
        first.Remove("host:22");

        var second = new JsonHostKeyStore(path);

        Assert.Empty(second.List());
    }

    [Fact]
    public void TrustedTimestampRoundTrips()
    {
        using var temp = new TempDirectory();
        var store = new JsonHostKeyStore(temp.File("known-hosts.json"));
        store.Save(Entry("host:22"));

        KnownHostEntry entry = Assert.Single(store.List());
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), entry.TrustedUtc);
    }

    [Fact]
    public void CorruptFileRejectsVerificationInsteadOfTreatingItAsFirstUse()
    {
        using var temp = new TempDirectory();
        string path = temp.File("known-hosts.json");
        File.WriteAllText(path, "not json");

        var store = new JsonHostKeyStore(path);

        Assert.Throws<IOException>(() => store.List());
        Assert.Throws<IOException>(() => store.TryGetFingerprint("host:22", out _));
    }
}
