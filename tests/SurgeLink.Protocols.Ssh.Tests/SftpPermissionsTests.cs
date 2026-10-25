using System.Reflection;
using Renci.SshNet.Sftp;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SftpPermissionsTests
{
    [Theory]
    [InlineData(0x000)]
    [InlineData(0x180)]
    [InlineData(0x1A4)]
    [InlineData(0x1C0)]
    [InlineData(0x1ED)]
    [InlineData(0xFFF)]
    public void BitMaskSurvivesTheActualSshNetPermissionApi(int mode)
    {
        // The SDK has no public attribute constructor; use its wire-attribute
        // constructor to exercise SetPermissions without a network dependency.
        var constructor = typeof(SftpFileAttributes).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var attributes = (SftpFileAttributes)constructor.Invoke(
            [DateTime.UnixEpoch, DateTime.UnixEpoch, 0L, 1000, 1000, (uint)0x8000, new Dictionary<string, string>()]);
        attributes.SetPermissions(SftpFileSystem.ToSftpPermissionMode(mode));
        Assert.Equal((mode & 0x100) != 0, attributes.OwnerCanRead);
        Assert.Equal((mode & 0x080) != 0, attributes.OwnerCanWrite);
        Assert.Equal((mode & 0x040) != 0, attributes.OwnerCanExecute);
        Assert.Equal((mode & 0x020) != 0, attributes.GroupCanRead);
        Assert.Equal((mode & 0x010) != 0, attributes.GroupCanWrite);
        Assert.Equal((mode & 0x008) != 0, attributes.GroupCanExecute);
        Assert.Equal((mode & 0x004) != 0, attributes.OthersCanRead);
        Assert.Equal((mode & 0x002) != 0, attributes.OthersCanWrite);
        Assert.Equal((mode & 0x001) != 0, attributes.OthersCanExecute);
        Assert.Equal((mode & 0x800) != 0, attributes.IsUIDBitSet);
        Assert.Equal((mode & 0x400) != 0, attributes.IsGroupIDBitSet);
        Assert.Equal((mode & 0x200) != 0, attributes.IsStickyBitSet);
    }
}
