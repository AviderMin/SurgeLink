namespace SurgeLink.Core.Terminal;

/// <summary>Options applied when the remote shell channel is opened.</summary>
public sealed record TerminalStartOptions
{
    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 32;

    /// <summary>TERM value sent to the server.</summary>
    public string TerminalType { get; init; } = "xterm-256color";

    /// <summary>Extra environment variables requested from the server.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>Raw bytes received from the remote shell.</summary>
public sealed class TerminalOutputEventArgs : EventArgs
{
    public TerminalOutputEventArgs(ReadOnlyMemory<byte> data) => Data = data;

    public ReadOnlyMemory<byte> Data { get; }
}

/// <summary>
/// The byte stream side of a terminal: no parsing, no rendering. The emulator
/// in <c>SurgeLink.Terminal</c> consumes these bytes.
/// </summary>
public interface IRemoteTerminal : IAsyncDisposable
{
    int Columns { get; }

    int Rows { get; }

    bool IsOpen { get; }

    /// <summary>Raised on the thread that read the bytes, not the UI thread.</summary>
    event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    /// <summary>Raised once when the remote shell exits or the channel drops.</summary>
    event EventHandler? Closed;

    /// <summary>Opens the shell channel with a PTY of the requested size.</summary>
    Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken);

    /// <summary>Writes raw input bytes (already encoded by the terminal input encoder).</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Notifies the server that the viewport changed size.</summary>
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
}
