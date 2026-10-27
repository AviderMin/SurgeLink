using SurgeLink.Core.Files;

namespace SurgeLink.Core.Tests;

public sealed class FileExperienceEditorBufferTests
{
    [Fact]
    public void CrlfFindSelectionSurvivesDocumentStateRoundTrip()
    {
        const string document = "first\nsecond\ntarget\nlast";
        var editorText = EditorTextBuffer.ToEditorText(document);
        var editorMatch = LiteralTextSearch.Find(editorText, "target", 0, false, true);

        var documentMatch = EditorTextBuffer.ToDocumentOffset(editorText, editorMatch);

        Assert.Equal(document.IndexOf("target", StringComparison.Ordinal), documentMatch);
        Assert.Equal(editorMatch, EditorTextBuffer.ToEditorOffset(document, documentMatch));
        Assert.Equal("target", editorText.Substring(editorMatch, "target".Length));
    }

    [Fact]
    public void CrlfReplaceKeepsUneditedLinesAndFollowingSelectionAligned()
    {
        const string document = "α\n第二行\ntarget\ntarget";
        var editorText = EditorTextBuffer.ToEditorText(document);
        var start = LiteralTextSearch.Find(editorText, "target", 0, false, true);
        var updatedEditor = string.Concat(editorText.AsSpan(0, start), "替换", editorText.AsSpan(start + 6));
        var updatedDocument = EditorTextBuffer.ToDocumentText(updatedEditor);
        var documentCaret = EditorTextBuffer.ToDocumentOffset(updatedEditor, start + 2);

        Assert.Equal("α\n第二行\n替换\ntarget", updatedDocument);
        Assert.Equal(updatedDocument.IndexOf("替换", StringComparison.Ordinal) + 2, documentCaret);
        Assert.Equal(start + 2, EditorTextBuffer.ToEditorOffset(updatedDocument, documentCaret));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a\r\nb\rc\nd", "a\nb\nc\nd")]
    [InlineData("空\r\n😀\r\n", "空\n😀\n")]
    public void EditorRoundTripPreservesUnicodeAndCanonicalDocumentBreaks(string editorText, string expected)
    {
        Assert.Equal(expected, EditorTextBuffer.ToDocumentText(editorText));
        Assert.Equal(editorText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal),
            EditorTextBuffer.ToEditorText(EditorTextBuffer.ToDocumentText(editorText)));
    }

    [Theory]
    [InlineData("a\r\nb", 0, 0)]
    [InlineData("a\r\nb", 1, 1)]
    [InlineData("a\r\nb", 2, 2)]
    [InlineData("a\r\nb", 3, 2)]
    [InlineData("a\r\nb", 4, 3)]
    [InlineData("a\r\nb", 99, 3)]
    public void DocumentOffsetClampsAndDoesNotCountCrAndLfTwice(string text, int editorOffset, int expected)
    {
        Assert.Equal(expected, EditorTextBuffer.ToDocumentOffset(text, editorOffset));
    }

    [Theory]
    [InlineData("a\nb", 2, 3)]
    [InlineData("a\r\nb", 3, 3)]
    [InlineData("a\r\nb", 2, 3)]
    [InlineData("a\rb", 2, 3)]
    public void EditorOffsetExpandsEveryLogicalBreakAndClampsMidCrlf(string text, int documentOffset, int expected)
    {
        Assert.Equal(expected, EditorTextBuffer.ToEditorOffset(text, documentOffset));
    }

    [Fact]
    public void CrlfFindQuerySelectionUsesDocumentLength()
    {
        const string document = "left\nright\nend";
        const string query = "right\r\nend";
        var editorText = EditorTextBuffer.ToEditorText(document);
        var editorStart = LiteralTextSearch.Find(editorText, query, 0, false, true);
        var documentStart = EditorTextBuffer.ToDocumentOffset(editorText, editorStart);
        var documentSelectionLength = EditorTextBuffer.ToDocumentText(query).Length;
        var restoredStart = EditorTextBuffer.ToEditorOffset(document, documentStart);
        var restoredEnd = EditorTextBuffer.ToEditorOffset(document, documentStart + documentSelectionLength);

        Assert.Equal(9, documentSelectionLength);
        Assert.Equal(editorStart, restoredStart);
        Assert.Equal(editorStart + query.Length, restoredEnd);
        Assert.Equal(query, editorText.Substring(restoredStart, restoredEnd - restoredStart));
    }

    [Fact]
    public void MultilineSelectionOffsetsRoundTripAcrossCrlf()
    {
        const string document = "一\n二\n三";
        var editorText = EditorTextBuffer.ToEditorText(document);
        var documentStart = 1;
        var documentLength = 4;
        var editorStart = EditorTextBuffer.ToEditorOffset(document, documentStart);
        var editorEnd = EditorTextBuffer.ToEditorOffset(document, documentStart + documentLength);

        Assert.Equal(1, editorStart);
        Assert.Equal(7, editorEnd);
        Assert.Equal(documentLength, EditorTextBuffer.ToDocumentOffset(editorText, editorEnd) - documentStart);
    }
}

public sealed class FileExperienceLineLayoutTests
{
    [Theory]
    [InlineData("a\r\nb\r\nc", 3)]
    [InlineData("a\rb\rc", 3)]
    [InlineData("a\r\nb\nc", 3)]
    [InlineData("\r\n", 2)]
    public void CrlfAndLegacyMacBreaksEachCountOnce(string text, int expected)
    {
        Assert.Equal(expected, LineLayout.CountLines(text));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc", 4, 2)]
    [InlineData("a\r\nb\r\nc", 7, 3)]
    [InlineData("a\r\nb\r\nc", 99, 3)]
    public void CaretLineMatchesCrlfDocument(string text, int offset, int expected)
    {
        Assert.Equal(expected, LineLayout.LineAt(text, offset));
    }
}
