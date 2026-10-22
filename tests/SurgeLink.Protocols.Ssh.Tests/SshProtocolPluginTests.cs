using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshProtocolPluginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentServerCanBeCancelledOrTimesOut(bool cancel)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var request = new ConnectionRequest
        {
            Host = "127.0.0.1",
            Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port,
            Username = "alice",
            Password = "test-only",
            ConnectTimeout = cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(300),
        };
        using var cancellation = new CancellationTokenSource();
        var connecting = new SshProtocolPlugin().ConnectAsync(request, CreateContext(), cancellation.Token);
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) cancellation.Cancel();
        var error = await Assert.ThrowsAsync<ProtocolException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancel ? ProtocolFailureKind.Cancelled : ProtocolFailureKind.Timeout, error.Kind);
    }

    [Fact]
    public void DescriptorDescribesTheSshProtocol()
    {
        var descriptor = new SshProtocolPlugin().Descriptor;

        Assert.Equal("ssh", descriptor.Id);
        Assert.Equal("SSH", descriptor.DisplayName);
        Assert.Equal(22, descriptor.DefaultPort);
        Assert.Equal("\uE8CE", descriptor.Glyph);
        Assert.Equal("Secure Shell, SFTP/SCP and remote monitor", descriptor.Description);
        Assert.True(descriptor.Capabilities.HasFlag(SessionCapabilities.Terminal));
        Assert.True(descriptor.Capabilities.HasFlag(SessionCapabilities.FileSystem));
        Assert.True(descriptor.Capabilities.HasFlag(SessionCapabilities.Monitor));
    }

    [Fact]
    public void DefaultDescriptorMatchesTheInstanceDescriptor()
    {
        Assert.Same(SshProtocolPlugin.DefaultDescriptor, new SshProtocolPlugin().Descriptor);
    }

    [Fact]
    public async Task ConnectAsyncRejectsNullRequest()
    {
        var plugin = new SshProtocolPlugin();

        await Assert.ThrowsAsync<ArgumentNullException>(() => plugin.ConnectAsync(null!, CreateContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsyncRejectsNullContext()
    {
        var plugin = new SshProtocolPlugin();
        var request = new ConnectionRequest { Host = "example.com", Port = 22, Username = "alice" };

        await Assert.ThrowsAsync<ArgumentNullException>(() => plugin.ConnectAsync(request, null!, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsyncHonoursAPreCancelledToken()
    {
        var plugin = new SshProtocolPlugin();
        var request = new ConnectionRequest { Host = "example.com", Port = 22, Username = "alice" };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ConnectAsync(request, CreateContext(), cancellation.Token));
    }

    private static ProtocolHostContext CreateContext() =>
        new(NullLoggerFactory.Instance, new InMemoryHostKeyStore());

    private sealed class InMemoryHostKeyStore : IHostKeyStore
    {
        private readonly Dictionary<string, KnownHostEntry> _entries = new(StringComparer.Ordinal);

        public bool TryGetFingerprint(string endpoint, out KnownHostEntry entry)
        {
            if (_entries.TryGetValue(endpoint, out var found))
            {
                entry = found;
                return true;
            }

            entry = null!;
            return false;
        }

        public void Save(KnownHostEntry entry) => _entries[entry.Endpoint] = entry;

        public bool Remove(string endpoint) => _entries.Remove(endpoint);

        public IReadOnlyList<KnownHostEntry> List() => new List<KnownHostEntry>(_entries.Values);
    }
}
