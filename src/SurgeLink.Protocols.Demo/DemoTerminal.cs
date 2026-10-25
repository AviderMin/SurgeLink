using System.Text;
using System.Threading.Channels;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Protocols.Demo;

/// <summary>
/// A tiny simulated POSIX shell. It echoes typed characters, answers a handful
/// of commands with ANSI coloured output and produces a coloured login banner,
/// which makes every terminal rendering feature visible without a server.
/// </summary>
public sealed class DemoTerminal : IRemoteTerminal
{
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>();
    private readonly StringBuilder _line = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _user;
    private readonly string _host;
    private readonly string _prompt;
    private Task? _pump;
    private int _closed;
    private int _closedRaised;
    private int _disposed;
    private bool _lastInputWasCarriageReturn;

    internal DemoTerminal(string user, string host)
    {
        _user = string.IsNullOrWhiteSpace(user) ? "demo" : user;
        _host = string.IsNullOrWhiteSpace(host) ? "demo-host" : host;
        _prompt = Ansi.Paint("demo@surge", "1;32") + ":" + Ansi.Paint("~", "1;34") + "$ ";
        Columns = 120;
        Rows = 32;
    }

    /// <inheritdoc />
    public int Columns { get; private set; }

    /// <inheritdoc />
    public int Rows { get; private set; }

    /// <inheritdoc />
    public bool IsOpen => _pump is not null && _closed == 0;

    /// <inheritdoc />
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken)
    {
        Columns = options.Columns;
        Rows = options.Rows;
        _pump = Task.Run(PumpAsync, CancellationToken.None);
        EmitBanner();
        Emit(_prompt);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed) != 0) { return ValueTask.CompletedTask; }
        var text = Encoding.UTF8.GetString(data.Span);
        foreach (var ch in text)
        {
            if (Volatile.Read(ref _closed) != 0) { break; }
            if (ch != '\n')
            {
                _lastInputWasCarriageReturn = false;
            }

            switch (ch)
            {
                case '\r':
                    _lastInputWasCarriageReturn = true;
                    HandleEnter();
                    break;
                case '\n':
                    // Enter typed on a POSIX terminal arrives as CR; CRLF only shows up in pasted text.
                    if (_lastInputWasCarriageReturn)
                    {
                        _lastInputWasCarriageReturn = false;
                        break;
                    }

                    HandleEnter();
                    break;
                case (char)127:
                case '\b':
                    if (_line.Length > 0)
                    {
                        _line.Length--;
                        Emit(Ansi.Esc + "[D " + Ansi.Esc + "[D");
                    }

                    break;
                case (char)3:
                    _line.Clear();
                    Emit("^C" + Ansi.Crlf + _prompt);
                    break;
                case (char)12:
                    Emit(Ansi.ClearScreen + _prompt);
                    break;
                default:
                    if (!char.IsControl(ch))
                    {
                        _line.Append(ch);
                        Emit(ch.ToString());
                    }

                    break;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        Columns = columns;
        Rows = rows;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _closed, 1);
        _lifetime.Cancel();
        _outbox.Writer.TryComplete();
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        RaiseClosedOnce();
        _lifetime.Dispose();
    }

    private void EmitBanner()
    {
        Emit(Ansi.Paint("+-----------------------------------------+", "1;36") + Ansi.Crlf);
        Emit(Ansi.Paint("|  S u r g e L i n k   -   d e m o       |", "1;36") + Ansi.Crlf);
        Emit(Ansi.Paint("+-----------------------------------------+", "1;36") + Ansi.Crlf + Ansi.Crlf);
        Emit(Ansi.Paint("Welcome to the SurgeLink demo session", "1;33")
             + " - user " + Ansi.Paint(_user, "1") + " on " + Ansi.Paint(_host, "1") + "." + Ansi.Crlf);
        Emit("This is an offline sandbox. Type " + Ansi.Paint("help", "1;35") + " for the command list." + Ansi.Crlf + Ansi.Crlf);
    }

    private void HandleEnter()
    {
        var command = _line.ToString().Trim();
        _line.Clear();
        Emit(Ansi.Crlf);
        if (command.Length == 0)
        {
            Emit(_prompt);
            return;
        }

        Execute(command);
        if (Volatile.Read(ref _closed) == 0) { Emit(_prompt); }
    }

    private void Execute(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();
        switch (name)
        {
            case "help":
                Emit("Available demo commands:" + Ansi.Crlf);
                Emit("  " + Ansi.Paint("ls", "1;32") + "        list files            "
                     + Ansi.Paint("pwd", "1;32") + "     print working directory" + Ansi.Crlf);
                Emit("  " + Ansi.Paint("whoami", "1;32") + "    current user           "
                     + Ansi.Paint("uname", "1;32") + "   kernel information" + Ansi.Crlf);
                Emit("  " + Ansi.Paint("date", "1;32") + "      current time           "
                     + Ansi.Paint("colors", "1;32") + "  256 colour chart" + Ansi.Crlf);
                Emit("  " + Ansi.Paint("monitor", "1;32") + "   fake metrics           "
                     + Ansi.Paint("clear", "1;32") + "    clear the screen" + Ansi.Crlf);
                Emit("  " + Ansi.Paint("exit", "1;32") + "      close the session      "
                     + Ansi.Paint("echo", "1;32") + "     print text" + Ansi.Crlf);
                break;
            case "ls":
                Emit(Ansi.Paint("Documents", "1;34") + "   " + Ansi.Paint("Projects", "1;34") + "   "
                     + Ansi.Paint("demo.txt", "1;32") + "   " + Ansi.Paint(".bashrc", "0;37") + Ansi.Crlf);
                break;
            case "pwd":
                Emit("/home/" + _user + Ansi.Crlf);
                break;
            case "whoami":
                Emit(_user + Ansi.Crlf);
                break;
            case "uname":
                Emit("Linux " + _host + " 6.6.0-demo #1 SMP PREEMPT_DYNAMIC x86_64 GNU/Linux" + Ansi.Crlf);
                break;
            case "date":
                Emit(DateTimeOffset.Now.ToString("R") + Ansi.Crlf);
                break;
            case "echo":
                Emit(string.Join(' ', parts.Skip(1)) + Ansi.Crlf);
                break;
            case "clear":
                Emit(Ansi.ClearScrollback);
                break;
            case "colors":
                EmitColorChart();
                break;
            case "monitor":
                Emit(Ansi.Paint("Open the Monitor tab to see the synthetic charts.", "1;33") + Ansi.Crlf);
                break;
            case "exit":
                Emit(Ansi.Paint("logout", "1;33") + Ansi.Crlf + "Connection to demo closed." + Ansi.Crlf);
                Interlocked.Exchange(ref _closed, 1);
                _outbox.Writer.TryComplete();
                break;
            default:
                Emit(Ansi.Paint("demo-sh: " + name + ": command not found", "1;31") + Ansi.Crlf);
                break;
        }
    }

    private void EmitColorChart()
    {
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                var code = 30 + row * 60 + column;
                Emit(Ansi.Sgr(code.ToString()) + "  " + code.ToString().PadLeft(3) + "  " + Ansi.Reset);
            }

            Emit(Ansi.Crlf);
        }

        Emit(Ansi.Esc + "[38;2;255;120;0mTrue colour: " + Ansi.Esc + "[38;2;0;200;255mRGB "
             + Ansi.Esc + "[38;2;255;0;120mcolumns" + Ansi.Reset + Ansi.Crlf);
        Emit("Styles: " + Ansi.Paint("bold", "1") + " " + Ansi.Paint("dim", "2") + " " + Ansi.Paint("italic", "3")
             + " " + Ansi.Paint("underline", "4") + " " + Ansi.Paint("inverse", "7") + " " + Ansi.Paint("strike", "9") + Ansi.Crlf);
    }

    private void Emit(string text)
    {
        if (_closed != 0)
        {
            return;
        }

        _outbox.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var chunk in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                OutputReceived?.Invoke(this, new TerminalOutputEventArgs(chunk));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            RaiseClosedOnce();
        }
    }

    private void RaiseClosedOnce()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
