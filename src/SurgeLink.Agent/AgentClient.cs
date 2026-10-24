using System.IO.Pipes;

namespace SurgeLink.Agent;

/// <summary>Shared client for CLI and future MCP adapters. Contains no UI or SSH implementation.</summary>
public sealed class AgentClient(string pipeName)
{
    public async Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(65));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, deadline.Token).ConfigureAwait(false);
        await AgentWire.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        return await AgentWire.ReadAsync<AgentResponse>(pipe, deadline.Token).ConfigureAwait(false);
    }
}
