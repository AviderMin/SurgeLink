using System.Text;
using SurgeLink.Protocols.Ssh.Internal;
using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class CommandOutputEncodingRegressionTests
{
    [Fact]
    public async Task CaptureKeepsACjkCharacterThatEncodesLikeTheUtf8Bom()
    {
        // 0xE0 0xA0 0xBB is one valid CJK UTF-8 sequence, not a byte-order mark. Only a
        // stream that really starts with the signature may have that prefix removed.
        var payload = new byte[] { 0xE0, 0xA0, 0xBB, (byte)'a' };
        using var input = new MemoryStream(payload);
        var text = await SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.UTF8, 32, default);
        Assert.Equal("\u083B\u0061", text);
    }

    [Fact]
    public async Task CaptureRemovesOnlyTheStreamSignature()
    {
        // The signature itself is dropped; nothing further in the same output may be.
        var payload = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x', (byte)'y' };
        using var input = new MemoryStream(payload);
        var text = await SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.UTF8, 32, default);
        Assert.Equal("xy", text);
    }

    [Fact]
    public async Task CaptureKeepsLatin1TextThatLooksLikeTheUtf8Bom()
    {
        // Latin-1 bytes are arbitrary, so a leading byte pattern must never be read as a
        // signature for an encoding that has no BOM to produce.
        using var input = new MemoryStream(Encoding.Latin1.GetBytes("\u00EF\u00BB\u00BFabc"));
        var text = await SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.Latin1, 32, default);
        Assert.Equal("\u00EF\u00BB\u00BFabc", text);
    }

    [Fact]
    public async Task CapturePreservesUtf16ContentThatBeginsWithItsSignatureCodeUnit()
    {
        // UTF-16 stores the signature as a code unit, so only the signature may be dropped;
        // a generic BOM detector also stripped the first U+FEFF of the decoded content.
        var payload = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("\uFEFFx"))
            .ToArray();
        using var input = new MemoryStream(payload);
        var text = await SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.Unicode, 32, default);
        Assert.Equal("\uFEFFx", text);
    }

    [Fact]
    public async Task MonitorSectionsAfterTheBomPowershellWritesAreStillFound()
    {
        // Windows PowerShell prints the UTF-8 BOM once, before the first marker line, so a
        // decoder that leaves it in place silently loses the cpu section of every sample.
        var output = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("@@@SURGELINK:cpu\n0|5\n@@@SURGELINK:end\n"))
            .ToArray();
        using var input = new MemoryStream(output);
        var sections = MonitorOutput.Split(await SshCommandRunner.ReadBoundedOutputAsync(input, Encoding.UTF8, 256, default));
        Assert.Equal("0|5\n", sections["cpu"]);
        Assert.Contains("end", sections.Keys);
    }
}
