using System.IO.Pipes;
using System.Text.Json;

namespace SurgeLink.Agent;

/// <summary>Current-user IPC with independent connections. Session input remains serialized by the gateway.</summary>
public sealed class AgentPipeServer : IAsyncDisposable
{
    private const int MaximumConnections = 64;
    private readonly string _pipeName;
    private readonly AgentGateway _gateway;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _run;
    private readonly object _connectionGate = new();
    private readonly HashSet<Task> _connections = [];
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public AgentPipeServer(string pipeName, AgentGateway gateway)
    {
        _pipeName = pipeName;
        _gateway = gateway;
        // Claim the endpoint synchronously. Otherwise a second desktop could appear to start
        // successfully and only fail later inside the asynchronous accept loop.
        var first = CreatePipe(first: true);
        _run = RunAsync(first);
    }

    private NamedPipeServerStream CreatePipe(bool first = false) => new(_pipeName, PipeDirection.InOut, MaximumConnections,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : 0));

    private async Task RunAsync(NamedPipeServerStream first)
    {
        var pipe = first;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                var accepted = pipe;
                pipe = CreatePipe();
                TrackConnection(ServeAsync(accepted));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        catch (IOException) { /* Preserve the existing best-effort desktop IPC lifecycle. */ }
        finally { await pipe.DisposeAsync().ConfigureAwait(false); }
    }

    private void TrackConnection(Task connection)
    {
        lock (_connectionGate) _connections.Add(connection);
        _ = connection.ContinueWith(finished =>
        {
            lock (_connectionGate) _connections.Remove(finished);
            _ = finished.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var request = await AgentWire.ReadAsync<AgentRequest>(pipe, deadline.Token).ConfigureAwait(false);
            var response = await _gateway.ExecuteAsync(request, deadline.Token).ConfigureAwait(false);
            using var reply = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            reply.CancelAfter(TimeSpan.FromSeconds(2));
            // A response that cannot fit never produces a partial frame.
            try { await AgentWire.WriteAsync(pipe, response, reply.Token).ConfigureAwait(false); }
            catch (AgentFrameTooLargeException) when (pipe.IsConnected && !reply.IsCancellationRequested)
            {
                await AgentWire.WriteAsync(pipe, AgentResponse.Error("output_limit", "The result exceeds the transport limit."), reply.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException) { }
        finally { await pipe.DisposeAsync().ConfigureAwait(false); }
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
            Task[] connections;
            lock (_connectionGate) connections = _connections.ToArray();
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
        finally { _shutdown.Dispose(); }
    }
}
