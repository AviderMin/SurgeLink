using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.Core.Protocols;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>
/// Renders the head of the <see cref="InteractionService"/> queue as a modal
/// dialog and completes it with the user's answer. Mounted once by the shell, so
/// every protocol prompt (host keys, passwords, confirmations) works without the
/// protocol knowing anything about UI.
/// </summary>
/// <remarks>
/// One question is shown at a time: the queue head is the only thing rendered.
/// Each dialog is keyed by <see cref="PendingInteraction.Id"/> so the next
/// question is built as a fresh <see cref="ContentDialog"/> instead of reusing
/// the closed one.
/// </remarks>
internal sealed class DialogHost : Component
{
    public override Element Render()
    {
        var interaction = AppServices.EnsureInitialized().Interaction;
        var pending = UseExternalStore(interaction.Subscribe, () => interaction.Current);

        // Hooks run unconditionally; this single buffer backs the text and secret
        // variants and is re-seeded whenever the head of the queue changes.
        var (answer, setAnswer) = UseState(string.Empty);
        var lastDialog = UseRef<Element?>(null);
        UseEffect(() => setAnswer(SeedAnswer(pending)), pending!);

        if (pending is null)
        {
            // Keep the last dialog element mounted after the queue drains. WinUI
            // has already closed it, and telling Reactor to unmount (or to hide)
            // a closed ContentDialog fails fast on WinUI 3, so re-rendering the
            // very same element is a no-op until the next question arrives.
            return lastDialog.Current ?? Empty();
        }

        var dialog = pending.Kind switch
        {
            InteractionKind.HostKey => BuildHostKey((HostKeyChallenge)pending.Request, pending, interaction),
            InteractionKind.Secret => BuildSecret((SecretRequest)pending.Request, answer, setAnswer, pending, interaction),
            InteractionKind.Confirmation => BuildConfirmation((ConfirmationRequest)pending.Request, pending, interaction),
            InteractionKind.Text => BuildText((TextRequest)pending.Request, answer, setAnswer, pending, interaction),
            InteractionKind.Error => BuildMessage((MessageRequest)pending.Request, true, pending, interaction),
            _ => BuildMessage((MessageRequest)pending.Request, false, pending, interaction),
        };

        lastDialog.Current = dialog.WithKey(pending.Id.ToString("n"));
        return lastDialog.Current;
    }

    private static string SeedAnswer(PendingInteraction? pending) => pending?.Request switch
    {
        TextRequest text => text.InitialValue ?? string.Empty,
        _ => string.Empty,
    };

    private static ContentDialogElement BuildHostKey(
        HostKeyChallenge challenge,
        PendingInteraction pending,
        InteractionService interaction)
    {
        var rows = new List<Element>
        {
            FactRow("Endpoint", challenge.Endpoint, monospace: false),
            FactRow("Key algorithm", challenge.Algorithm, monospace: false),
            FactRow("SHA256 fingerprint", challenge.FingerprintSha256, monospace: true),
        };

        rows.Add(challenge.IsChanged
            ? InfoBar("Host key changed", $"The key presented by {challenge.Endpoint} does not match the fingerprint trusted before. " +
                                          "This can mean the server was reinstalled \u2014 or that the connection is being intercepted. " +
                                          "Only trust it when you can explain the change.")
                .Error()
            : InfoBar("Unknown host", "This is the first connection to this host. Compare the fingerprint with the server administrator before trusting it.")
                .Informational());

        var dialog = ContentDialog("Verify host key", VStack(AppTheme.SpacingM, rows.ToArray()), primaryButtonText: "Trust once") with
        {
            IsOpen = true,
            SecondaryButtonText = "Trust always",
            CloseButtonText = "Reject",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result => Finish(interaction, pending, result switch
            {
                ContentDialogResult.Primary => HostKeyDecision.TrustOnce,
                ContentDialogResult.Secondary => HostKeyDecision.TrustAlways,
                _ => HostKeyDecision.Reject,
            }),
        };

        return dialog.Set(DialogAutomation.Tag("HostKey"));
    }

    private static ContentDialogElement BuildSecret(
        SecretRequest request,
        string answer,
        Action<string> setAnswer,
        PendingInteraction pending,
        InteractionService interaction)
    {
        Element input = request.IsPassword
            ? PasswordBox(Optional<string>.Of(answer), setAnswer, placeholderText: "Password")
                .AutomationName("Password")
            : TextBox(Optional<string>.Of(answer), setAnswer, placeholderText: "Value")
                .AutomationName(request.Title);

        var content = VStack(
            AppTheme.SpacingM,
            TextBlock(request.Description).TextWrapping(TextWrapping.Wrap).Foreground(Theme.SecondaryText),
            input);

        var dialog = ContentDialog(request.Title, content, primaryButtonText: "Continue") with
        {
            IsOpen = true,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result => Finish(
                interaction,
                pending,
                result == ContentDialogResult.Primary ? answer : null),
        };

        return dialog.Set(DialogAutomation.Tag("Secret"));
    }

    private static ContentDialogElement BuildConfirmation(
        ConfirmationRequest request,
        PendingInteraction pending,
        InteractionService interaction)
    {
        var dialog = ContentDialog(
                request.Title,
                TextBlock(request.Message).TextWrapping(TextWrapping.Wrap),
                primaryButtonText: request.PrimaryText) with
        {
            IsOpen = true,
            CloseButtonText = request.CloseText,
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result => Finish(interaction, pending, result == ContentDialogResult.Primary),
        };

        return dialog.Set(DialogAutomation.Tag("Confirmation"));
    }

    private static ContentDialogElement BuildText(
        TextRequest request,
        string answer,
        Action<string> setAnswer,
        PendingInteraction pending,
        InteractionService interaction)
    {
        var content = VStack(
            AppTheme.SpacingM,
            TextBox(Optional<string>.Of(answer), setAnswer, header: request.Label)
                .AutomationName(request.Label),
            Caption(request.Title).Foreground(Theme.SecondaryText));

        var dialog = ContentDialog(request.Title, content, primaryButtonText: "OK") with
        {
            IsOpen = true,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result => Finish(
                interaction,
                pending,
                result == ContentDialogResult.Primary ? answer : null),
        };

        return dialog.Set(DialogAutomation.Tag("TextInput"));
    }

    private static ContentDialogElement BuildMessage(
        MessageRequest request,
        bool isError,
        PendingInteraction pending,
        InteractionService interaction)
    {
        var bar = InfoBar(message: request.Message).IsClosable(false);
        var content = isError ? bar.Error() : bar.Informational();

        // An explicit positive button keeps the dialog's command space non-empty;
        // an empty primary label made WinUI tear down the dialog incorrectly.
        var dialog = ContentDialog(request.Title, content, primaryButtonText: "OK") with
        {
            IsOpen = true,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = _ => Finish(interaction, pending, null),
        };

        return dialog.Set(DialogAutomation.Tag(isError ? "Error" : "Info"));
    }

    private static Element FactRow(string label, string value, bool monospace)
    {
        var text = TextBlock(value)
            .TextWrapping(TextWrapping.Wrap)
            .IsTextSelectionEnabled(true);

        if (monospace)
        {
            text = text.FontFamily(AppTheme.Monospace).FontSize(13);
        }

        return Grid(
            columns: [GridSize.Px(140), GridSize.Star()],
            rows: [GridSize.Auto],
            Caption(label).Foreground(Theme.SecondaryText).Grid(row: 0, column: 0),
            text.Grid(row: 0, column: 1));
    }

    /// <summary>
    /// Hands the answer back and removes the question from the queue.
    /// </summary>
    /// <remarks>
    /// Completion is queued for the next dispatcher turn. This runs inside the
    /// ContentDialog's own <c>Closed</c> callback, and re-rendering the host
    /// there would unmount the dialog while WinUI is still tearing it down.
    /// One turn later the control is fully closed and safe to remove.
    /// </remarks>
    private static void Finish(InteractionService interaction, PendingInteraction pending, object? value)
    {
        if (pending.Result.IsCompleted)
        {
            return;
        }

        void Complete()
        {
            pending.Complete(value);
            interaction.Dequeue(pending);
        }

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            Complete();
            return;
        }

        queue.TryEnqueue(() => Complete());
    }
}
