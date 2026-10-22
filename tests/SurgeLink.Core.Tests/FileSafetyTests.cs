using System.Text;
using SurgeLink.Core.Files;

namespace SurgeLink.Core.Tests;

public sealed class FileSafetyTests
{
    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("/etc/file")]
    [InlineData("file:stream")]
    [InlineData("a/b")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("LPT\u00b2.txt")]
    [InlineData("CON .txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("..")]
    public void UnsafeWindowsDownloadNamesAreRejected(string name)
    {
        using var temp = new TempDirectory();
        Assert.Throws<IOException>(() => LocalDownloadPath.Resolve(temp.Root, name));
    }

    [Fact]
    public void UnicodeNameStaysInsideDestination()
    {
        using var temp = new TempDirectory();
        Assert.Equal(temp.File("报告.txt"), LocalDownloadPath.Resolve(temp.Root, "报告.txt"));
    }

    [Fact]
    public async Task TextLimitUsesActualBytes()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("12345"));
        await Assert.ThrowsAsync<IOException>(() => BoundedTextFile.ReadAsync(stream, 4, CancellationToken.None));
    }

    [Fact]
    public async Task TextAtLimitIsAccepted()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("文本"));
        Assert.Equal("文本", await BoundedTextFile.ReadAsync(stream, 6, CancellationToken.None));
    }

    [Theory]
    [InlineData(new byte[] { 255, 255, 255 })]
    [InlineData(new byte[] { 65, 0, 66 })]
    public async Task InvalidOrBinaryTextIsRejected(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        var failure = await Record.ExceptionAsync(() => BoundedTextFile.ReadAsync(stream, 32, CancellationToken.None));
        Assert.True(failure is IOException or DecoderFallbackException);
    }

    [Fact]
    public async Task CancelledReadIsNotCompleted()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(new byte[1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedTextFile.ReadAsync(stream, 4, cancellation.Token));
    }
}
