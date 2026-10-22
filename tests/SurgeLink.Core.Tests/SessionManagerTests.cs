using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class SessionManagerTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(int defaultPort = 2222)
        {
            Plugin = new FakeProtocolPlugin("fake", defaultPort);
            Catalog = new ProtocolCatalog();
            Catalog.Register(Plugin);
            Credentials = new FakeCredentialStore();
            HostKeys = new FakeHostKeyStore();
            HostContext = new ProtocolHostContext(NullLoggerFactory.Instance, HostKeys);
            Manager = new SessionManager(Catalog, Credentials, HostContext, NullLogger<SessionManager>.Instance);
            Manager.SessionOpened += (_, args) =>
            {
                OpenedCount++;
                LastOpened = args;
            };
            Manager.SessionClosed += (_, args) =>
            {
                ClosedCount++;
                LastClosedSessionId = args.SessionId;
            };
        }

        public FakeProtocolPlugin Plugin { get; }

        public ProtocolCatalog Catalog { get; }

        public FakeCredentialStore Credentials { get; }

        public FakeHostKeyStore HostKeys { get; }

        public ProtocolHostContext HostContext { get; }

        public SessionManager Manager { get; }

        public int OpenedCount { get; private set; }

        public int ClosedCount { get; private set; }

        public SessionOpenedEventArgs? LastOpened { get; private set; }

        public string? LastClosedSessionId { get; private set; }

        public async ValueTask DisposeAsync() => await Manager.DisposeAsync();
    }

    private static SessionProfile Profile(
        int port = 0,
        AuthenticationMethod authentication = AuthenticationMethod.Password,
        string? privateKeyPath = null,
        IReadOnlyDictionary<string, string>? options = null) => new()
    {
        Id = "profile-1",
        Name = "Example session",
        ProtocolId = "fake",
        Host = "example.test",
        Port = port,
        Username = "alice",
        Authentication = authentication,
        PrivateKeyPath = privateKeyPath,
        Options = options ?? new Dictionary<string, string> { ["encoding"] = "utf-8" },
    };

    private static FakeProtocolSession Fake(IProtocolSession session) => Assert.IsType<FakeProtocolSession>(session);

    [Fact]
    public async Task OpenBuildsRequestFromProfile()
    {
        await using var harness = new Harness(2222);
        var options = new Dictionary<string, string> { ["term"] = "xterm-256color", ["encoding"] = "utf-8" };
        SessionProfile profile = Profile(options: options);

        IProtocolSession session = await harness.Manager.OpenAsync(profile, null, CancellationToken.None);

        ConnectionRequest request = harness.Plugin.LastRequest!;
        Assert.Equal("example.test", request.Host);
        Assert.Equal(2222, request.Port);
        Assert.Equal("alice", request.Username);
        Assert.Equal(AuthenticationMethod.Password, request.Authentication);
        Assert.Null(request.Password);
        Assert.Null(request.PrivateKeyPath);
        Assert.Equal(profile.Id, request.ProfileId);
        Assert.Equal(profile.Name, request.DisplayName);
        Assert.Equal("xterm-256color", request.Options["term"]);
        Assert.Equal("utf-8", request.Options["encoding"]);
        Assert.Same(harness.HostContext, harness.Plugin.LastContext);
        Assert.Equal(harness.Plugin.Descriptor, session.Protocol);
    }

    [Fact]
    public async Task ExplicitProfilePortWins()
    {
        await using var harness = new Harness(2222);

        await harness.Manager.OpenAsync(Profile(port: 2200), null, CancellationToken.None);

        Assert.Equal(2200, harness.Plugin.LastRequest!.Port);
    }

    [Fact]
    public async Task PasswordIsResolvedFromCredentialStore()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile();
        harness.Credentials.Save(profile.Id, new StoredCredential { Password = "stored-password" });

        await harness.Manager.OpenAsync(profile, null, CancellationToken.None);

        Assert.Equal("stored-password", harness.Plugin.LastRequest!.Password);
        Assert.True(harness.Credentials.GetCalls > 0);
    }

    [Fact]
    public async Task PassphraseIsResolvedFromCredentialStoreForPublicKey()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile(
            authentication: AuthenticationMethod.PublicKey,
            privateKeyPath: "/home/alice/.ssh/id_ed25519");
        harness.Credentials.Save(profile.Id, new StoredCredential { PrivateKeyPassphrase = "key-passphrase" });

        await harness.Manager.OpenAsync(profile, null, CancellationToken.None);

        ConnectionRequest request = harness.Plugin.LastRequest!;
        Assert.Equal("key-passphrase", request.PrivateKeyPassphrase);
        Assert.Equal("/home/alice/.ssh/id_ed25519", request.PrivateKeyPath);
        Assert.Null(request.Password);
    }

    [Fact]
    public async Task ConnectOptionsPasswordOverridesCredentialStore()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile();
        harness.Credentials.Save(profile.Id, new StoredCredential { Password = "stored-password" });

        await harness.Manager.OpenAsync(profile, new ConnectOptions { Password = "typed-password" }, CancellationToken.None);

        Assert.Equal("typed-password", harness.Plugin.LastRequest!.Password);
    }

    [Fact]
    public async Task ConnectOptionsPassphraseAndKeyPathOverrideProfileAndStore()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile(
            authentication: AuthenticationMethod.PublicKey,
            privateKeyPath: "/profile/key");
        harness.Credentials.Save(profile.Id, new StoredCredential { PrivateKeyPassphrase = "stored-passphrase" });
        var overrides = new ConnectOptions
        {
            PrivateKeyPath = "/options/key",
            PrivateKeyPassphrase = "typed-passphrase",
        };

        await harness.Manager.OpenAsync(profile, overrides, CancellationToken.None);

        ConnectionRequest request = harness.Plugin.LastRequest!;
        Assert.Equal("/options/key", request.PrivateKeyPath);
        Assert.Equal("typed-passphrase", request.PrivateKeyPassphrase);
    }

    [Fact]
    public async Task ConnectOptionsKeyPathFillsMissingProfileKeyPath()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile(authentication: AuthenticationMethod.PublicKey);

        await harness.Manager.OpenAsync(
            profile,
            new ConnectOptions { PrivateKeyPath = "/options/key" },
            CancellationToken.None);

        Assert.Equal("/options/key", harness.Plugin.LastRequest!.PrivateKeyPath);
    }

    [Fact]
    public async Task CredentialStoreIsNotQueriedWhenOptionsSupplyBothSecrets()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile();
        harness.Credentials.Save(profile.Id, new StoredCredential { Password = "stored-password" });

        await harness.Manager.OpenAsync(
            profile,
            new ConnectOptions { Password = "typed", PrivateKeyPassphrase = "typed-phrase" },
            CancellationToken.None);

        Assert.Equal(0, harness.Credentials.GetCalls);
        Assert.Equal("typed", harness.Plugin.LastRequest!.Password);
    }

    [Fact]
    public async Task PublicKeyProfileWithoutKeyPathThrowsConfiguration()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile(authentication: AuthenticationMethod.PublicKey);

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            () => harness.Manager.OpenAsync(profile, null, CancellationToken.None));

        Assert.Equal(ProtocolFailureKind.Configuration, exception.Kind);
        Assert.Equal(0, harness.Plugin.ConnectCalls);
        Assert.Empty(harness.Manager.Sessions);
        Assert.Equal(0, harness.OpenedCount);
    }

    [Fact]
    public async Task PasswordAuthDoesNotUseStoredPassphrase()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile();
        harness.Credentials.Save(profile.Id, new StoredCredential { PrivateKeyPassphrase = "irrelevant" });

        await harness.Manager.OpenAsync(profile, null, CancellationToken.None);

        ConnectionRequest request = harness.Plugin.LastRequest!;
        Assert.Null(request.Password);
        Assert.Null(request.PrivateKeyPassphrase);
    }

    [Fact]
    public async Task SessionOpenedFiresWithProfileAndSession()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile();

        IProtocolSession session = await harness.Manager.OpenAsync(profile, null, CancellationToken.None);

        Assert.Equal(1, harness.OpenedCount);
        Assert.Same(profile, harness.LastOpened!.Profile);
        Assert.Same(session, harness.LastOpened.Session);
        Assert.Same(session, Assert.Single(harness.Manager.Sessions));
    }

    [Fact]
    public async Task SessionsReturnsSnapshot()
    {
        await using var harness = new Harness();
        IProtocolSession session = await harness.Manager.OpenAsync(Profile(), null, CancellationToken.None);
        IReadOnlyList<IProtocolSession> snapshot = harness.Manager.Sessions;

        await harness.Manager.CloseAsync(session.SessionId);

        Assert.Single(snapshot);
        Assert.Empty(harness.Manager.Sessions);
    }

    [Fact]
    public async Task CloseDisposesSessionAndRaisesSessionClosed()
    {
        await using var harness = new Harness();
        FakeProtocolSession session = Fake(
            await harness.Manager.OpenAsync(Profile(), null, CancellationToken.None));

        await harness.Manager.CloseAsync(session.SessionId);

        Assert.Equal(1, session.DisposeCount);
        Assert.Empty(harness.Manager.Sessions);
        Assert.Equal(1, harness.ClosedCount);
        Assert.Equal(session.SessionId, harness.LastClosedSessionId);
    }

    [Fact]
    public async Task CloseTwiceIsANoOp()
    {
        await using var harness = new Harness();
        FakeProtocolSession session = Fake(
            await harness.Manager.OpenAsync(Profile(), null, CancellationToken.None));

        await harness.Manager.CloseAsync(session.SessionId);
        await harness.Manager.CloseAsync(session.SessionId);

        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, harness.ClosedCount);
    }

    [Fact]
    public async Task CloseUnknownSessionIdDoesNothing()
    {
        await using var harness = new Harness();

        await harness.Manager.CloseAsync("unknown-session");

        Assert.Equal(0, harness.ClosedCount);
        Assert.Empty(harness.Manager.Sessions);
    }

    [Fact]
    public async Task CloseRaisesSessionClosedEvenWhenDisposeThrows()
    {
        await using var harness = new Harness();
        var session = new FakeProtocolSession(harness.Plugin.Descriptor)
        {
            DisposeException = new InvalidOperationException("dispose failed"),
        };
        harness.Plugin.SessionFactory = _ => session;
        await harness.Manager.OpenAsync(Profile(), null, CancellationToken.None);

        await harness.Manager.CloseAsync(session.SessionId);

        Assert.Equal(1, session.DisposeCount);
        Assert.Empty(harness.Manager.Sessions);
        Assert.Equal(1, harness.ClosedCount);
    }

    [Fact]
    public async Task ProtocolFailurePropagatesAndLeavesNoSession()
    {
        await using var harness = new Harness();
        var failure = new ProtocolException("authentication failed", ProtocolFailureKind.Authentication);
        harness.Plugin.Failure = failure;

        ProtocolException thrown = await Assert.ThrowsAsync<ProtocolException>(
            () => harness.Manager.OpenAsync(Profile(), null, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Empty(harness.Manager.Sessions);
        Assert.Equal(0, harness.OpenedCount);
        Assert.Equal(0, harness.ClosedCount);
        Assert.Equal(1, harness.Plugin.ConnectCalls);
    }

    [Fact]
    public async Task CanceledConnectionPropagates()
    {
        await using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Manager.OpenAsync(Profile(), null, cancellation.Token));

        Assert.Empty(harness.Manager.Sessions);
        Assert.Equal(0, harness.OpenedCount);
    }

    [Fact]
    public async Task DisposeDisposesEveryLiveSession()
    {
        await using var harness = new Harness();
        FakeProtocolSession first = Fake(
            await harness.Manager.OpenAsync(Profile(), null, CancellationToken.None));
        FakeProtocolSession second = Fake(
            await harness.Manager.OpenAsync(Profile(), null, CancellationToken.None));

        await harness.Manager.DisposeAsync();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Empty(harness.Manager.Sessions);
    }

    [Fact]
    public async Task DisposeSwallowsFailureToDisposeOneSession()
    {
        await using var harness = new Harness();
        var session = new FakeProtocolSession(harness.Plugin.Descriptor)
        {
            DisposeException = new InvalidOperationException("dispose failed"),
        };
        harness.Plugin.SessionFactory = _ => session;
        await harness.Manager.OpenAsync(Profile(), null, CancellationToken.None);

        await harness.Manager.DisposeAsync();

        Assert.Equal(1, session.DisposeCount);
        Assert.Empty(harness.Manager.Sessions);
    }

    [Fact]
    public async Task ConnectionCompletingAfterDisposeIsRejectedAndReleased()
    {
        await using var harness = new Harness();
        var ready = new TaskCompletionSource<IProtocolSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeProtocolSession(harness.Plugin.Descriptor);
        harness.Plugin.AsyncSessionFactory = (_, _) => ready.Task;
        var opening = harness.Manager.OpenAsync(Profile(), null, CancellationToken.None);
        await harness.Manager.DisposeAsync();
        ready.SetResult(session);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opening);
        Assert.Empty(harness.Manager.Sessions);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, harness.OpenedCount);
    }

    [Fact]
    public async Task FailingOpenedSubscriberDoesNotLeakConnection()
    {
        await using var harness = new Harness();
        var session = new FakeProtocolSession(harness.Plugin.Descriptor);
        harness.Plugin.SessionFactory = _ => session;
        harness.Manager.SessionOpened += (_, _) => throw new IOException("subscriber failed");
        await Assert.ThrowsAsync<IOException>(() => harness.Manager.OpenAsync(Profile(), null, CancellationToken.None));
        Assert.Empty(harness.Manager.Sessions);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task OpenAfterDisposeThrowsObjectDisposedException()
    {
        await using var harness = new Harness();

        await harness.Manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => harness.Manager.OpenAsync(Profile(), null, CancellationToken.None));
    }

    [Fact]
    public async Task OpenWithNullProfileThrows()
    {
        await using var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Manager.OpenAsync(null!, null, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownProtocolThrowsConfiguration()
    {
        await using var harness = new Harness();
        SessionProfile profile = Profile() with { ProtocolId = "missing" };

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            () => harness.Manager.OpenAsync(profile, null, CancellationToken.None));

        Assert.Equal(ProtocolFailureKind.Configuration, exception.Kind);
        Assert.Equal(0, harness.Plugin.ConnectCalls);
    }

    [Fact]
    public async Task SessionStateChangesAreObservedWithoutThrowing()
    {
        await using var harness = new Harness();
        FakeProtocolSession session = Fake(
            await harness.Manager.OpenAsync(Profile(), null, CancellationToken.None));

        Exception? observed = Record.Exception(
            () => session.RaiseStateChanged(ConnectionState.Connected, ConnectionState.Disconnected, "bye"));

        Assert.Null(observed);
    }
}
