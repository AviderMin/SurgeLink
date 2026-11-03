using Renci.SshNet.Common;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Surfaces Tailscale check-mode banners independently for each SSH transport.</summary>
internal sealed class SshWebAuthentication(
    string endpoint,
    Func<WebAuthenticationChallenge, CancellationToken, Task<bool>> prompt,
    SshConnectBudget? budget,
    CancellationToken cancellationToken)
{
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);

    internal void Handle(object? sender, AuthenticationBannerEventArgs args)
    {
        var uri = FindCheckUri(args.BannerMessage);
        if (uri is null || !_shown.Add(uri.AbsoluteUri)) return;
        cancellationToken.ThrowIfCancellationRequested();
        budget?.Pause();
        try
        {
            var answer = Task.Run(() => prompt(new(endpoint, uri), cancellationToken), CancellationToken.None);
            TaskObservation.ObserveFaults(answer);
            if (!answer.WaitAsync(cancellationToken).GetAwaiter().GetResult())
                throw new ProtocolException("Browser authentication was cancelled.", ProtocolFailureKind.Cancelled);
        }
        finally { budget?.Resume(); }
    }

    internal static Uri? FindCheckUri(string banner)
    {
        // Do not automatically turn arbitrary server text into a launchable URL.
        foreach (var word in banner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(word.Trim('"', '\'', '(', ')', '<', '>'), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0
                && uri.Host.Equals("login.tailscale.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/a/", StringComparison.Ordinal) && uri.AbsolutePath.Length > 3)
                return uri;
        }
        return null;
    }
}
