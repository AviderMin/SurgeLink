using SurgeLink.Core.Protocols;

namespace SurgeLink.App.Services;

/// <summary>Kinds of question the app can ask the user.</summary>
public enum InteractionKind
{
    HostKey,
    Secret,
    Confirmation,
    Text,
    Choice,
    Error,
    Info,
}

/// <summary>
/// One pending question. The dialog layer renders it and calls <see cref="Complete"/>.
/// Protocol code awaits <see cref="Result"/> without knowing anything about UI.
/// </summary>
public sealed class PendingInteraction
{
    private readonly TaskCompletionSource<object?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingInteraction(InteractionKind kind, object request, CancellationToken cancellationToken)
    {
        Kind = kind;
        Request = request;
        CancellationToken = cancellationToken;
    }

    public InteractionKind Kind { get; }

    /// <summary>
    /// Stable identity of this question. The dialog host keys its dialog by this
    /// value so a dialog for the next question is rebuilt instead of reused.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Typed payload: <see cref="HostKeyChallenge"/>, <see cref="SecretRequest"/>, <see cref="ConfirmationRequest"/>, <see cref="TextRequest"/> or <see cref="MessageRequest"/>.</summary>
    public object Request { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>Completes when the dialog closes; <c>null</c> means "dismissed".</summary>
    public Task<object?> Result => _completion.Task;

    /// <summary>Called by the dialog layer with the user's answer.</summary>
    public void Complete(object? value) => _completion.TrySetResult(value);

    internal void Cancel() => _completion.TrySetCanceled(CancellationToken);
}

/// <summary>Request for a secret such as a password or key passphrase.</summary>
public sealed record SecretRequest(string Title, string Description, bool IsPassword = true);

/// <summary>Yes/no request.</summary>
public sealed record ConfirmationRequest(string Title, string Message, string PrimaryText = "Continue", string CloseText = "Cancel");

/// <summary>Free text request.</summary>
public sealed record TextRequest(string Title, string Label, string? InitialValue = null);

/// <summary>Three-way choice whose close button is the safe default.</summary>
public sealed record ChoiceRequest(string Title, string Message, string PrimaryText, string SecondaryText, string CloseText);

/// <summary>Notification shown as a dialog.</summary>
public sealed record MessageRequest(string Title, string Message);

/// <summary>
/// Queue of user interactions. Protocols and services await it; the shell's
/// dialog host renders whatever is currently at the head of the queue.
/// </summary>
public sealed class InteractionService
{
    private readonly object _gate = new();
    private readonly LinkedList<PendingInteraction> _queue = new();
    private readonly IUiDispatcher _dispatcher;

    /// <summary>Creates the service on the UI thread.</summary>
    public InteractionService(IUiDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Raised when the head of the queue changes; may fire on a background thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The interaction the dialog host should currently display.</summary>
    public PendingInteraction? Current
    {
        get
        {
            lock (_gate)
            {
                return _queue.First?.Value;
            }
        }
    }

    /// <summary>Scoped subscription helper for <c>UseExternalStore</c>.</summary>
    public Action Subscribe(Action listener)
    {
        EventHandler handler = (_, _) => listener();
        Changed += handler;
        return () => Changed -= handler;
    }

    /// <summary>Asks whether an unknown or changed host key may be trusted.</summary>
    public async Task<HostKeyDecision> RequestHostKeyTrustAsync(HostKeyChallenge challenge, CancellationToken cancellationToken)
    {
        var pending = Enqueue(InteractionKind.HostKey, challenge, cancellationToken);
        var result = await AwaitAnswerAsync(pending, cancellationToken).ConfigureAwait(false);
        return result as HostKeyDecision ?? HostKeyDecision.Reject;
    }

    /// <summary>Asks for a secret value; returns <c>null</c> when dismissed.</summary>
    public async Task<string?> RequestSecretAsync(SecretRequest request, CancellationToken cancellationToken)
    {
        var pending = Enqueue(InteractionKind.Secret, request, cancellationToken);
        var result = await AwaitAnswerAsync(pending, cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    /// <summary>Asks a yes/no question.</summary>
    public async Task<bool> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken cancellationToken)
    {
        var pending = Enqueue(InteractionKind.Confirmation, request, cancellationToken);
        var result = await AwaitAnswerAsync(pending, cancellationToken).ConfigureAwait(false);
        return result is true;
    }

    /// <summary>Asks for free text; returns <c>null</c> when dismissed.</summary>
    public async Task<string?> RequestTextAsync(TextRequest request, CancellationToken cancellationToken)
    {
        var pending = Enqueue(InteractionKind.Text, request, cancellationToken);
        var result = await AwaitAnswerAsync(pending, cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    /// <summary>Returns 1 for primary, 2 for secondary, or 0 when dismissed.</summary>
    public async Task<int> RequestChoiceAsync(ChoiceRequest request, CancellationToken cancellationToken)
    {
        var pending = Enqueue(InteractionKind.Choice, request, cancellationToken);
        return await AwaitAnswerAsync(pending, cancellationToken).ConfigureAwait(false) is int choice ? choice : 0;
    }

    /// <summary>Shows a blocking error message.</summary>
    public Task ShowErrorAsync(string title, string message) =>
        ShowMessageAsync(InteractionKind.Error, new MessageRequest(title, message));

    /// <summary>Shows a blocking informational message.</summary>
    public Task ShowInfoAsync(string title, string message) =>
        ShowMessageAsync(InteractionKind.Info, new MessageRequest(title, message));

    private async Task ShowMessageAsync(InteractionKind kind, MessageRequest request)
    {
        var pending = Enqueue(kind, request, CancellationToken.None);
        await pending.Result.ConfigureAwait(false);
    }

    private PendingInteraction Enqueue(InteractionKind kind, object request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingInteraction(kind, request, cancellationToken);
        lock (_gate)
        {
            _queue.AddLast(pending);
        }

        RaiseChanged();
        return pending;
    }

    private async Task<object?> AwaitAnswerAsync(PendingInteraction pending, CancellationToken ct)
    {
        using var registration = ct.Register(() =>
        {
            var removed = false;
            lock (_gate)
            {
                // The native host closes an already-current popup using its own token binding.
                // Waiting prompts can be removed immediately without ever displaying them.
                if (!ReferenceEquals(_queue.First?.Value, pending)) removed = _queue.Remove(pending);
            }
            if (removed) { pending.Cancel(); RaiseChanged(); }
        });
        return await pending.Result.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes the head of the queue; called by the dialog host once it is closed.</summary>
    public void Dequeue(PendingInteraction pending)
    {
        lock (_gate)
        {
            _queue.Remove(pending);
        }

        RaiseChanged();
    }

    /// <summary>Cancels every pending question, for shutdown.</summary>
    public void CancelAll()
    {
        PendingInteraction[] snapshot;
        lock (_gate)
        {
            snapshot = _queue.ToArray();
            _queue.Clear();
        }

        foreach (var pending in snapshot)
        {
            pending.Cancel();
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_dispatcher.IsOnUiThread)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.Post(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }
}
