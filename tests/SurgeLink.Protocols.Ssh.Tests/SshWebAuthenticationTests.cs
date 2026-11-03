using Renci.SshNet.Common;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshWebAuthenticationTests
{
    private const string CheckUrl = "https://login.tailscale.com/a/test-check";
    private static AuthenticationBannerEventArgs Banner(string url = CheckUrl) =>
        new("alice", $"# Tailscale SSH requires an additional check.\n# To authenticate, visit: {url}\n", "en");

    [Fact]
    public void CancelledAttemptDoesNotSuppressTheSameLinkOnReconnect()
    {
        var calls = 0;
        Task<bool> Prompt(WebAuthenticationChallenge challenge, CancellationToken ct)
        {
            Assert.Equal("host.test:22", challenge.Endpoint);
            Assert.Equal(CheckUrl, challenge.Uri.AbsoluteUri);
            return Task.FromResult(++calls > 1);
        }
        var first = new SshWebAuthentication("host.test:22", Prompt, null, default);
        var error = Assert.Throws<ProtocolException>(() => first.Handle(null, Banner()));
        Assert.Equal(ProtocolFailureKind.Cancelled, error.Kind);
        var retry = new SshWebAuthentication("host.test:22", Prompt, null, default);
        retry.Handle(null, Banner());
        retry.Handle(null, Banner());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("http://login.tailscale.com/a/test")]
    [InlineData("https://login.tailscale.com.evil.example/a/test")]
    [InlineData("https://evil.example/a/test")]
    [InlineData("https://user@login.tailscale.com/a/test")]
    [InlineData("https://login.tailscale.com:444/a/test")]
    [InlineData("https://login.tailscale.com/")]
    [InlineData("Welcome to SSH")]
    public void UnrelatedOrUnsafeBannersDoNotRequestBrowserAuthentication(string banner)
    {
        var handler = new SshWebAuthentication("host:22", (_, _) => throw new InvalidOperationException("Unexpected prompt"), null, default);
        handler.Handle(null, Banner(banner));
    }

    [Fact]
    public async Task BrowserWaitPausesNetworkTimeoutAndStillObservesCancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var budget = new SshConnectBudget(TimeSpan.FromSeconds(1), cancel.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SshWebAuthentication("host:22", async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }, budget, budget.Token);
        var pending = Task.Run(() => handler.Handle(null, Banner()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.False(budget.IsCancellationRequested);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task NetworkBudgetResumesAfterBrowserPrompt()
    {
        using var budget = new SshConnectBudget(TimeSpan.FromMilliseconds(200), default);
        var handler = new SshWebAuthentication("host:22", async (_, ct) =>
        {
            await Task.Delay(300, ct);
            return true;
        }, budget, budget.Token);
        handler.Handle(null, Banner());
        Assert.False(budget.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(5), budget.Token));
    }
}
