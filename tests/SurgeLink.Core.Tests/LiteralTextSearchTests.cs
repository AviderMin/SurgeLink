using SurgeLink.Core.Files;

namespace SurgeLink.Core.Tests;

public sealed class LiteralTextSearchTests
{
    [Theory]
    [InlineData("one two one", "one", 3, false, true, 8)]
    [InlineData("one two one", "one", 11, false, true, 0)]
    [InlineData("one two one", "one", 7, true, true, 0)]
    [InlineData("one two one", "one", -1, true, true, 8)]
    [InlineData("AbC abc", "abc", 0, false, false, 0)]
    [InlineData("AbC abc", "abc", 0, false, true, 4)]
    [InlineData("hello", "missing", 0, false, true, -1)]
    [InlineData("hello", "", 0, false, true, -1)]
    [InlineData("", "hello", 0, true, false, -1)]
    [InlineData("aaaa", "aa", 1, true, true, 1)]
    [InlineData("甲乙甲", "甲", 1, false, false, 2)]
    [InlineData("line1\nline2", "line2", 0, false, true, 6)]
    public void FindsLiteralMatchesWithWraparound(string text, string query, int start, bool previous, bool matchCase, int expected)
    {
        Assert.Equal(expected, LiteralTextSearch.Find(text, query, start, previous, matchCase));
    }
}
