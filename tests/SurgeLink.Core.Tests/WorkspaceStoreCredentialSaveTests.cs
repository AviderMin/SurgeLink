using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

/// <summary>
/// Regression coverage for a saved secret that could not be decrypted. Saving an
/// edited profile must commit the profile without rewriting (or deleting) the
/// untouched ciphertext and must not throw after the profile has hit disk.
/// </summary>
public sealed class WorkspaceStoreCredentialSaveTests
{
    [Fact]
    public async Task SaveProfileWithUndecryptableCredentialCommitsProfileWithoutRewritingSecret()
    {
        using var temp = new TempDirectory();
        var profileStore = new JsonSessionStore(temp.File("sessions.json"));
        var credentials = new RejectingCredentialStore();
        var catalog = new ProtocolCatalog();
        catalog.Register(new FakeProtocolPlugin());
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, profileStore, credentials, new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);

        var original = SessionProfile.New("fake", "old.test", 22, "alice");
        profileStore.Upsert(original);

        // A stored secret that cannot be decrypted surfaces as DecryptionFailed; the
        // real EncryptedCredentialStore throws IOException on Save of such a value.
        var undecryptable = new StoredCredential { DecryptionFailed = true };
        credentials.Seed(original.Id, undecryptable);
        var edited = original with { Host = "new.test", Name = "Renamed", Port = 2200 };

        var exception = Record.Exception(() => workspace.SaveProfile(edited, undecryptable));

        Assert.Null(exception);

        var saved = Assert.Single(profileStore.Load());
        Assert.Equal(edited.Id, saved.Id);
        Assert.Equal("new.test", saved.Host);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(2200, saved.Port);

        // The credential write must be skipped entirely: no Save, no Delete, ciphertext untouched.
        Assert.Equal(0, credentials.SaveCalls);
        Assert.Equal(0, credentials.DeleteCalls);
        Assert.Same(undecryptable, credentials.Get(original.Id));

        // The UI snapshot is still refreshed from disk after the committed profile.
        Assert.Equal("Renamed", Assert.Single(workspace.Snapshot.Profiles).Name);
    }

    [Fact]
    public async Task SaveProfileWithoutCredentialStillDeletesStoredSecret()
    {
        using var temp = new TempDirectory();
        var profileStore = new JsonSessionStore(temp.File("sessions.json"));
        var credentials = new RejectingCredentialStore();
        var catalog = new ProtocolCatalog();
        catalog.Register(new FakeProtocolPlugin());
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, profileStore, credentials, new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);

        var original = SessionProfile.New("fake", "host.test", 22, "alice");
        profileStore.Upsert(original);
        credentials.Seed(original.Id, new StoredCredential { Password = "remembered" });

        workspace.SaveProfile(original with { Name = "Renamed" }, null);

        Assert.Equal(1, credentials.DeleteCalls);
        Assert.Equal(0, credentials.SaveCalls);
        Assert.Null(credentials.Get(original.Id));
    }

    /// <summary>Mimics <see cref="EncryptedCredentialStore"/>: Save of a DecryptionFailed value throws.</summary>
    private sealed class RejectingCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, StoredCredential> _items = new(StringComparer.Ordinal);

        public int SaveCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public void Seed(string profileId, StoredCredential credential) => _items[profileId] = credential;

        public StoredCredential? Get(string profileId) =>
            _items.TryGetValue(profileId, out StoredCredential? credential) ? credential : null;

        public void Save(string profileId, StoredCredential credential)
        {
            SaveCalls++;
            if (credential.DecryptionFailed)
            {
                throw new IOException("A saved credential could not be decrypted. Re-enter it or explicitly turn off Remember before saving.");
            }

            _items[profileId] = credential;
        }

        public void Delete(string profileId)
        {
            DeleteCalls++;
            _items.Remove(profileId);
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => true;
        public void Post(Action action) => action();
    }
}
