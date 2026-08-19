// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class FolderMergeUxRegressionTests {
    static DuplicateItemVM Video(Guid group, string path, long size) => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = group,
            Path = path,
            Folder = MainWindowVM.GetPikPakFolder(path).Replace('/', '\\'),
            SizeLong = size,
            Similarity = 100,
            Duration = TimeSpan.FromMinutes(10),
            FrameSize = "1920x1080",
            FrameSizeInt = 3000,
            BitRateKbs = 8_000,
            Fps = 30,
            Format = "h264",
            AudioChannel = "stereo",
            AudioFormat = "aac",
            AudioBitRateKbs = 192,
            AudioSampleRate = 48_000,
            HdrFormat = string.Empty,
        }
    };

    static string RepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void DifferentFileSizeAlone_GetsLikelyBestButNeverStrictAutoBest() {
        var group = Guid.NewGuid();
        var small = Video(group, @"D:\Series\small.mkv", 100_000_000);
        var large = Video(group, @"E:\Collection\large.mkv", 900_000_000);

        Assert.False(MainWindowVM.TryPickDecisiveQualityWinner(new[] { small, large }, out _));

        BestRecommendation recommendation = MainWindowVM.RecommendBest(new[] { small, large });
        Assert.Same(large, recommendation.Winner);
        Assert.False(recommendation.IsConfirmed);
        Assert.Contains("弱参考", recommendation.Reason);

        var presentation = MainWindowVM.PickDecisiveBestForResults(new[] { small, large });
        Assert.Same(large, presentation.Best);
        Assert.NotNull(presentation.Tooltip);
    }

    [Fact]
    public void TreePreview_UsesExplicitKeepAddAndBestMarkers() {
        string root = Path.GetFullPath(@"D:\MergedSeries");
        var files = new Dictionary<string, (long Bytes, string Marker)>(StringComparer.OrdinalIgnoreCase) {
            [Path.Combine(root, "2026-01", "keep.mkv")] = (1000, "＝ 保留"),
            [Path.Combine(root, "2026-02", "new.mkv")] = (2000, "＋ 新增"),
            [Path.Combine(root, "2026-03", "best.mkv")] = (3000, "↑ BEST替换"),
        };

        string tree = MainWindowVM.BuildOneTreeSection(root, files);

        Assert.Contains("＝ 保留", tree);
        Assert.Contains("＋ 新增", tree);
        Assert.Contains("↑ BEST替换", tree);
        Assert.Contains("2026-01", tree);
        Assert.Contains("2026-03", tree);
    }

    [Fact]
    public void ManualReviewPreview_OffersConfirmAllAndClearAll() {
        string root = RepoRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "VDF.GUI", "Views", "ResourceConsolidationPreviewDialog.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "VDF.GUI", "Views", "ResourceConsolidationPreviewDialog.xaml.cs"));

        Assert.Contains("Content=\"确认全部\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"取消确认全部\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnConfirmAllManualClicked\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnClearAllManualClicked\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SetAllManualReviewsConfirmed(true)", code, StringComparison.Ordinal);
        Assert.Contains("SetAllManualReviewsConfirmed(false)", code, StringComparison.Ordinal);
        Assert.Contains("suppressInteractivePreviewRefresh", code, StringComparison.Ordinal);
        Assert.Contains("manualAcceptBoxes", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayModeChange_ResetsViewportButOrdinaryRebuildDoesNot() {
        Assert.True(ResourceSeriesSelectionSession.ShouldResetViewportForModeChange(
            ResultsDisplayMode.SimilarityGroups,
            ResultsDisplayMode.ResourceConsolidation));
        Assert.True(ResourceSeriesSelectionSession.ShouldResetViewportForModeChange(
            ResultsDisplayMode.ResourceConsolidation,
            ResultsDisplayMode.SimilarityGroups));
        Assert.False(ResourceSeriesSelectionSession.ShouldResetViewportForModeChange(
            ResultsDisplayMode.ResourceConsolidation,
            ResultsDisplayMode.ResourceConsolidation));
    }

    [Fact]
    public void ProductDefaultLanguage_IsSimplifiedChinese() {
        Assert.Equal("zh-Hans", SettingsFile.DefaultLanguageCode);
        var settings = new SettingsFile();
        Assert.Equal("zh-Hans", settings.LanguageCode);
    }
}
