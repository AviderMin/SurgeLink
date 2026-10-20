using Microsoft.UI.Dispatching;

namespace SurgeLink.App.Services;

/// <summary>WinUI <see cref="DispatcherQueue"/> based dispatcher.</summary>
public sealed class DispatcherUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;
    private readonly int _threadId;

    /// <summary>Captures the dispatcher of the calling (UI) thread.</summary>
    public DispatcherUiDispatcher(DispatcherQueue queue, int threadId)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _threadId = threadId;
    }

    /// <summary>Creates a dispatcher from the current thread; must be called on the UI thread.</summary>
    public static DispatcherUiDispatcher CreateForCurrentThread()
    {
        var queue = DispatcherQueue.GetForCurrentThread()
                    ?? throw new InvalidOperationException("No DispatcherQueue on the current thread.");
        return new DispatcherUiDispatcher(queue, Environment.CurrentManagedThreadId);
    }

    /// <inheritdoc />
    public bool IsOnUiThread => Environment.CurrentManagedThreadId == _threadId;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsOnUiThread)
        {
            action();
            return;
        }

        _queue.TryEnqueue(() => action());
    }
}
