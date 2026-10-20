using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;

namespace SurgeLink.Core.Tests;

public sealed class JsonSessionStoreTests
{
    private static SessionProfile Profile(string id = "p1", string name = "Session") => new()
    {
        Id = id,
        Name = name,
        ProtocolId = "ssh",
        Host = "example.test",
        Port = 2222,
        Username = "alice",
    };

    [Fact]
    public void MissingFileLoadsEmpty()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));

        Assert.Empty(store.Load());
    }

    [Fact]
    public void CorruptFileLoadsEmpty()
    {
        using var temp = new TempDirectory();
        string path = temp.File("sessions.json");
        File.WriteAllText(path, "not json at all");

        var store = new JsonSessionStore(path);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void UpsertInsertsProfileAndPersists()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));

        store.Upsert(Profile());

        SessionProfile saved = Assert.Single(store.Load());
        Assert.Equal("p1", saved.Id);
        Assert.Equal("Session", saved.Name);
        Assert.Single(new JsonSessionStore(temp.File("sessions.json")).Load());
    }

    [Fact]
    public void UpsertUpdatesExistingProfileInPlace()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));
        store.Upsert(Profile());
        store.Upsert(Profile(name: "Renamed"));

        SessionProfile saved = Assert.Single(store.Load());
        Assert.Equal("Renamed", saved.Name);
    }

    [Fact]
    public void UpsertAppendsNewProfilesInOrder()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));

        store.Upsert(Profile("p3", "third"));
        store.Upsert(Profile("p1", "first"));
        store.Upsert(Profile("p2", "second"));

        Assert.Equal(new[] { "p3", "p1", "p2" }, store.Load().Select(p => p.Id));
    }

    [Fact]
    public void SaveReplacesWholeSetAndPreservesOrder()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));

        store.Save(new[] { Profile("b", "B"), Profile("a", "A") });
        Assert.Equal(new[] { "b", "a" }, store.Load().Select(p => p.Id));

        store.Save(new[] { Profile("c", "C") });
        Assert.Equal(new[] { "c" }, store.Load().Select(p => p.Id));
    }

    [Fact]
    public void SaveEmptyListClearsStore()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));
        store.Upsert(Profile());

        store.Save(Array.Empty<SessionProfile>());

        Assert.Empty(store.Load());
        Assert.Empty(new JsonSessionStore(temp.File("sessions.json")).Load());
    }

    [Fact]
    public void DeleteRemovesProfileAndPersists()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));
        store.Upsert(Profile());

        bool removed = store.Delete("p1");

        Assert.True(removed);
        Assert.Empty(store.Load());
        Assert.Empty(new JsonSessionStore(temp.File("sessions.json")).Load());
    }

    [Fact]
    public void DeleteMissingProfileReturnsFalse()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));
        store.Upsert(Profile());

        Assert.False(store.Delete("unknown"));
        Assert.Single(store.Load());
    }

    [Fact]
    public void ProfilesPersistAcrossStoreInstances()
    {
        using var temp = new TempDirectory();
        string path = temp.File("sessions.json");
        var first = new JsonSessionStore(path);
        first.Upsert(Profile("p1", "one"));
        first.Upsert(Profile("p2", "two"));

        var second = new JsonSessionStore(path);

        Assert.Equal(new[] { "p1", "p2" }, second.Load().Select(p => p.Id));
    }

    [Fact]
    public void ProfileRoundTripsEveryProperty()
    {
        using var temp = new TempDirectory();
        string path = temp.File("sessions.json");
        var profile = new SessionProfile
        {
            Id = "full",
            Name = "Full profile",
            ProtocolId = "ssh",
            Host = "remote.example",
            Port = 2200,
            Username = "bob",
            Authentication = AuthenticationMethod.PublicKey,
            PrivateKeyPath = "/home/bob/.ssh/id_ed25519",
            Group = "Production",
            ColorTag = "#FF8800",
            LastUsedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)),
            Options = new Dictionary<string, string>
            {
                ["encoding"] = "utf-8",
                ["term"] = "xterm-256color",
            },
        };

        var store = new JsonSessionStore(path);
        store.Upsert(profile);

        SessionProfile loaded = Assert.Single(new JsonSessionStore(path).Load());
        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal(profile.Name, loaded.Name);
        Assert.Equal(profile.ProtocolId, loaded.ProtocolId);
        Assert.Equal(profile.Host, loaded.Host);
        Assert.Equal(profile.Port, loaded.Port);
        Assert.Equal(profile.Username, loaded.Username);
        Assert.Equal(profile.Authentication, loaded.Authentication);
        Assert.Equal(profile.PrivateKeyPath, loaded.PrivateKeyPath);
        Assert.Equal(profile.Group, loaded.Group);
        Assert.Equal(profile.ColorTag, loaded.ColorTag);
        Assert.Equal(profile.LastUsedUtc, loaded.LastUsedUtc);
        Assert.Equal(2, loaded.Options.Count);
        Assert.Equal("utf-8", loaded.Options["encoding"]);
        Assert.Equal("xterm-256color", loaded.Options["term"]);
    }

    [Fact]
    public void LoadReturnsSnapshotThatLaterMutationsDoNotChange()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));
        store.Upsert(Profile("p1"));
        IReadOnlyList<SessionProfile> snapshot = store.Load();

        store.Upsert(Profile("p2"));

        Assert.Single(snapshot);
    }

    [Fact]
    public void ConcurrentUpsertsAreSafeAndComplete()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));

        Parallel.For(0, 40, i => store.Upsert(Profile("p" + i, "Session " + i)));

        Assert.Equal(40, store.Load().Count);
        Assert.Equal(40, new JsonSessionStore(temp.File("sessions.json")).Load().Count);
    }

    [Fact]
    public void ConcurrentReadsAndWritesDoNotThrow()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));
        store.Upsert(Profile("seed"));

        Parallel.For(0, 60, i =>
        {
            if (i % 3 == 0)
            {
                store.Upsert(Profile("p" + i, "Session " + i));
            }
            else if (i % 3 == 1)
            {
                _ = store.Load();
            }
            else
            {
                store.Delete("seed");
            }
        });

        Assert.NotNull(store.Load());
    }
}
