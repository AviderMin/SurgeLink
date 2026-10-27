using SurgeLink.Core.Files;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class AgentExperienceTests
{
    [Fact]
    public async Task OversizedDirectoryReadReturnsAnActionableTransportError()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(true));
        var entries = Enumerable.Range(0, 500).Select(i => new RemoteFileEntry
        {
            Name = new string('界', 900), FullPath = "/日志/" + new string('界', 900),
        }).ToArray();
        var session = new AgentGatewayTests.TestSession { FileSystem = new LargeDirectory(entries) };
        var handle = gateway.GrantSession("tab", "大目录", session, null);
        var name = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var server = new AgentPipeServer(name, gateway);

        var response = await new AgentClient(name).SendAsync(new("files.list", handle, "/日志"), default);
        Assert.False(response.Success);
        Assert.Equal("output_limit", response.ErrorCode);
        Assert.Contains("1 MiB", response.Message);
        Assert.Contains("smaller file/directory", response.Message);
    }

    [Fact]
    public async Task ScreenTruncationNeverSplitsUnicodeSurrogatePairs()
    {
        var gateway = new AgentGateway();
        var text = new string('a', 32767) + "😀" + new string('b', 100);
        var handle = gateway.GrantSession("tab", "Unicode", new AgentGatewayTests.TestSession(), () => text);

        var response = await gateway.ExecuteAsync(new("terminal.screen", handle), default);
        var data = response.Data!.Value;
        Assert.True(data.GetProperty("truncated").GetBoolean());
        var visible = data.GetProperty("text").GetString()!;
        Assert.Equal(32767, visible.Length);
        Assert.DoesNotContain('\uFFFD', visible);
        Assert.False(char.IsHighSurrogate(visible[^1]));
        Assert.Equal(text[..32767], visible);
    }

    private sealed class LargeDirectory(IReadOnlyList<RemoteFileEntry> entries) : IRemoteFileSystem
    {
        public bool SupportsPermissions => false;
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => Task.FromResult(entries);
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(string source, string destination, CancellationToken ct) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => throw new NotSupportedException();
    }
}
