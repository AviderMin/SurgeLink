using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor.Core;
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
        Agent = new AgentWorkspaceService(workspace, dispatcher, loggerFactory.CreateLogger<AgentWorkspaceService>());
    }

    /// <summary>The live services instance; created by the startup bootstrap before the shell mounts.</summary>
    public static AppServices? Current { get; private set; }

    /// <summary>Carries the prepared composition root to the shell's component tree.</summary>
    public static Context<AppServices?> ReactorContext { get; } = new(null);

    /// <summary>Returns the prepared graph or explains that startup failed.</summary>
    public static AppServices RequireInitialized =>
        Current ?? throw new InvalidOperationException("AppServices is not initialized; the startup bootstrap must run before the UI renders.");

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

    public AgentWorkspaceService Agent { get; }

    private static readonly object InitializationGate = new();

    /// <summary>
    /// Creates the graph on the UI thread during application startup. Components
    /// only read <see cref="Current"/> (or the Reactor context); Render never
    /// triggers composition, settings I/O, or global state changes.
    /// </summary>
    public static AppServices Initialize()
    {
        lock (InitializationGate)
        {
            return Current ??= Create(DispatcherUiDispatcher.CreateForCurrentThread());
        }
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
        protocols.Register(new SurgeLink.Protocols.Telnet.TelnetProtocolPlugin());
        protocols.Register(new SurgeLink.Protocols.Serial.SerialProtocolPlugin());
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
