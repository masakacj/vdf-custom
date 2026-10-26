using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class UnifiedExclusionManagerTests {
    [Fact]
    public void SingleFileSearch_IsCaseInsensitive_AndCapsDisplayedRows() {
        var history = Enumerable.Range(0, 1000)
            .Select(i => $@"Y:\Media\VIDEO_{i:D5}.jpg").ToArray();
        var initial = BlacklistManagerVM.SearchExcludedPaths(history, string.Empty);
        Assert.Equal(500, initial.Count);
        Assert.Same(history[0], initial[0]);

        var selected = BlacklistManagerVM.SearchExcludedPaths(history, "video_0099");
        Assert.Equal(10, selected.Count);
        Assert.All(selected, s => Assert.Contains("VIDEO_0099", s));
        Assert.Empty(BlacklistManagerVM.SearchExcludedPaths(history, "not-in-history"));
        Assert.Equal(1000, history.Length);
    }
}
