using System.Text;
using SurgeLink.Core.Terminal;
using Xunit.Sdk;

namespace SurgeLink.Protocols.Conformance;

/// <summary>
/// Collects terminal output as UTF-8 text so tests can poll for a fragment
/// instead of sleeping. Subscribe before <see cref="IRemoteTerminal.StartAsync"/>
/// to capture the banner/prompt as well.
/// </summary>
public sealed class TerminalCapture : IDisposable
{
    private readonly IRemoteTerminal _terminal;
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private int _chunkCount;
    private bool _disposed;

    public TerminalCapture(IRemoteTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        _terminal = terminal;
        _terminal.OutputReceived += OnOutputReceived;
    }

    /// <summary>All output seen so far.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>Number of output chunks received so far.</summary>
    public int ChunkCount
    {
        get
        {
            lock (_gate)
            {
                return _chunkCount;
            }
        }
    }

    /// <summary>Polls captured output until it contains <paramref name="fragment"/>.</summary>
    public async Task WaitForTextAsync(string fragment, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Text.Contains(fragment, StringComparison.Ordinal))
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XunitException(
                    $"Terminal output did not contain '{fragment}' within {timeout.TotalMilliseconds:0} ms. Captured tail: {Tail()}");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _terminal.OutputReceived -= OnOutputReceived;
    }

    private void OnOutputReceived(object? sender, TerminalOutputEventArgs e)
    {
        string chunk = Encoding.UTF8.GetString(e.Data.Span);
        lock (_gate)
        {
            _text.Append(chunk);
            _chunkCount++;
        }
    }

    private string Tail()
    {
        string text = Text;
        return text.Length <= 400 ? text : "..." + text[^400..];
    }
}
