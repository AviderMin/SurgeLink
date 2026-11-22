using SurgeLink.Core.Files;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SftpDepthLimitTests
{
    [Fact]
    public void RecursiveDeleteAcceptsEveryDepthTheCopierCreates()
    {
        // A copy that succeeds must be removable by the rollback of the same connection.
        // These bounds diverged before (copy 128, delete 64), so a failure below level 64
        // left a staging tree that its own cleanup was not allowed to delete.
        Assert.Equal(RemoteFileLimits.MaximumDepth, SftpFileSystem.MaximumDeleteDepth);
        Assert.True(SftpFileSystem.MaximumDeleteDepth >= 64);
    }
}