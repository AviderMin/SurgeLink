using System.Text;

namespace SurgeLink.Terminal.Tests;

/// <summary>Facade that makes emulator tests read like terminal transcripts.</summary>
internal sealed class TestTerminal
{
    public TestTerminal(int columns = 80, int rows = 24, int scrollbackLimit = 1000)
    {
        Emulator = new TerminalEmulator(columns, rows, scrollbackLimit);
    }

    public TerminalEmulator Emulator { get; }

    public int Columns => Emulator.Columns;

    public int Rows => Emulator.Rows;

    public TerminalModes Modes => Emulator.Modes;

    public TerminalCursor Cursor => Emulator.Cursor;

    public void Feed(string text) => Emulator.Feed(Encoding.UTF8.GetBytes(text));

    public void Feed(params byte[] bytes) => Emulator.Feed(bytes);

    public void Feed(ReadOnlySpan<byte> bytes) => Emulator.Feed(bytes);

    public string[] Lines => Emulator.GetVisibleText().Split('\n');

    public string Line(int row) => Lines[row];

    public TerminalCell Cell(int row, int column) => Emulator.GetCell(row, column);

    public string CellText(int row, int column) => Emulator.GetCell(row, column).Text;
}
