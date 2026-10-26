using System.Security.Cryptography;
using SurgeLink.Agent;

namespace SurgeLink.AgentHost;

/// <summary>Re-discovers the desktop for each call, including after restarts. Presence never grants access.</summary>
public sealed class DesktopConnection(int? instance = null, Func<int>? resolveDesktop = null)
{
    private readonly Func<int> _resolveDesktop = resolveDesktop ?? (() => AgentEndpoint.ResolveDesktopProcess(instance));
    private readonly string _clientId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _clientName = "MCP";
    private int _lastDesktop;

    public void ClientConnected(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !name.Any(char.IsControl))
            Volatile.Write(ref _clientName, name);
        _connected.TrySetResult();
    }

    public async Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct)
    {
        try
        {
            var desktop = _resolveDesktop();
            return await new AgentClient(AgentEndpoint.ForProcess(desktop)).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return AgentResponse.Error("cancelled", "The request was cancelled or expired."); }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Discovery messages identify missing/ambiguous desktops without leaking remote payloads.
            return AgentResponse.Error("desktop_unavailable", "Cannot reach one SurgeLink desktop. Keep the intended desktop running; if several are open, copy its instance-specific MCP configuration.");
        }
    }

    public async Task RunPresenceAsync(CancellationToken ct)
    {
        try
        {
            await _connected.Task.WaitAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            do
            {
                try
                {
                    var desktop = _resolveDesktop();
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(4));
                    var result = await new AgentClient(AgentEndpoint.ForProcess(desktop)).SendAsync(
                        new("client.heartbeat", ClientId: _clientId, ClientName: Volatile.Read(ref _clientName)), budget.Token).ConfigureAwait(false);
                    if (result.Success) Volatile.Write(ref _lastDesktop, desktop);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or PlatformNotSupportedException)
                { /* Desktop restart/offline is recoverable; tool discovery must still succeed. */ }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            var desktop = Volatile.Read(ref _lastDesktop);
            if (desktop > 0)
            {
                try
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await new AgentClient(AgentEndpoint.ForProcess(desktop)).SendAsync(
                        new("client.disconnect", ClientId: _clientId), budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
            }
        }
    }
}
