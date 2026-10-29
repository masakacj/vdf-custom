using VDF.Core;
using VDF.Core.Utils;

namespace VDF.Core.Tests;

public sealed class ManualExclusionPageTests {
    [Fact]
    public void LargeHistory_OnlyReturnsRequestedPageAndTotalCount() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-exclusion-page");
        var entries = new List<FileEntry>(40_000);
        for (int i = 0; i < 40_000; i++)
            entries.Add(new FileEntry {
                Path = Path.Combine(root, $"FILE-{i:D6}.mp4"),
                Flags = i % 2 == 0 ? EntryFlags.ManuallyExcluded : EntryFlags.MetadataError
            });
        var first = DatabaseUtils.FindManuallyExcludedPage(entries, "", 0, 200);
        var next = DatabaseUtils.FindManuallyExcludedPage(entries, "", 200, 200);
        Assert.Equal(20_000, first.TotalCount);
        Assert.Equal(20_000, next.TotalCount);
        Assert.Equal(200, first.Paths.Count);
        Assert.Equal(200, next.Paths.Count);
        Assert.Equal(Path.Combine(root, "FILE-000400.mp4"), next.Paths[0]);
    }

    [Fact]
    public void Search_MatchesCaseInsensitiveAndNeverChangesFlags() {
        string root = Path.GetTempPath();
        var a = new FileEntry { Path = Path.Combine(root, "VIDEO-A.MKV"), Flags = EntryFlags.ManuallyExcluded };
        var b = new FileEntry { Path = Path.Combine(root, "other.mp4"), Flags = EntryFlags.ManuallyExcluded };
        var c = new FileEntry { Path = Path.Combine(root, "video-c.mkv"), Flags = EntryFlags.MetadataError };
        var result = DatabaseUtils.FindManuallyExcludedPage(new[] { a, b, c }, "video-", 0, 200);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(a.Path, Assert.Single(result.Paths));
        Assert.True(a.Flags.Has(EntryFlags.ManuallyExcluded));
        Assert.True(b.Flags.Has(EntryFlags.ManuallyExcluded));
    }

    [Fact]
    public void InvalidPageArguments_DoNotEnumerateDatabase() {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DatabaseUtils.FindManuallyExcludedPage(Array.Empty<FileEntry>(), "", -1, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DatabaseUtils.FindManuallyExcludedPage(Array.Empty<FileEntry>(), "", 0, 1000));
    }
}
