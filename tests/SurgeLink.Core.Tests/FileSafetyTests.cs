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
    [Theory]
    [InlineData(65001, false)]
    [InlineData(65001, true)]
    [InlineData(1200, true)]
    [InlineData(1201, true)]
    [InlineData(12000, true)]
    [InlineData(12001, true)]
    public async Task EditingPreservesEncodingAndBom(int codePage, bool bom)
    {
        Encoding encoding = codePage switch
        {
            65001 => new UTF8Encoding(bom, true),
            1200 => new UnicodeEncoding(false, bom, true),
            1201 => new UnicodeEncoding(true, bom, true),
            12000 => new UTF32Encoding(false, bom, true),
            _ => new UTF32Encoding(true, bom, true),
        };
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes("中文 😀\r\n")];
        using var stream = new MemoryStream(original);
        var document = await BoundedTextFile.ReadDocumentAsync(stream, 128, CancellationToken.None);
        Assert.Equal("中文 😀\r\n", document.Text);
        Assert.Equal(original, document.Encode(document.Text));
        Assert.Equal<byte>([.. encoding.GetPreamble(), .. encoding.GetBytes("修改")], document.Encode("修改"));
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xfe, 0x41 })]
    [InlineData(new byte[] { 0, 0, 0xfe, 0xff, 0 })]
    public async Task MalformedBomEncodedTextIsRejected(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => BoundedTextFile.ReadDocumentAsync(stream, 32, CancellationToken.None));
    }
}
