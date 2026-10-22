using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using Windows.Foundation;
using Windows.Storage;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>How the session editor was opened.</summary>
internal enum SessionEditorMode
{
    /// <summary>A brand new profile.</summary>
    Create,

    /// <summary>Editing an existing profile in place.</summary>
    Edit,

    /// <summary>Copy of an existing profile with a fresh identity.</summary>
    Duplicate,
}

/// <summary>Result of a successful edit: what to save and whether to dial right away.</summary>
internal sealed record SessionEditorOutcome(
    SessionProfile Profile, StoredCredential? Credential, bool ConnectAfterSave, ConnectOptions Options);

/// <summary>Props for <see cref="SessionEditorDialog"/>; a new instance per open.</summary>
internal sealed record SessionEditorProps(
    SessionEditorMode Mode,
    SessionProfile? Existing,
    Action<SessionEditorOutcome> OnSaved,
    Action OnCancel);

/// <summary>
/// Create, edit or duplicate a saved connection. Protocols and their default
/// ports come from <see cref="IProtocolCatalog"/>, so a new protocol needs no
/// changes here. Validation is shown in an inline <see cref="InfoBar"/> and the
/// two save buttons differ in whether the session is dialled afterwards.
/// </summary>
internal sealed class SessionEditorDialog : Component<SessionEditorProps>
{
    private const int MaxPort = 65535;

    public override Element Render()
    {
        var services = UseContext(AppServices.ReactorContext)
            ?? throw new InvalidOperationException("The component must be inside the shell service context.");
        var pickers = UseContext(WindowStoragePicker.Context)
            ?? throw new InvalidOperationException("The session editor requires its owning window's storage picker.");
        var props = Props;
        var template = props.Existing;
        var plugins = services.Protocols.Plugins;
        var settings = UseExternalStore(services.Settings.Subscribe, () => services.Settings.Current);
        var (viewport, setViewport) = UseState((Width: 640d, Height: 520d));

        var (name, setName) = UseState(template?.Name ?? string.Empty);
        var (group, setGroup) = UseState(template?.Group ?? string.Empty);
        var (host, setHost) = UseState(template?.Host ?? string.Empty);
        var (port, setPort) = UseState(template is { Port: > 0 }
            ? template.Port.ToString(CultureInfo.InvariantCulture)
            : string.Empty);
        var (userName, setUserName) = UseState(template?.Username ?? Environment.UserName);
        var (protocolIndex, setProtocolIndex) = UseState(IndexOfProtocol(plugins, template?.ProtocolId));
        var (authIndex, setAuthIndex) = UseState((int)(template?.Authentication ?? AuthenticationMethod.Password));
        var (keyPath, setKeyPath) = UseState(template?.PrivateKeyPath ?? string.Empty);
        var (password, setPassword) = UseState(string.Empty);
        var (passphrase, setPassphrase) = UseState(string.Empty);
        var (remember, setRemember) = UseState(HasStoredSecret(services, template));
        var (attempted, setAttempted) = UseState(false);
        var (pickerError, setPickerError) = UseState<string?>(null);

        // Read by the dialog buttons, which are wired on mount in the editor's
        // command space (the declarative DSL has no click handler for those).
        var validationRef = UseRef<string?>(null);
        var clickHandler = UseRef<TypedEventHandler<ContentDialog, ContentDialogButtonClickEventArgs>?>(null);
        var dialogControl = UseRef<ContentDialog?>(null);
        // The shell retains this component after close. Settings notifications can
        // render it again, so its declarative open state must also stay closed.
        var closed = UseRef(false);
        var sizingRoot = UseRef<XamlRoot?>(null);
        var sizingHandler = UseRef<TypedEventHandler<XamlRoot, XamlRootChangedEventArgs>?>(null);
        UseEffect(() => DetachDialogEvents, Array.Empty<object>());

        var protocol = plugins.Count > 0 ? plugins[Math.Clamp(protocolIndex, 0, plugins.Count - 1)].Descriptor : null;
        var isPassword = authIndex == (int)AuthenticationMethod.Password;
        var isKey = authIndex == (int)AuthenticationMethod.PublicKey;
        var authNames = new[]
        {
            Str.T("shell.editor.auth.password", "Password"),
            Str.T("shell.editor.auth.key", "Private key"),
            Str.T("shell.editor.auth.keyboard", "Keyboard interactive"),
        };

        var errors = Validate();
        var showErrors = errors.Count > 0 && (attempted || pickerError is not null);
        var canSave = errors.Count == 0;
        validationRef.Current = canSave ? null : string.Join(Environment.NewLine, errors);

        void Submit(bool connectAfterSave)
        {
            if (!canSave)
            {
                setAttempted(true);
                return;
            }

            var profile = BuildProfile();
            props.OnSaved(new SessionEditorOutcome(profile, BuildCredential(), connectAfterSave, new ConnectOptions
            {
                // A secret entered for this connection is independent of whether it is saved.
                Password = isPassword && password.Length > 0 ? password : null,
                PrivateKeyPath = profile.PrivateKeyPath,
                PrivateKeyPassphrase = isKey && passphrase.Length > 0
                    ? passphrase
                    : BuildCredential()?.PrivateKeyPassphrase,
            }));
        }

        var dialog = ContentDialog(
                TitleFor(props.Mode),
                ScrollView(VStack(AppTheme.SpacingXl, BuildFields()).Margin(0, 0, AppTheme.SpacingS, AppTheme.SpacingXs))
                    .Width(viewport.Width).MaxHeight(viewport.Height)
                    .HorizontalScrollMode(ScrollingScrollMode.Disabled),
                primaryButtonText: Str.T("shell.editor.saveAndConnect", "Save & connect")) with
        {
            IsOpen = !closed.Current,
            SecondaryButtonText = Str.T("shell.editor.save", "Save"),
            CloseButtonText = Str.T("common.cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result =>
            {
                if (closed.Current) return;
                closed.Current = true;
                DetachDialogEvents();
                SubmitOrCancel(result);
            },
        };

        // Keep the buttons enabled: an invalid form cancels the click and shows the
        // InfoBar instead of leaving the user with a dead button and no reason.
        return QueuedDialog.Wrap(dialog
            .Set(control =>
            {
                // Set runs against the live ContentDialog created by the overlay
                // lifecycle (OnMountAdd receives only the hidden anchor).
                dialogControl.Current = control;
                control.Resources["ContentDialogMaxWidth"] = 760d;
                control.RequestedTheme = ThemeController.ToElementTheme(settings.Theme);
                DialogAutomation.Tag("SessionEditor")(control);
            })
            .Opened(AttachDialogEvents));

        void AttachDialogEvents()
        {
            if (dialogControl.Current is not { } control || clickHandler.Current is not null)
            {
                return;
            }

            // The DSL has no click hook for the dialog's own command-space
            // buttons. Attach when the dialog opens; DetachDialogEvents runs in
            // OnClosed so the pair is balanced for every result path. The
            // handler reads validationRef.Current, so it validates the latest
            // form state rather than the state captured at mount.
            TypedEventHandler<ContentDialog, ContentDialogButtonClickEventArgs> handler =
                (_, args) => CancelIfInvalid(args);
            clickHandler.Current = handler;
            control.PrimaryButtonClick += handler;
            control.SecondaryButtonClick += handler;
            if (control.XamlRoot is { } root)
            {
                void Resize() => setViewport((
                    Math.Max(0, Math.Min(640, root.Size.Width - 96)),
                    Math.Max(0, Math.Min(600, root.Size.Height - 240))));
                sizingRoot.Current = root;
                sizingHandler.Current = (_, _) => Resize();
                root.Changed += sizingHandler.Current;
                Resize();
            }
            DialogAutomation.Tag("SessionEditor")(control);
        }

        void DetachDialogEvents()
        {
            if (dialogControl.Current is { } control && clickHandler.Current is { } handler)
            {
                control.PrimaryButtonClick -= handler;
                control.SecondaryButtonClick -= handler;
            }

            clickHandler.Current = null;
            if (sizingRoot.Current is { } root && sizingHandler.Current is { } resizeHandler)
            {
                root.Changed -= resizeHandler;
            }
            sizingRoot.Current = null;
            sizingHandler.Current = null;
            // ContentDialog instances are pooled; do not widen the next dialog.
            dialogControl.Current?.Resources.Remove("ContentDialogMaxWidth");
        }

        void CancelIfInvalid(ContentDialogButtonClickEventArgs args)
        {
            if (validationRef.Current is null)
            {
                return;
            }

            // Keep the dialog open so the validation InfoBar becomes visible.
            setAttempted(true);
            args.Cancel = true;
        }

        void SubmitOrCancel(ContentDialogResult result)
        {
            switch (result)
            {
                case ContentDialogResult.Primary:
                    Submit(true);
                    break;
                case ContentDialogResult.Secondary:
                    Submit(false);
                    break;
                default:
                    props.OnCancel();
                    break;
            }
        }

        Element[] BuildFields()
        {
            var nameLabel = Str.T("shell.editor.name", "Session name");
            var hostLabel = Str.T("shell.editor.host", "Host");
            var portLabel = Str.T("shell.editor.port", "Port");
            var userLabel = Str.T("shell.editor.userName", "User name");
            var groupLabel = Str.T("shell.editor.group", "Group (optional)");
            var authLabel = Str.T("shell.editor.auth", "Authentication");
            var passwordLabel = Str.T("shell.editor.password", "Password");
            var keyLabel = Str.T("shell.editor.keyFile", "Private key file");
            var passphraseLabel = Str.T("shell.editor.passphrase", "Key passphrase (optional)");

            var connection = SessionEditorLayout.Section(
                Str.T("shell.editor.section.connection", "Connection"),
                Str.T("shell.editor.section.connectionHint", "Choose a protocol and the server you want to connect to."),
                ComboBox(plugins.Select(p => p.Descriptor.DisplayName).ToArray(), Optional<int>.Of(protocolIndex), setProtocolIndex)
                    .Set(combo => combo.Header = Str.T("shell.editor.protocol", "Protocol"))
                    .HAlign(HorizontalAlignment.Stretch)
                    .AutomationName(Str.T("shell.editor.protocol", "Protocol"))
                    .AutomationId("SessionEditorProtocol"),
                SessionEditorLayout.Pair(
                    (settings.PrivacyMode
                        ? (Element)PasswordBox(Optional<string>.Of(host), setHost,
                                placeholderText: Str.T("shell.editor.hostPlaceholder", "host name or IP address"))
                            .Set(control => { control.Header = hostLabel; PrivacyDisplay.HideInput(control); })
                            .AutomationName(hostLabel).AutomationId("SessionEditorHost")
                        : TextBox(Optional<string>.Of(host), setHost, header: hostLabel,
                                placeholderText: Str.T("shell.editor.hostPlaceholder", "host name or IP address"))
                            .AutomationName(hostLabel).AutomationId("SessionEditorHost"))
                        .Grid(column: 0),
                    TextBox(Optional<string>.Of(port), setPort, header: portLabel,
                            placeholderText: (protocol?.DefaultPort ?? 22).ToString(CultureInfo.InvariantCulture))
                        .AutomationName(portLabel).AutomationId("SessionEditorPort").Grid(column: 1),
                    GridSize.Px(104)),
                Caption(protocol?.Description ?? Str.T("shell.editor.protocolHint",
                        "Pick a protocol to see what the connection offers."))
                    .Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap));

            var authentication = new List<Element>
            {
                TextBox(Optional<string>.Of(userName), setUserName, header: userLabel)
                    .AutomationName(userLabel).AutomationId("SessionEditorUserName"),
                ComboBox(authNames, Optional<int>.Of(authIndex), setAuthIndex)
                    .Set(combo => combo.Header = authLabel)
                    .HAlign(HorizontalAlignment.Stretch)
                    .AutomationName(authLabel).AutomationId("SessionEditorAuthentication"),
            };
            if (isPassword)
            {
                authentication.Add(SessionEditorLayout.Labeled(passwordLabel,
                    PasswordBox(Optional<string>.Of(password), setPassword, placeholderText: passwordLabel)
                        .AutomationName(passwordLabel).AutomationId("SessionEditorPassword")));
                var rememberLabel = Str.T("shell.editor.rememberPassword",
                    "Remember password (stored encrypted with Windows DPAPI)");
                authentication.Add(CheckBox(Optional<bool?>.Of(remember), setRemember, label: rememberLabel)
                    .AutomationName(rememberLabel));
            }
            if (isKey)
            {
                authentication.Add(SessionEditorLayout.Pair(
                    TextBox(Optional<string>.Of(keyPath), setKeyPath, header: keyLabel,
                            placeholderText: @"C:\Users\you\.ssh\id_ed25519")
                        .AutomationName(keyLabel).AutomationId("SessionEditorKeyPath").Grid(column: 0),
                    Button(Str.T("shell.editor.browse", "Browse…"), BrowseKey)
                        .AutomationName(Str.T("shell.editor.browseAutomation", "Browse for private key"))
                        .AutomationId("SessionEditorBrowseKey")
                        .VAlign(VerticalAlignment.Bottom).MinHeight(32).Grid(column: 1),
                    GridSize.Auto));
                authentication.Add(SessionEditorLayout.Labeled(passphraseLabel,
                    PasswordBox(Optional<string>.Of(passphrase), setPassphrase, placeholderText: passphraseLabel)
                        .AutomationName(passphraseLabel).AutomationId("SessionEditorPassphrase")));
                var rememberLabel = Str.T("shell.editor.rememberPassphrase",
                    "Remember passphrase (stored encrypted with Windows DPAPI)");
                authentication.Add(CheckBox(Optional<bool?>.Of(remember), setRemember, label: rememberLabel)
                    .AutomationName(rememberLabel));
            }
            if (authIndex == (int)AuthenticationMethod.KeyboardInteractive)
            {
                authentication.Add(InfoBar(
                    Str.T("shell.editor.keyboardTitle", "Keyboard interactive"),
                    Str.T("shell.editor.keyboardBody",
                        "The server prompts for one-time codes after the connection is opened.")).Informational());
            }

            var organization = SessionEditorLayout.Section(
                Str.T("shell.editor.section.organization", "Session details"),
                Str.T("shell.editor.section.organizationHint", "Add an optional name and group to keep your sessions organized."),
                settings.PrivacyMode && template is not null && PrivacyDisplay.Mask(template.Name) != template.Name
                    ? (Element)PasswordBox(Optional<string>.Of(name), setName,
                            placeholderText: Str.T("shell.editor.namePlaceholder", "Defaults to user@host"))
                        .Set(control => { control.Header = nameLabel; PrivacyDisplay.HideInput(control); })
                        .AutomationName(nameLabel).AutomationId("SessionEditorName")
                    : TextBox(Optional<string>.Of(name), setName, header: nameLabel,
                            placeholderText: Str.T("shell.editor.namePlaceholder", "Defaults to user@host"))
                        .AutomationName(nameLabel).AutomationId("SessionEditorName"),
                TextBox(Optional<string>.Of(group), setGroup, header: groupLabel,
                        placeholderText: Str.T("shell.editor.groupPlaceholder", "e.g. Production"))
                    .AutomationName(groupLabel).AutomationId("SessionEditorGroup"));
            var fields = new List<Element>();
            if (showErrors)
            {
                var message = pickerError ?? string.Join(Environment.NewLine, errors);
                fields.Add(InfoBar(Str.T("shell.editor.invalidTitle", "Check the connection details"), message).Warning());
            }
            fields.Add(connection);
            fields.Add(SessionEditorLayout.Section(
                Str.T("shell.editor.section.authentication", "Authentication"),
                Str.T("shell.editor.section.authenticationHint", "Choose how to sign in to this server."),
                authentication.ToArray()));
            fields.Add(organization);

            return fields.ToArray();
        }

        List<string> Validate()
        {
            var problems = new List<string>(3);
            if (string.IsNullOrWhiteSpace(host))
            {
                problems.Add(Str.T("shell.editor.hostRequired", "Host is required."));
            }

            if (string.IsNullOrWhiteSpace(userName))
            {
                problems.Add(Str.T("shell.editor.userRequired", "User name is required."));
            }

            if (port.Length > 0 && ParsePort(port) < 0)
            {
                problems.Add(Str.F(
                    "shell.editor.portInvalid",
                    "Port must be a number between 1 and {0}.",
                    MaxPort));
            }

            if (isKey && string.IsNullOrWhiteSpace(keyPath))
            {
                problems.Add(Str.T("shell.editor.keyRequired", "Pick the private key file to use."));
            }

            return problems;
        }

        Task<StorageFile?> PickKeyFileAsync() => pickers.PickFileAsync(
            [".pem", ".key", ".ppk", "*"],
            Str.T("shell.editor.useKey", "Use key"));

        async void BrowseKey()
        {
            try
            {
                var file = await PickKeyFileAsync();
                if (file is not null && !string.IsNullOrWhiteSpace(file.Path))
                {
                    setKeyPath(file.Path);
                    setPickerError(null);
                }
            }
            catch (Exception ex)
            {
                setPickerError(Str.F("shell.editor.pickerFailed", "Could not open the file picker: {0}", ex.Message));
            }
        }

        SessionProfile BuildProfile()
        {
            var parsedPort = ParsePort(port);
            var protocolId = protocol?.Id ?? template?.ProtocolId ?? "ssh";
            var displayName = string.IsNullOrWhiteSpace(name)
                ? $"{userName.Trim()}@{host.Trim()}"
                : name.Trim();
            var groupValue = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
            var auth = (AuthenticationMethod)authIndex;
            var keyFile = isKey && !string.IsNullOrWhiteSpace(keyPath) ? keyPath.Trim() : null;

            if (props.Mode == SessionEditorMode.Edit && template is not null)
            {
                return template with
                {
                    Name = displayName,
                    ProtocolId = protocolId,
                    Host = host.Trim(),
                    Port = parsedPort,
                    Username = userName.Trim(),
                    Authentication = auth,
                    PrivateKeyPath = keyFile,
                    Group = groupValue,
                };
            }

            // Create and Duplicate both get a fresh identity so credentials and
            // open sessions never collide with the source profile.
            return SessionProfile.New(protocolId, host.Trim(), parsedPort, userName.Trim(), displayName) with
            {
                Authentication = auth,
                PrivateKeyPath = keyFile,
                Group = groupValue,
            };
        }

        StoredCredential? BuildCredential()
        {
            if (!remember)
            {
                return null;
            }

            // Editing without retyping a secret keeps the stored one instead of
            // silently forgetting it.
            var existing = props.Mode == SessionEditorMode.Edit && template is not null
                ? services.Credentials.Get(template.Id)
                : null;

            if (isPassword)
            {
                return !string.IsNullOrEmpty(password)
                    ? new StoredCredential { Password = password }
                    : existing is { Password: not null } ? existing : null;
            }

            if (isKey)
            {
                return !string.IsNullOrEmpty(passphrase)
                    ? new StoredCredential { PrivateKeyPassphrase = passphrase }
                    : existing is { PrivateKeyPassphrase: not null } ? existing : null;
            }

            return null;
        }
    }

    private static string TitleFor(SessionEditorMode mode) => mode switch
    {
        SessionEditorMode.Edit => Str.T("shell.editor.editTitle", "Edit session"),
        SessionEditorMode.Duplicate => Str.T("shell.editor.duplicateTitle", "Duplicate session"),
        _ => Str.T("shell.editor.createTitle", "New session"),
    };

    private static int IndexOfProtocol(IReadOnlyList<IProtocolPlugin> plugins, string? protocolId)
    {
        for (var index = 0; index < plugins.Count; index++)
        {
            if (string.Equals(plugins[index].Descriptor.Id, protocolId ?? "ssh", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static bool HasStoredSecret(AppServices services, SessionProfile? profile) =>
        profile is not null && services.Credentials.Get(profile.Id) is not null;

    /// <summary>Returns 0 for "use the protocol default" and -1 for an invalid value.</summary>
    private static int ParsePort(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
               value is >= 1 and <= MaxPort
            ? value
            : -1;
    }

}
