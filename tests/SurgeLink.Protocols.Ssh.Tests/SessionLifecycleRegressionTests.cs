using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SessionLifecycleRegressionTests
{
    [Theory]
    [InlineData("Terminal")]
    [InlineData("FileSystem")]
    [InlineData("Monitor")]
    public async Task CapabilityWaitingForSessionLockCannotAppearAfterDisposal(string capability)
    {
        var request = new ConnectionRequest { Host = "unused.test", Username = "user", Password = "unused" };
        var options = new SshProtocolOptions(request);
        var context = new ProtocolHostContext(NullLoggerFactory.Instance,
            new JsonHostKeyStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "hosts.json")));
        var factory = new SshConnectionFactory(request, options, context, NullLogger.Instance);
        using var client = new SshClient("unused.test", "user", "unused");
        await using var session = new SshSession(client, factory, options, SshProtocolPlugin.DefaultDescriptor, NullLogger<SshSession>.Instance);
        var gate = typeof(SshSession).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var result = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { result.SetResult(typeof(SshSession).GetProperty(capability)!.GetValue(session)); }
            catch (Exception ex) { result.SetException(ex); }
        });
        Task disposal;
        try
        {
            lock (gate)
            {
                worker.Start();
                Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
                disposal = session.DisposeAsync().AsTask();
            }
        }
        finally { if (worker.IsAlive) Assert.True(worker.Join(TimeSpan.FromSeconds(5))); }
        await disposal;
        Assert.Null(await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
