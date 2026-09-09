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

    [Fact]
    public void LookupEntry_ForDeletedPath_DoesNotStatTheMissingFile() {
        string path = Path.Combine(Path.GetTempPath(), "vdf-journal-missing", Guid.NewGuid().ToString("N"), "gone.mkv");
        Assert.False(File.Exists(path));

        FileEntry entry = ScanEngine.CreateInteractiveLookupEntry(path);

        Assert.Equal(Path.GetFullPath(path), entry.Path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CompactRewrite_ReplacesJournalCompletely_AndLeavesNoTempFile() {
        string dir = Path.Combine(Path.GetTempPath(), "vdf-journal-compact", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string journal = Path.Combine(dir, "ScannedFiles.interactive.log");
        try {
            File.WriteAllText(journal, "old-record\n", Encoding.UTF8);
            string[] required = {
                $"D\t{B64(Path.Combine(dir, "gone.mkv"))}",
                $"M\t{B64(Path.Combine(dir, "old.mkv"))}\t{B64(Path.Combine(dir, "new.mkv"))}",
            };

            ScanEngine.RewriteInteractiveJournalSafely(journal, required);

            Assert.Equal(required, File.ReadAllLines(journal, Encoding.UTF8));
            Assert.False(File.Exists(journal + ".compact.tmp"));
        }
        finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
