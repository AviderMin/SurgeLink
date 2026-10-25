using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet.Common;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshAuthenticationInteractionTests
{
    [Fact]
    public void PasswordFallbackAnswersOnlyThePasswordAndAsksForMfa()
    {
        var factory = CreateFactory();
        var questions = new List<string>();
        var prompts = new[] { new AuthenticationPrompt(0, false, "Password:"), new AuthenticationPrompt(1, false, "Verification code:") };
        var args = new AuthenticationPromptEventArgs("alice", "", "", prompts);
        factory.AnswerPrompts(args, (question, _) => { questions.Add(question.Question); return Task.FromResult<string?>("123456"); }, "existing-password", CancellationToken.None);
        Assert.Equal("existing-password", prompts[0].Response);
        Assert.Equal("123456", prompts[1].Response);
        Assert.Equal(new[] { "Verification code:" }, questions);
    }

    [Fact]
    public void CancelledMfaDoesNotReceiveTheSavedPassword()
    {
        var prompts = new[] { new AuthenticationPrompt(0, false, "One-time password:") };
        var error = Assert.Throws<ProtocolException>(() => CreateFactory().AnswerPrompts(new AuthenticationPromptEventArgs("alice", "", "", prompts),
            (_, _) => Task.FromResult<string?>(null), "saved-password", CancellationToken.None));
        Assert.Equal(ProtocolFailureKind.Cancelled, error.Kind);
        Assert.Null(prompts[0].Response);
    }

    [Fact]
    public void PureInteractiveAuthenticationStillAsksForPassword()
    {
        var prompts = new[] { new AuthenticationPrompt(0, false, "Password:") };
        var calls = 0;
        CreateFactory().AnswerPrompts(new AuthenticationPromptEventArgs("alice", "", "", prompts),
            (_, _) => { calls++; return Task.FromResult<string?>("typed-password"); }, null, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("typed-password", prompts[0].Response);
    }

    [Fact]
    public async Task NetworkBudgetPausesForHumanInputButExplicitCancellationStillWorks()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new SshConnectBudget(TimeSpan.FromMilliseconds(200), cancellation.Token);
        budget.Pause();
        await Task.Delay(400);
        Assert.False(budget.IsCancellationRequested);
        cancellation.Cancel();
        Assert.True(budget.IsCancellationRequested);
    }

    [Fact]
    public async Task NetworkDeadlineResumesAfterThePrompt()
    {
        using var budget = new SshConnectBudget(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        budget.Pause();
        await Task.Delay(300);
        budget.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(5), budget.Token));
    }

    private static SshConnectionFactory CreateFactory()
    {
        var request = new ConnectionRequest { Host = "unused", Username = "alice", Password = "saved" };
        var context = new ProtocolHostContext(NullLoggerFactory.Instance,
            new JsonHostKeyStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "hosts.json")));
        return new SshConnectionFactory(request, new SshProtocolOptions(request), context, NullLogger.Instance);
    }
}
