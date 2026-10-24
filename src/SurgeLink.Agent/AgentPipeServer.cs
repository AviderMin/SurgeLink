using System.IO.Pipes;
using System.Text.Json;

namespace SurgeLink.Agent;

/// <summary>Current-user IPC. Intentionally serial: at most one desktop read/approval at a time.</summary>
public sealed class AgentPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly AgentGateway _gateway;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _run;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public AgentPipeServer(string pipeName, AgentGateway gateway)
    {
        _gateway = gateway;
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        _run = RunAsync();
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                var request = await AgentWire.ReadAsync<AgentRequest>(_pipe, deadline.Token).ConfigureAwait(false);
                var response = await _gateway.ExecuteAsync(request, deadline.Token).ConfigureAwait(false);
                using var reply = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                reply.CancelAfter(TimeSpan.FromSeconds(2));
                // A response that cannot fit never produces a partial frame.
                try { await AgentWire.WriteAsync(_pipe, response, reply.Token).ConfigureAwait(false); }
                catch (AgentFrameTooLargeException) when (_pipe.IsConnected && !reply.IsCancellationRequested)
                {
                    await AgentWire.WriteAsync(_pipe, AgentResponse.Error("output_limit", "The result exceeds the transport limit."), reply.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException) { }
            finally
            {
                try { if (_pipe.IsConnected) _pipe.Disconnect(); }
                catch (IOException) { /* A client may disappear between the state check and disconnect. */ }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            try { await _shutdown.CancelAsync().ConfigureAwait(false); }
            finally { await _run.ConfigureAwait(false); }
        }
        finally { await _pipe.DisposeAsync().ConfigureAwait(false); _shutdown.Dispose(); }
    }
}
