using Microsoft.Extensions.Logging;
using SurgeLink.App.Localization;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Protocols.Ssh;

namespace SurgeLink.App.Services;

/// <summary>
/// Composition root. Everything the app needs is created here once, on the UI
/// thread, and exposed as read-only properties. Adding a protocol is one
/// <c>Protocols.Register(...)</c> line in <see cref="Create"/>.
/// </summary>
public sealed class AppServices
{
    private AppServices(
        ILoggerFactory loggerFactory,
        IUiDispatcher dispatcher,
        IProtocolCatalog protocols,
        ISessionStore profileStore,
        ICredentialStore credentials,
        IHostKeyStore hostKeys,
        ISessionManager sessions,
        InteractionService interaction,
        IAppSettingsStore settings,
        LocalizationService localization,
        WorkspaceStore workspace)
    {
        Logs = loggerFactory;
        Dispatcher = dispatcher;
        Protocols = protocols;
        ProfileStore = profileStore;
        Credentials = credentials;
        HostKeys = hostKeys;
        Sessions = sessions;
        Interaction = interaction;
        Settings = settings;
        Localization = localization;
        Workspace = workspace;
    }

    /// <summary>The live services instance; available after the shell mounts.</summary>
    public static AppServices? Current { get; private set; }

    public ILoggerFactory Logs { get; }

    public IUiDispatcher Dispatcher { get; }

    public IProtocolCatalog Protocols { get; }

    public ISessionStore ProfileStore { get; }

    public ICredentialStore Credentials { get; }

    public IHostKeyStore HostKeys { get; }

    public ISessionManager Sessions { get; }

    public InteractionService Interaction { get; }

    /// <summary>User preferences (theme, fonts, intervals, language).</summary>
    public IAppSettingsStore Settings { get; }

    /// <summary>UI language and translated text resolution.</summary>
    public LocalizationService Localization { get; }

    public WorkspaceStore Workspace { get; }

    /// <summary>Creates the graph once; subsequent calls return the existing instance.</summary>
    public static AppServices EnsureInitialized()
    {
        if (Current is not null)
        {
            return Current;
        }

        Current = Create(DispatcherUiDispatcher.CreateForCurrentThread());
        return Current;
    }

    /// <summary>Builds the service graph; exposed for tests and tooling.</summary>
    public static AppServices Create(IUiDispatcher dispatcher)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(AppPaths.LogsDirectory));
        });

        // Protocol registry: the single place a new protocol is wired in.
        var protocols = new ProtocolCatalog();
        protocols.Register(new SshProtocolPlugin());
        protocols.Register(new SurgeLink.Protocols.Demo.DemoProtocolPlugin());

        var profileStore = new JsonSessionStore(AppPaths.SessionsFile);
        var credentials = new EncryptedCredentialStore(AppPaths.CredentialsFile, new DpapiSecretProtector());
        var hostKeys = new JsonHostKeyStore(AppPaths.HostKeysFile);

        InteractionService? interaction = null;
        var hostContext = new ProtocolHostContext(
            loggerFactory,
            hostKeys,
            hostKeyPrompt: (challenge, ct) => interaction!.RequestHostKeyTrustAsync(challenge, ct),
            interactivePrompt: (prompt, ct) => interaction!.RequestSecretAsync(
                new SecretRequest("Authentication", prompt.Question, prompt.IsSecret), ct));

        interaction = new InteractionService(dispatcher);
        var settings = new AppSettingsStore(Path.Combine(AppPaths.Root, "settings.json"));
        var localization = new LocalizationService(settings);

        var sessions = new SessionManager(
            protocols,
            credentials,
            hostContext,
            loggerFactory.CreateLogger<SessionManager>());

        var workspace = new WorkspaceStore(
            sessions,
            profileStore,
            credentials,
            dispatcher,
            loggerFactory.CreateLogger<WorkspaceStore>());

        return new AppServices(
            loggerFactory,
            dispatcher,
            protocols,
            profileStore,
            credentials,
            hostKeys,
            sessions,
            interaction,
            settings,
            localization,
            workspace);
    }
}