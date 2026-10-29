using SurgeLink.Core.Security;

namespace SurgeLink.Core.Tests;

public sealed class DiagnosticTextTests
{
    [Fact]
    public void RemoteTextCannotForgeLinesOrEmbedTerminalAndBidiControls()
    {
        Assert.Equal("Linux\\u000D\\u000A[INF] forged\\u001B[2J\\u202E\\u2028",
            DiagnosticText.Escape("Linux\r\n[INF] forged\u001b[2J\u202e\u2028"));
        Assert.Equal("正常 diagnostic 🙂", DiagnosticText.Escape("正常 diagnostic 🙂"));
    }
}
