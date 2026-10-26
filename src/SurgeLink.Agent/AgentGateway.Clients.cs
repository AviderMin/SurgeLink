namespace SurgeLink.Agent;

public sealed partial class AgentGateway
{
    private readonly Dictionary<string, AgentMcpPeer> _mcpPeers = new(StringComparer.Ordinal);
    public static TimeSpan McpPresenceLifetime => TimeSpan.FromSeconds(90);

    public event Action? McpPeersChanged;

    /// <summary>Recent heartbeats only; client names are self-reported and convey no authority.</summary>
    public IReadOnlyList<AgentMcpPeer> McpPeers
    {
        get
        {
            lock (_gate) return _mcpPeers.Values.Where(p => IsPresent(p)).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        }
    }

    private bool IsPresent(AgentMcpPeer peer) => _timeProvider.GetUtcNow() - peer.LastSeenUtc < McpPresenceLifetime;

    private AgentResponse UpdateMcpPresence(AgentRequest request)
    {
        if (request.ClientId is not { Length: 48 } id || !id.All(char.IsAsciiHexDigit)
            || request.Session is not null)
            return AgentResponse.Error("invalid_arguments", "Supply a valid MCP connection ID without a session handle.");
        if (request.Method == "client.heartbeat"
            && (string.IsNullOrWhiteSpace(request.ClientName) || request.ClientName.Length > 80 || request.ClientName.Any(char.IsControl)))
            return AgentResponse.Error("invalid_arguments", "Supply a client name of at most 80 characters without control characters.");
        lock (_gate)
        {
            foreach (var expired in _mcpPeers.Values.Where(p => !IsPresent(p)).Select(p => p.Id).ToArray()) _mcpPeers.Remove(expired);
            if (request.Method == "client.disconnect") _mcpPeers.Remove(id);
            else
            {
                if (!_mcpPeers.ContainsKey(id) && _mcpPeers.Count >= 32)
                    return AgentResponse.Error("client_limit", "Too many active MCP connections.");
                _mcpPeers[id] = new(id, request.ClientName!, _timeProvider.GetUtcNow());
            }
        }
        if (McpPeersChanged is { } changed)
            foreach (Action handler in changed.GetInvocationList())
                try { handler(); }
                catch (Exception) { /* Presence observers must not disrupt IPC. */ }
        return AgentResponse.Ok(new { connected = request.Method == "client.heartbeat" });
    }
}
