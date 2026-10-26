using VDF.Core;
using VDF.Core.Utils;

namespace VDF.Core.Tests.Utils;

public class ManualExclusionFlagTests {
    [Fact]
    public void ClearingFlag_OnlyAffectsSelectedEntries_NotMetadataOrHashSet() {
        string folder = Path.Combine(Path.GetTempPath(), "manual-exclusion-test");
        var a = new FileEntry { Path = Path.Combine(folder, "a.jpg"),
            Flags = EntryFlags.ManuallyExcluded | EntryFlags.TooDark, FileSize = 12345 };
        var b = new FileEntry { Path = Path.Combine(folder, "b.jpg"),
            Flags = EntryFlags.ManuallyExcluded };
        var notExcluded = new FileEntry { Path = Path.Combine(folder, "c.jpg"),
            Flags = EntryFlags.MetadataError };
        var entries = new HashSet<FileEntry> { a, b, notExcluded };

        var changed = DatabaseUtils.ClearManuallyExcludedFlags(entries,
            new[] { a.Path, a.Path, notExcluded.Path, Path.Combine(folder, "missing.jpg") });
        Assert.Single(changed);
        Assert.Same(a, changed[0]);
        Assert.False(a.Flags.Has(EntryFlags.ManuallyExcluded));
        Assert.True(a.Flags.Has(EntryFlags.TooDark));
        Assert.Equal(12345, a.FileSize);
        Assert.True(b.Flags.Has(EntryFlags.ManuallyExcluded));
        Assert.True(notExcluded.Flags.Has(EntryFlags.MetadataError));
        Assert.True(entries.Contains(a));
        Assert.Equal(3, entries.Count);
        Assert.Empty(DatabaseUtils.ClearManuallyExcludedFlags(entries, [a.Path]));
    }

    [Fact]
    public void NoSelectedPaths_LeavesOtherExclusionsIntact() {
        var a = new FileEntry { Path = Path.Combine(Path.GetTempPath(), "excluded.jpg"),
            Flags = EntryFlags.ManuallyExcluded };
        var entries = new HashSet<FileEntry> { a };
        Assert.Empty(DatabaseUtils.ClearManuallyExcludedFlags(entries, Array.Empty<string>()));
        Assert.True(a.Flags.Has(EntryFlags.ManuallyExcluded));
    }
}
