using SurgeLink.App.Services;
using Xunit;

namespace SurgeLink.Core.Tests;

public sealed class AddressMaskerTests
{
    [Theory]
    [InlineData("root@192.0.2.10:22", "192.0.2.10", "root@***:22")]
    [InlineData("root@EXAMPLE.com:22", "example.com", "root@***:22")]
    [InlineData("root@[2001:db8::1]:22", "2001:db8::1", "root@[***]:22")]
    [InlineData("serveruser@server", "server", "serveruser@***")]
    [InlineData("example.com.evil", "example.com", "example.com.evil")]
    [InlineData("Error connecting to example.com (example.com)", "example.com", "Error connecting to *** (***)")]
    public void MasksEndpointsWithoutChangingOtherText(string text, string host, string expected) =>
        Assert.Equal(expected, AddressMasker.Mask(text, [host]));

    [Fact]
    public void HandlesOverlappingAndEmptyHosts() =>
        Assert.Equal("*** ***", AddressMasker.Mask("db.example.com example.com", ["", "example.com", "db.example.com"]));

    [Fact]
    public void NoHostsLeavesTextUnchanged() =>
        Assert.Equal("Ready", AddressMasker.Mask("Ready", []));
}
