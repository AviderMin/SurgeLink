using System.Diagnostics;
using Renci.SshNet;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Limits network work while excluding time spent answering user prompts.</summary>
internal sealed class SshConnectBudget : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _timeout;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _limit;
    private TimeSpan _remaining;
    private int _pauses;
    private bool _disposed;

    internal SshConnectBudget(TimeSpan limit, CancellationToken cancellationToken)
    {
        _limit = _remaining = limit;
        _timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timeout.CancelAfter(limit);
    }

    internal CancellationToken Token => _timeout.Token;
    internal bool IsCancellationRequested => _timeout.IsCancellationRequested;

    internal void Pause()
    {
        lock (_gate)
        {
            if (_disposed || _pauses++ != 0) { return; }
            _remaining -= _clock.Elapsed;
            _timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (_disposed || --_pauses != 0) { return; }
            _clock.Restart();
            _timeout.CancelAfter(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero);
        }
    }

    internal async Task ConnectAsync(BaseClient client)
    {
        // SSH.NET also has synchronous waits during authentication. Those waits
        // must not impose a second, non-pausable deadline on a human prompt.
        client.ConnectionInfo.Timeout = Timeout.InfiniteTimeSpan;
        var connect = Task.Run(() => client.ConnectAsync(Token), CancellationToken.None);
        TaskObservation.ObserveFaults(connect);
        try { await connect.WaitAsync(Token).ConfigureAwait(false); }
        finally { client.ConnectionInfo.Timeout = _limit; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timeout.Dispose();
        }
    }
}
