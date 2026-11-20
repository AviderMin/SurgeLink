using System.Text;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class AgentTextTests
{
    [Fact]
    public void HeadKeepsTheRequestedNumberOfAsciiBytes()
    {
        var trimmed = AgentText.TrimHead(new string('a', 100), stripAnsi: false, maxBytes: 10, maxLines: null);

        Assert.Equal(10, trimmed.Text.Length);
        Assert.Equal(10, trimmed.KeptCharacters);
        Assert.True(trimmed.Truncated);
    }

    [Fact]
    public void TailKeepsTheLastBytesInsteadOfDroppingOnlyThePrefix()
    {
        var trimmed = AgentText.TrimTail(new string('a', 100), stripAnsi: false, maxBytes: 10, maxLines: null);

        Assert.Equal(10, trimmed.Text.Length);
        Assert.Equal(10, trimmed.KeptCharacters);
        Assert.True(trimmed.Truncated);
        Assert.Equal(100, trimmed.OriginalCharacters);
    }

    [Fact]
    public void HeadAndTailDoNotSplitMultiByteCharacters()
    {
        const string text = "中A😀B"; // 3 + 1 + 4 + 1 bytes

        var first = AgentText.TrimHead(text, stripAnsi: false, maxBytes: 2, maxLines: null);
        Assert.Equal("中", first.Text);
        Assert.Equal(1, first.KeptCharacters);

        var last = AgentText.TrimTail(text, stripAnsi: false, maxBytes: 2, maxLines: null);
        Assert.Equal("B", last.Text);

        var pair = AgentText.TrimHead("😀B", stripAnsi: false, maxBytes: 2, maxLines: null);
        Assert.Equal("😀", pair.Text);
        Assert.Equal(2, pair.KeptCharacters);
        Assert.False(char.IsHighSurrogate(pair.Text[0]) && pair.Text.Length == 1);
    }

    [Fact]
    public void HeadKeepsPrefixLinesAndTailKeepsSuffixLines()
    {
        const string text = "one\ntwo\nthree\nfour\n";

        Assert.Equal("one\ntwo\n", AgentText.TrimHead(text, false, maxBytes: null, maxLines: 2).Text);
        Assert.Equal("three\nfour", AgentText.TrimTail(text, false, maxBytes: null, maxLines: 2).Text);
    }

    [Fact]
    public void AnsiIsRemovedAfterTheRawCutSoCursorUnitsStayStable()
    {
        const string text = "\u001b[31mabcdef\u001b[0m";

        var trimmed = AgentText.TrimHead(text, stripAnsi: true, maxBytes: 6, maxLines: null);

        Assert.Equal("a", trimmed.Text);
        Assert.Equal(6, trimmed.KeptCharacters);
    }

    [Fact]
    public void LeadingEchoIsRemovedOnlyNearTheStart()
    {
        Assert.Equal("tail", AgentText.StripLeadingEcho("echo hi\r\ntail", "echo hi"));
        Assert.Equal("x", AgentText.StripLeadingEcho("xecho hi", "echo hi"));
        var lateEcho = new string('a', 70) + "echo hi";
        Assert.Equal(lateEcho, AgentText.StripLeadingEcho(lateEcho, "echo hi"));
    }

    [Fact]
    public async Task FilePageEndsOnACharacterBoundaryAndContinues()
    {
        var bytes = Encoding.UTF8.GetBytes("中A");

        var first = await AgentFilePage.ReadAsync(new MemoryStream(bytes), 0, 2, default);
        Assert.Equal("中", first.Text);
        Assert.Equal(3, first.BytesRead);
        Assert.Equal(0, first.SkippedBytes);
        Assert.True(first.Truncated);

        var second = await AgentFilePage.ReadAsync(new MemoryStream(bytes), first.BytesRead, 1, default);
        Assert.Equal("A", second.Text);
        Assert.Equal(1, second.BytesRead);
        Assert.False(second.Truncated);
    }

    [Fact]
    public async Task FilePageAlignsForwardFromAnOffsetInsideACharacter()
    {
        var bytes = Encoding.UTF8.GetBytes("中A");

        var page = await AgentFilePage.ReadAsync(new MemoryStream(bytes), 1, 4, default);

        Assert.Equal("A", page.Text);
        Assert.Equal(2, page.SkippedBytes);
        Assert.Equal(3, page.BytesRead);
        Assert.Equal(4, 1 + page.BytesRead);
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task FilePageKeepsASurrogatePairTogether()
    {
        var bytes = Encoding.UTF8.GetBytes("😀B");

        var page = await AgentFilePage.ReadAsync(new MemoryStream(bytes), 0, 2, default);

        Assert.Equal("😀", page.Text);
        Assert.Equal(4, page.BytesRead);
        Assert.True(page.Truncated);
    }

    [Fact]
    public async Task TruncatedFilePageDoesNotDecodeAPartialTrailingCharacter()
    {
        var bytes = Encoding.UTF8.GetBytes("ab中");

        var page = await AgentFilePage.ReadAsync(new MemoryStream(bytes), 0, 3, default);

        Assert.Equal("ab", page.Text);
        Assert.Equal(2, page.BytesRead);
        Assert.True(page.Truncated);

        var rest = await AgentFilePage.ReadAsync(new MemoryStream(bytes), page.BytesRead, 3, default);
        Assert.Equal("中", rest.Text);
        Assert.Equal(3, rest.BytesRead);
        Assert.False(rest.Truncated);
    }

    [Fact]
    public async Task InvalidUtf8IsReportedInsteadOfBeingCut()
    {
        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            AgentFilePage.ReadAsync(new MemoryStream(new byte[] { 0xFF, 0x41 }), 0, 8, default));
    }

    [Fact]
    public void ReplayCacheEvictsBySerializedSizeAsWellAsCount()
    {
        var cache = new InputReplayCache(maximumEntries: 100, maximumBytes: 4_000);
        for (var index = 0; index < 10; index++)
        {
            cache.Remember("op-" + index, "fp", AgentResponse.Ok(new { text = new string('x', 1_000) }));
        }

        Assert.True(cache.Count < 10);
        Assert.InRange(cache.EstimatedBytes, 0, 4_000);
        Assert.False(cache.TryGetValue("op-0", out _));
        Assert.True(cache.TryGetValue("op-9", out var latest));
        Assert.Equal("fp", latest.Fingerprint);
    }
}
