namespace SurgeLink.App.Services;

/// <summary>Marshals work onto the UI thread. Every background callback that touches UI state goes through this.</summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller already runs on the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread, immediately when already there.</summary>
    void Post(Action action);
}
