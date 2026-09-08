// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Text;
using VDF.Core;

namespace VDF.Core.Tests;

public class InteractiveDatabaseJournalTests {
    static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    [Fact]
    public void MoveRecord_RoundTripsUnicodeAndSpecialPathCharacters() {
        string oldPath = @"C:\媒体\A	old 名称.mkv";
        string newPath = @"D:\整理后\A new 名称.mkv";
        string line = $"M\t{B64(oldPath)}\t{B64(newPath)}";

        bool parsed = ScanEngine.TryParseInteractiveJournalLine(line, out char op, out string a, out string? b);

        Assert.True(parsed);
        Assert.Equal('M', op);
        Assert.Equal(oldPath, a);
        Assert.Equal(newPath, b);
    }

    [Fact]
    public void DeleteRecord_RoundTripsUnicodePath() {
        string path = @"C:\视频\待删除\剧集 01.mkv";
        string line = $"D\t{B64(path)}";

        bool parsed = ScanEngine.TryParseInteractiveJournalLine(line, out char op, out string a, out string? b);

        Assert.True(parsed);
        Assert.Equal('D', op);
        Assert.Equal(path, a);
        Assert.Null(b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M\tbroken")]
    [InlineData("D\tnot-base64!!!")]
    [InlineData("X\tYWJj")]
    public void TornOrInvalidRecord_IsIgnored(string line) {
        Assert.False(ScanEngine.TryParseInteractiveJournalLine(line, out _, out _, out _));
    }
}
