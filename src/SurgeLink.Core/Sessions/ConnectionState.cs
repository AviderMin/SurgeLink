namespace SurgeLink.Core.Sessions;

/// <summary>Lifecycle state of a session.</summary>
public enum ConnectionState
{
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Disconnected,
    Faulted,
}

/// <summary>State change notification payload.</summary>
public sealed class SessionStateChangedEventArgs : EventArgs
{
    public SessionStateChangedEventArgs(ConnectionState oldState, ConnectionState newState, string? message = null)
    {
        OldState = oldState;
        NewState = newState;
        Message = message;
    }

    public ConnectionState OldState { get; }

    public ConnectionState NewState { get; }

    public string? Message { get; }
}
