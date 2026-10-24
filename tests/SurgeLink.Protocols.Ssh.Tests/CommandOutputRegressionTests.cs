using System.Text;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class CommandOutputRegressionTests
{
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task CaptureRejectsOversizedOutputRatherThanReturningPartialData(int length)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', length)));
        if (length > 32)
            await Assert.ThrowsAsync<IOException>(() => SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.UTF8, 32, default));
        else
            Assert.Equal(new string('x', length), await SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.UTF8, 32, default));
    }

    [Fact]
    public async Task CapturePreservesMultibyteOutputAndHonorsCancellation()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("你好🙂"));
        Assert.Equal("你好🙂", await SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.UTF8, 32, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.UTF8, 32, new CancellationToken(true)));
    }
}
