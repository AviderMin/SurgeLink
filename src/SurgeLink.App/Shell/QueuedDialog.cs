using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

internal sealed record QueuedDialogProps(ContentDialogElement Dialog, CancellationToken CancellationToken);

/// <summary>Serializes native dialogs per owner, including protocol and file prompts.</summary>
internal sealed class QueuedDialog : Component<QueuedDialogProps>
{
    private static readonly ConditionalWeakTable<Window, ModalQueue> Queues = new();

    public static Element Wrap(ContentDialogElement dialog, CancellationToken cancellationToken = default) =>
        Component<QueuedDialog, QueuedDialogProps>(new(dialog, cancellationToken));

    public static bool IsBusy(Window? owner) =>
        owner is not null && Queues.TryGetValue(owner, out var queue) && queue.Current is not null;

    public override Element Render()
    {
        var window = UseWindow();
        var owner = UseRef<Window?>(null);
        owner.Current ??= window?.NativeWindow;
        var queue = Queues.GetOrCreateValue(owner.Current
            ?? throw new InvalidOperationException("A dialog requires an owning window."));
        var id = UseRef(Guid.NewGuid());
        var finished = UseRef(false);
        var lastDialog = UseRef<Element?>(null);
        var latest = UseRef(Props.Dialog);
        latest.Current = Props.Dialog;
        var current = UseExternalStore(queue.Subscribe, () => queue.Current);
        var settingsStore = AppServices.Current!.Settings;
        var settings = UseExternalStore(settingsStore.Subscribe, () => settingsStore.Current);

        UseEffect(() =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var registration = Props.CancellationToken.Register(() => dispatcher?.TryEnqueue(() =>
            {
                // A waiting prompt has no native popup to hide. Complete it
                // without flashing it after the current dialog eventually closes.
                if (lastDialog.Current is null) Finish(ContentDialogResult.None);
            }));
            queue.Enqueue(id.Current);
            return () =>
            {
                registration.Dispose();
                queue.Remove(id.Current);
            };
        }, queue);

        void Finish(ContentDialogResult result)
        {
            if (finished.Current) return;
            finished.Current = true;
            var callback = latest.Current.OnClosed;
            // ContentDialog.Closed runs before native popup teardown finishes.
            // Release the modal slot only after its deferred application callback.
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            void Complete()
            {
                try { callback?.Invoke(result); }
                finally { queue.Remove(id.Current); }
            }
            if (dispatcher is null) Complete();
            else dispatcher.TryEnqueue(Complete);
        }

        // Retain the exact closed element: another render must never reopen it.
        if (finished.Current) return lastDialog.Current ?? Empty();
        if (current != id.Current || !Props.Dialog.IsOpen) return Empty();

        var dialog = Props.Dialog;
        lastDialog.Current = (dialog with
        {
            OnClosed = Finish,
        }).Set(control => control.RequestedTheme = ThemeController.ToElementTheme(settings.Theme));
        return lastDialog.Current;
    }
}
