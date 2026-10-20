using SurgeLink.Core.Files;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class RemoteFileEntryFormatModeTests
{
    [Theory]
    [InlineData(0x1A4, false, "-rw-r--r--")]
    [InlineData(0x1ED, true, "drwxr-xr-x")]
    [InlineData(0x1FF, false, "-rwxrwxrwx")]
    [InlineData(0x000, false, "----------")]
    [InlineData(0x1A0, false, "-rw-r-----")]
    public void FormatModeRendersUnixBits(int mode, bool isDirectory, string expected)
    {
        Assert.Equal(expected, RemoteFileEntry.FormatMode(mode, isDirectory));
    }

    [Fact]
    public void PermissionTextUsesTheRenderedMode()
    {
        var file = new RemoteFileEntry
        {
            Name = "notes.txt",
            FullPath = "/home/alice/notes.txt",
            Kind = RemoteEntryKind.File,
            UnixMode = 0x1A4,
        };

        Assert.Equal("-rw-r--r--", file.PermissionText);
        Assert.False(file.IsDirectory);
    }

    [Fact]
    public void PermissionTextIsEmptyWithoutModeInformation()
    {
        var file = new RemoteFileEntry { Name = "x", FullPath = "/x" };

        Assert.Equal(string.Empty, file.PermissionText);
    }
}