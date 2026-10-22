using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;

namespace SurgeLink.Core.Tests;

public sealed class RemoteSystemIconTests
{
    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("Unknown host", "unknown")]
    [InlineData("Windows Server 2025", "windows")]
    [InlineData("Darwin 25.0.0", "apple")]
    [InlineData("macOS 15", "apple")]
    [InlineData("Ubuntu 24.04.3 LTS", "ubuntu")]
    [InlineData("Debian GNU/Linux 13", "debian")]
    [InlineData("Fedora Linux 43", "fedora")]
    [InlineData("CentOS Stream 10", "centos")]
    [InlineData("Rocky Linux 10.0", "rockylinux")]
    [InlineData("AlmaLinux 10", "almalinux")]
    [InlineData("Arch Linux", "archlinux")]
    [InlineData("openSUSE Leap 16", "opensuse")]
    [InlineData("Alpine Linux v3.22", "alpinelinux")]
    [InlineData("Kali GNU/Linux Rolling", "kalilinux")]
    [InlineData("FreeBSD 14.3-RELEASE", "freebsd")]
    [InlineData("Red Hat Enterprise Linux 9", "redhat")]
    [InlineData("Linux Mint 22", "linuxmint")]
    [InlineData("SurgeLink Demo Linux 6.6", "linux")]
    [InlineData("Research UNIX", "unknown")]
    public void ResolvesOnlyRecognizedSystemNames(string? name, string expected) =>
        Assert.Equal(expected, RemoteSystemIcon.Resolve(name));

    [Fact]
    public void DetectedSystemSurvivesSavingAndReloading()
    {
        using var temp = new TempDirectory();
        var path = temp.File("sessions.json");
        var profile = SessionProfile.New("ssh", "example.test", 22, "alice") with
        {
            DetectedOperatingSystem = "Ubuntu 24.04 LTS",
        };
        new JsonSessionStore(path).Upsert(profile);
        var reloaded = Assert.Single(new JsonSessionStore(path).Load());
        Assert.Equal(profile.DetectedOperatingSystem, reloaded.DetectedOperatingSystem);
        Assert.Equal("ubuntu", RemoteSystemIcon.Resolve(reloaded.DetectedOperatingSystem));
    }

    [Fact]
    public void OlderProfileWithoutSystemRemainsCompatible()
    {
        using var temp = new TempDirectory();
        var path = temp.File("sessions.json");
        File.WriteAllText(path, """[{"Id":"old","Name":"Old","ProtocolId":"ssh","Host":"example.test","Username":"alice"}]""");
        var profile = Assert.Single(new JsonSessionStore(path).Load());
        Assert.Null(profile.DetectedOperatingSystem);
        Assert.Equal("unknown", RemoteSystemIcon.Resolve(profile.DetectedOperatingSystem));
    }
}
