using System.Diagnostics;

namespace SurgeLink.Agent;

/// <summary>Cancels a helper's work when its exact desktop process exits.</summary>
public static class AgentProcessLifetime
{
    public static async Task<T> RunAsync<T>(int desktopProcessId,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        Process desktop;
        try { desktop = Process.GetProcessById(desktopProcessId); }
        catch (ArgumentException) { throw DesktopExited(desktopProcessId); }
        using (desktop)
        using (var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var stopWatching = new CancellationTokenSource())
        {
            // Keep the process handle for this invocation, so PID reuse cannot
            // silently attach an in-flight request to a replacement process.
            _ = desktop.SafeHandle;
            if (desktop.HasExited) { throw DesktopExited(desktopProcessId); }
            var watching = CancelOnExitAsync(desktop, request, stopWatching.Token);
            try { return await operation(request.Token).ConfigureAwait(false); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && (exception is IOException or OperationCanceledException) && desktop.HasExited)
            {
                throw DesktopExited(desktopProcessId);
            }
            finally
            {
                await stopWatching.CancelAsync().ConfigureAwait(false);
                await watching.ConfigureAwait(false);
            }
        }
    }

    private static async Task CancelOnExitAsync(Process desktop, CancellationTokenSource request, CancellationToken ct)
    {
        try
        {
            await desktop.WaitForExitAsync(ct).ConfigureAwait(false);
            await request.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static AgentDesktopDiscoveryException DesktopExited(int pid) => new("desktop_instance_expired",
        "The SurgeLink desktop has exited. Restart the desktop before retrying. Remote terminal input may be uncertain; observe before retrying and preserve the original operation ID.", [pid]);
}
