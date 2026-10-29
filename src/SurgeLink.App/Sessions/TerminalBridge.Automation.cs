using SurgeLink.Core.Terminal;
using SurgeLink.Terminal;

namespace SurgeLink.App.Sessions;

public sealed partial class TerminalBridge
{
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly TerminalOutputBuffer _output = new();
    private long _inputRevision;

    private void MarkUserInput()
    {
        Interlocked.Increment(ref _inputRevision);
        UserInput?.Invoke(this, EventArgs.Empty);
    }

    public TerminalScreenSnapshot ReadScreen()
    {
        lock (Emulator.SyncRoot)
        {
            var text = Emulator.GetVisibleText();
            var cursor = Emulator.Cursor;
            var modes = Emulator.Modes;
            return new(text[..Math.Min(text.Length, 32768)], Emulator.Columns, Emulator.Rows,
                cursor.Row, cursor.Column, cursor.Visible, modes.AlternateScreen, modes.BracketedPaste,
                modes.MouseTracking, Interlocked.Read(ref _inputRevision), _output.Cursor, IsClosed, text.Length > 32768, Emulator.ViewportOffset);
        }
    }

    public TerminalOutputPage ReadOutput(long? cursor) => _output.Read(cursor);

    public Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct) =>
        SendTextCoreAsync(text, submit, expectedRevision, ct);

    public Task<TerminalInputReceipt> SendCheckedTextAsync(string text, bool submit, TerminalScreenSnapshot expected, CancellationToken ct) =>
        SendTextCoreAsync(text, submit, expected.InputRevision, ct, expected);

    private Task<TerminalInputReceipt> SendTextCoreAsync(string text, bool submit, long expectedRevision, CancellationToken ct,
        TerminalScreenSnapshot? expected = null) =>
        WriteAgentAsync(() =>
        {
            var paste = TerminalInputEncoder.EncodePaste(text, Emulator.Modes);
            return submit ? [.. paste, (byte)'\r'] : paste;
        }, submit, expectedRevision, ct, expected);

    public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct)
        => SendKeyCoreAsync(key, expectedRevision, ct);

    public Task<TerminalInputReceipt> SendCheckedKeyAsync(string key, TerminalScreenSnapshot expected, CancellationToken ct)
        => SendKeyCoreAsync(key, expected.InputRevision, ct, expected);

    private Task<TerminalInputReceipt> SendKeyCoreAsync(string key, long expectedRevision, CancellationToken ct, TerminalScreenSnapshot? expected = null)
    {
        var chord = TerminalKeyChord.Parse(key);
        return WriteAgentAsync(() => TerminalInputEncoder.EncodeKey(chord.VirtualKey, chord.Control, chord.Alt, chord.Shift, Emulator.Modes)
            ?? throw new ArgumentException("Unsupported terminal key."), chord.VirtualKey == 0x0D, expectedRevision, ct, expected);
    }

    private async Task<TerminalInputReceipt> WriteAgentAsync(Func<byte[]> encode, bool submitted, long expectedRevision, CancellationToken ct,
        TerminalScreenSnapshot? expected = null)
    {
        await _inputGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (IsClosed) throw new InvalidOperationException("The terminal is closed.");
            if (Interlocked.Read(ref _inputRevision) != expectedRevision) throw new TerminalInputConflictException();
            byte[] bytes;
            lock (Emulator.SyncRoot)
            {
                if (expected is not null && ReadScreen() != expected) throw new TerminalInputConflictException();
                bytes = encode();
                if (bytes.Length == 0) throw new ArgumentException("Terminal input is empty.");
                ct.ThrowIfCancellationRequested();
                // Reserve while holding the screen lock so output cannot invalidate
                // the checked view between comparison, encoding and reservation.
                if (Interlocked.CompareExchange(ref _inputRevision, expectedRevision + 1, expectedRevision) != expectedRevision)
                    throw new TerminalInputConflictException();
                Emulator.ScrollToBottom();
            }
            RaiseInvalidated();
            var revision = expectedRevision + 1;
            var outputCursor = _output.Cursor;
            try { await _remote.WriteAsync(bytes, ct).ConfigureAwait(false); }
            catch (Exception ex) { throw new TerminalInputUnknownException(ex); }
            return new(revision, outputCursor, bytes.Length, submitted);
        }
        finally { _inputGate.Release(); }
    }
}
