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
        WriteAgentAsync(() =>
        {
            var paste = TerminalInputEncoder.EncodePaste(text, Emulator.Modes);
            return submit ? [.. paste, (byte)'\r'] : paste;
        }, submit, expectedRevision, ct);

    public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct)
    {
        var chord = TerminalKeyChord.Parse(key);
        return WriteAgentAsync(() => TerminalInputEncoder.EncodeKey(chord.VirtualKey, chord.Control, chord.Alt, chord.Shift, Emulator.Modes)
            ?? throw new ArgumentException("Unsupported terminal key."), chord.VirtualKey == 0x0D, expectedRevision, ct);
    }

    private async Task<TerminalInputReceipt> WriteAgentAsync(Func<byte[]> encode, bool submitted, long expectedRevision, CancellationToken ct)
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
                bytes = encode();
                Emulator.ScrollToBottom();
            }
            RaiseInvalidated();
            if (bytes.Length == 0) throw new ArgumentException("Terminal input is empty.");
            // Human input can arrive while encoding waits for the screen lock.
            // Reserve the observed revision atomically before the transport write.
            ct.ThrowIfCancellationRequested();
            var revision = expectedRevision + 1;
            if (Interlocked.CompareExchange(ref _inputRevision, revision, expectedRevision) != expectedRevision)
                throw new TerminalInputConflictException();
            var outputCursor = _output.Cursor;
            try { await _remote.WriteAsync(bytes, ct).ConfigureAwait(false); }
            catch (Exception ex) { throw new TerminalInputUnknownException(ex); }
            return new(revision, outputCursor, bytes.Length, submitted);
        }
        finally { _inputGate.Release(); }
    }
}
