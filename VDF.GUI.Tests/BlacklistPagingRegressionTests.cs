using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public sealed class BlacklistPagingRegressionTests {
    [Fact]
    public void TenThousandGroups_OnlyMaterializesOneHundredEntryModelsOnOpen() {
        var groups = Enumerable.Range(0, 10_000)
            .Select(n => new HashSet<string> { Path.Combine(Path.GetTempPath(), $"group-{n:D5}.mp4") })
            .ToList();
        var manager = new BlacklistManagerVM(groups, () => Task.CompletedTask);
        Assert.Equal(10_000, groups.Count);
        Assert.Equal(100, manager.Entries.Count);
        Assert.True(manager.CanNextGroupPage);
        Assert.False(manager.CanPreviousGroupPage);
        Assert.Contains("10,000", manager.GroupPageSummary);
    }

    [Fact]
    public void SingleHugeGroup_PreViewsOnlyThreePathsInsteadOfJoiningThousands() {
        var giant = Enumerable.Range(0, 50_000).Select(n =>
            Path.Combine(Path.GetTempPath(), $"subfolder-{n:D5}", "episode.mkv")).ToHashSet();
        var entry = new BlacklistEntryVM(giant);
        Assert.Equal(50_000, entry.Count);
        Assert.True(entry.PathsDisplay.Length < 800);
        Assert.Contains("49,997", entry.PathsDisplay);
        Assert.DoesNotContain("subfolder-49999", entry.PathsDisplay);
    }

    [Fact]
    public void SearchAndPages_OnlyReturnMatchingRows() {
        var groups = Enumerable.Range(0, 10_000).Select(n =>
            new HashSet<string> {
                Path.Combine(Path.GetTempPath(), n % 7 == 0 ? "special" : "ordinary", $"ep-{n:D5}.mp4")
            }).ToList();
        var page0 = BlacklistManagerVM.GroupPageOf(groups, "SPECIAL", 0);
        var page1 = BlacklistManagerVM.GroupPageOf(groups, "SPECIAL", 1);
        Assert.Equal(100, page0.Rows.Count);
        Assert.True(page0.HasNext);
        Assert.Equal(100, page1.Rows.Count);
        Assert.All(page0.Rows.Concat(page1.Rows), x =>
            Assert.Contains("special", x.PathsDisplay, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(page0.Rows[0].Source, page1.Rows.Select(x => x.Source));
    }

    [Fact]
    public void FolderPages_DoNotAllocateAllRules() {
        var input = Enumerable.Range(0, 40_000).Select(n => $"C:\\Media\\{n:D6}").ToArray();
        var page0 = BlacklistManagerVM.PageOf(input, "", x => x, 0, 100);
        var page200 = BlacklistManagerVM.PageOf(input, "", x => x, 200, 100);
        Assert.Equal(100, page0.Rows.Count);
        Assert.Equal(100, page200.Rows.Count);
        Assert.Equal(input[20_000], page200.Rows[0]);
        Assert.True(page200.HasNext);
    }
}
