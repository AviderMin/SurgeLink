using System.IO.Pipes;
using System.Text.Json;

namespace SurgeLink.Agent;

/// <summary>Current-user IPC with independent connections. Session input remains serialized by the gateway.</summary>
public sealed class AgentPipeServer : IAsyncDisposable
{
    private const int MaximumConnections = 64;
    private static readonly TimeSpan ListenRetryDelay = TimeSpan.FromMilliseconds(250);
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

    private NamedPipeServerStream CreatePipe(bool first = false) => new(_pipeName, PipeDirection.InOut, MaximumConnections + 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : 0));

    private async Task RunAsync(NamedPipeServerStream first)
    {
        var pipe = first;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                NamedPipeServerStream? accepted = null;
                try
                {
                    await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                    accepted = pipe;

                    // Serve before allocating another instance. Even at saturation,
                    // the last accepted client must have a reader and a deadline.
                    TrackConnection(ServeAsync(accepted));
                    accepted = null;
                    while (true)
                    {
                        try
                        {
                            pipe = CreatePipe();
                            break;
                        }
                        catch (IOException) when (!_shutdown.IsCancellationRequested)
                        {
                            await Task.Delay(ListenRetryDelay, _shutdown.Token).ConfigureAwait(false);
                        }
                    }

                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
                catch (IOException)
                {
                    // A broken or aborted wait must not end the listener. Back off
                    // briefly so a persistent failure cannot spin the loop.
                    try { await Task.Delay(ListenRetryDelay, _shutdown.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                }
                finally
                {
                    // Only release the accepted pipe when it was not handed off.
                    if (accepted is not null) { await accepted.DisposeAsync().ConfigureAwait(false); }
                }
            }
        }
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
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            var request = await AgentWire.ReadAsync<AgentRequest>(pipe, readDeadline.Token).ConfigureAwait(false);
            var response = await _gateway.ExecuteAsync(request, deadline.Token).ConfigureAwait(false);
            using var reply = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            reply.CancelAfter(TimeSpan.FromSeconds(2));
            // A response that cannot fit never produces a partial frame.
            try { await AgentWire.WriteAsync(pipe, response, reply.Token).ConfigureAwait(false); }
            catch (AgentFrameTooLargeException) when (pipe.IsConnected && !reply.IsCancellationRequested)
            {
                await AgentWire.WriteAsync(pipe, AgentResponse.Error("output_limit",
                    "The result exceeds the 1 MiB transport limit. Retry with a narrower request: read terminal output incrementally or read a smaller file/directory instead of listing everything."), reply.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException) { }
        catch (Exception)
        {
            // Unexpected handler/serialization failures still produce a bounded reply.
            using var reply = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            reply.CancelAfter(TimeSpan.FromSeconds(2));
            try { await AgentWire.WriteAsync(pipe, AgentResponse.Error("operation_failed", "The Agent request failed."), reply.Token).ConfigureAwait(false); }
            catch (Exception) { /* The client may already have disconnected. */ }
        }
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
