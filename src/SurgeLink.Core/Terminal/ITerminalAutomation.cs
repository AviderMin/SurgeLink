namespace SurgeLink.Core.Terminal;

/// <summary>Observes and writes the same PTY used by the visible desktop terminal.</summary>
public interface ITerminalAutomation
{
    TerminalScreenSnapshot ReadScreen();
    TerminalOutputPage ReadOutput(long? cursor);
    Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct);
    Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct);
}

public sealed record TerminalScreenSnapshot(string Text, int Columns, int Rows, int CursorRow, int CursorColumn,
    bool CursorVisible, bool AlternateScreen, bool BracketedPaste, bool MouseTracking,
    long InputRevision, long OutputCursor, bool IsClosed, bool Truncated, int ViewportOffset = 0, string Source = "screen");

/// <summary>Cursors count UTF-16 code units. Text retains terminal escape sequences.</summary>
public sealed record TerminalOutputPage(string Text, long Cursor, long OldestCursor, long LatestCursor, bool Gap, bool HasMore);

/// <summary>Acknowledges a PTY write, not remote command completion or an exit status.</summary>
public sealed record TerminalInputReceipt(long InputRevision, long OutputCursor, int AcceptedBytes, bool Submitted);

public sealed class TerminalInputConflictException() : InvalidOperationException("Terminal input changed; read the screen again.");
public sealed class TerminalInputUnknownException(Exception inner) : IOException("The PTY write may have been partially sent. Do not automatically retry with a new operation ID.", inner);
