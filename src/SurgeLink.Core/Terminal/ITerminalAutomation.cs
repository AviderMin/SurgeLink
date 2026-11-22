namespace SurgeLink.Core.Terminal;

/// <summary>Observes and writes the same PTY used by the visible desktop terminal.</summary>
public interface ITerminalAutomation
{
    TerminalScreenSnapshot ReadScreen();
    TerminalOutputPage ReadOutput(long? cursor);
    Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct);
    Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct);

    Task<TerminalInputReceipt> SendCheckedTextAsync(string text, bool submit, TerminalScreenSnapshot expected, CancellationToken ct)
    {
        if (!ReadScreen().MatchesContent(expected)) throw new TerminalInputConflictException();
        return SendTextAsync(text, submit, expected.InputRevision, ct);
    }

    Task<TerminalInputReceipt> SendCheckedKeyAsync(string key, TerminalScreenSnapshot expected, CancellationToken ct)
    {
        if (!ReadScreen().MatchesContent(expected)) throw new TerminalInputConflictException();
        return SendKeyAsync(key, expected.InputRevision, ct);
    }
}

public sealed record TerminalScreenSnapshot(string Text, int Columns, int Rows, int CursorRow, int CursorColumn,
    bool CursorVisible, bool AlternateScreen, bool BracketedPaste, bool MouseTracking,
    long InputRevision, long OutputCursor, bool IsClosed, bool Truncated, int ViewportOffset = 0, string Source = "screen")
{
    /// <summary>
    /// Compares only the state an input decision depends on. <see cref="ViewportOffset"/> is
    /// presentation: scrolling the history moves the visible window without changing the
    /// screen buffer, so it must not invalidate a check the Agent just took. <see cref="OutputCursor"/>
    /// counts every byte the PTY produced, including output that leaves the visible buffer
    /// unchanged, so it is telemetry rather than a guard; a change that matters is already
    /// visible in <see cref="Text"/>, the cursor position or the terminal modes.
    /// </summary>
    public bool MatchesContent(TerminalScreenSnapshot other) =>
        string.Equals(Text, other.Text, StringComparison.Ordinal)
        && Columns == other.Columns && Rows == other.Rows
        && CursorRow == other.CursorRow && CursorColumn == other.CursorColumn
        && CursorVisible == other.CursorVisible
        && AlternateScreen == other.AlternateScreen && BracketedPaste == other.BracketedPaste
        && MouseTracking == other.MouseTracking
        && InputRevision == other.InputRevision && IsClosed == other.IsClosed && Truncated == other.Truncated;
}

/// <summary>Cursors count UTF-16 code units. Text retains terminal escape sequences.</summary>
public sealed record TerminalOutputPage(string Text, long Cursor, long OldestCursor, long LatestCursor, bool Gap, bool HasMore);

/// <summary>Acknowledges a PTY write, not remote command completion or an exit status.</summary>
public sealed record TerminalInputReceipt(long InputRevision, long OutputCursor, int AcceptedBytes, bool Submitted);

public sealed class TerminalInputConflictException() : InvalidOperationException("Terminal input changed; read the screen again.");
public sealed class TerminalInputUnknownException(Exception inner) : IOException("The PTY write may have been partially sent. Do not automatically retry with a new operation ID.", inner);
