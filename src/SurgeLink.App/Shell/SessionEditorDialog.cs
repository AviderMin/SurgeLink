using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
internal sealed record SessionEditorOutcome(SessionProfile Profile, StoredCredential? Credential, bool ConnectAfterSave);

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

    private static readonly string[] AuthNames = { "Password", "Private key", "Keyboard interactive" };

    public override Element Render()
    {
        var services = AppServices.EnsureInitialized();
        var props = Props;
        var template = props.Existing;
        var plugins = services.Protocols.Plugins;

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

        var protocol = plugins.Count > 0 ? plugins[Math.Clamp(protocolIndex, 0, plugins.Count - 1)].Descriptor : null;
        var isPassword = authIndex == (int)AuthenticationMethod.Password;
        var isKey = authIndex == (int)AuthenticationMethod.PublicKey;

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

            props.OnSaved(new SessionEditorOutcome(BuildProfile(), BuildCredential(), connectAfterSave));
        }

        var dialog = ContentDialog(
                TitleFor(props.Mode),
                ScrollView(VStack(AppTheme.SpacingM, BuildFields()).MaxWidth(560)).MaxHeight(520),
                primaryButtonText: "Save & connect") with
        {
            IsOpen = true,
            SecondaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result => SubmitOrCancel(result),
        };

        // Keep the buttons enabled: an invalid form cancels the click and shows the
        // InfoBar instead of leaving the user with a dead button and no reason.
        return dialog.Set(dialogControl =>
        {
            DialogAutomation.Tag("SessionEditor")(dialogControl);
            if (clickHandler.Current is not null)
            {
                return;
            }

            // The DSL has no click hook for the dialog's own command-space buttons,
            // so they are wired once here and the handler is kept for removal.
#pragma warning disable REACTOR_EVENT_001
            TypedEventHandler<ContentDialog, ContentDialogButtonClickEventArgs> handler = (_, args) => CancelIfInvalid(args);
            clickHandler.Current = handler;
            dialogControl.PrimaryButtonClick += handler;
            dialogControl.SecondaryButtonClick += handler;
#pragma warning restore REACTOR_EVENT_001
        });

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
            var fields = new List<Element>
            {
                TextBox(Optional<string>.Of(name), setName, header: "Session name", placeholderText: "Defaults to user@host")
                    .AutomationName("Session name"),
                ComboBox(plugins.Select(p => p.Descriptor.DisplayName).ToArray(), Optional<int>.Of(protocolIndex), setProtocolIndex)
                    .Set(combo => combo.Header = "Protocol")
                    .AutomationName("Protocol"),
                FieldGrid(
                    TextBox(Optional<string>.Of(host), setHost, header: "Host", placeholderText: "host name or IP address")
                        .AutomationName("Host")
                        .Grid(column: 0),
                    TextBox(Optional<string>.Of(port), setPort, header: "Port",
                            placeholderText: (protocol?.DefaultPort ?? 22).ToString(CultureInfo.InvariantCulture))
                        .AutomationName("Port")
                        .Grid(column: 1)),
                FieldGrid(
                    TextBox(Optional<string>.Of(userName), setUserName, header: "User name")
                        .AutomationName("User name")
                        .Grid(column: 0),
                    TextBox(Optional<string>.Of(group), setGroup, header: "Group (optional)", placeholderText: "e.g. Production")
                        .AutomationName("Group")
                        .Grid(column: 1)),
                ComboBox(AuthNames, Optional<int>.Of(authIndex), setAuthIndex)
                    .Set(combo => combo.Header = "Authentication")
                    .AutomationName("Authentication"),
            };

            if (isPassword)
            {
                fields.Add(PasswordBox(Optional<string>.Of(password), setPassword, placeholderText: "Password")
                    .AutomationName("Password"));
                fields.Add(CheckBox(Optional<bool?>.Of(remember), setRemember,
                        label: "Remember password (stored encrypted with Windows DPAPI)")
                    .AutomationName("Remember password"));
            }

            if (isKey)
            {
                fields.Add(FieldGrid(
                    TextBox(Optional<string>.Of(keyPath), setKeyPath, header: "Private key file",
                            placeholderText: @"C:\Users\you\.ssh\id_ed25519")
                        .AutomationName("Private key file")
                        .Grid(column: 0),
                    Button("Browse\u2026", BrowseKey)
                        .AutomationName("Browse for private key")
                        .VAlign(VerticalAlignment.Bottom)
                        .Grid(column: 1)));
                fields.Add(PasswordBox(Optional<string>.Of(passphrase), setPassphrase, placeholderText: "Key passphrase (optional)")
                    .AutomationName("Key passphrase"));
                fields.Add(CheckBox(Optional<bool?>.Of(remember), setRemember,
                        label: "Remember passphrase (stored encrypted with Windows DPAPI)")
                    .AutomationName("Remember passphrase"));
            }

            if (authIndex == (int)AuthenticationMethod.KeyboardInteractive)
            {
                fields.Add(InfoBar(
                    "Keyboard interactive",
                    "The server prompts for one-time codes after the connection is opened.").Informational());
            }

            fields.Add(Caption(protocol?.Description ?? "Pick a protocol to see what the connection offers.")
                .Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.Wrap));

            if (showErrors)
            {
                var message = pickerError ?? string.Join(Environment.NewLine, errors);
                fields.Insert(0, InfoBar("Check the connection details", message).Warning());
            }

            return fields.ToArray();
        }

        List<string> Validate()
        {
            var problems = new List<string>(3);
            if (string.IsNullOrWhiteSpace(host))
            {
                problems.Add("Host is required.");
            }

            if (string.IsNullOrWhiteSpace(userName))
            {
                problems.Add("User name is required.");
            }

            if (port.Length > 0 && ParsePort(port) < 0)
            {
                problems.Add($"Port must be a number between 1 and {MaxPort}.");
            }

            if (isKey && string.IsNullOrWhiteSpace(keyPath))
            {
                problems.Add("Pick the private key file to use.");
            }

            return problems;
        }

        Task<StorageFile?> PickKeyFileAsync() => this.UseFilePickerAsync(new FilePickerOptions(
            FileTypeFilter: new[] { ".pem", ".key", ".ppk", "*" },
            CommitButtonText: "Use key"));

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
                setPickerError("Could not open the file picker: " + ex.Message);
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
        SessionEditorMode.Edit => "Edit session",
        SessionEditorMode.Duplicate => "Duplicate session",
        _ => "New session",
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

    private static Element FieldGrid(Element first, Element second) =>
        Grid(
            columns: [GridSize.Star(), GridSize.Star()],
            rows: [GridSize.Auto],
            first,
            second) with
        {
            ColumnSpacing = AppTheme.SpacingM,
        };
}
