// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class BestRecommendationInteractiveMergeTests {
    static DuplicateItemVM Image(Guid group, string path, int resolution, long size, string format = "jpg") => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = group,
            Path = path,
            Folder = Path.GetDirectoryName(path) ?? string.Empty,
            SizeLong = size,
            Similarity = 99,
            IsImage = true,
            FrameSize = resolution >= 6000 ? "3840x2160" : "1920x1080",
            FrameSizeInt = resolution,
            Format = format,
            HdrFormat = string.Empty,
        }
    };

    static DuplicateItemVM Video(Guid group, string path, long size, decimal bitrate) => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = group,
            Path = path,
            Folder = Path.GetDirectoryName(path) ?? string.Empty,
            SizeLong = size,
            Similarity = 99,
            Duration = TimeSpan.FromMinutes(20),
            FrameSize = "1920x1080",
            FrameSizeInt = 3000,
            BitRateKbs = bitrate,
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
    public void AmbiguousImageGroup_StillGetsRecommendedBest() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-image-best");
        var smaller = Image(group, Path.Combine(root, "small.jpg"), 3000, 500_000);
        var larger = Image(group, Path.Combine(root, "large.jpg"), 3000, 2_000_000);

        BestRecommendation recommendation = MainWindowVM.RecommendBest(new[] { smaller, larger });

        Assert.NotNull(recommendation.Winner);
        Assert.False(recommendation.IsConfirmed);
        Assert.Same(larger, recommendation.Winner); // size is only a final weak tie-break, never smaller-size-win
        Assert.Contains("推荐 BEST", recommendation.Reason);
        Assert.Contains("弱参考", recommendation.Reason);
    }

    [Fact]
    public void HigherResolutionImage_IsRecommendedButStillRequiresHumanReview() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-image-resolution-best");
        var fullHd = Image(group, Path.Combine(root, "1080.jpg"), 3000, 5_000_000);
        var ultraHd = Image(group, Path.Combine(root, "4k.jpg"), 6000, 2_000_000);

        BestRecommendation recommendation = MainWindowVM.RecommendBest(new[] { fullHd, ultraHd });

        Assert.False(recommendation.IsConfirmed);
        Assert.Same(ultraHd, recommendation.Winner);
        Assert.Contains("分辨率更高", recommendation.Reason);
        Assert.Contains("人工", recommendation.Reason);
        Assert.False(MainWindowVM.TryPickDecisiveQualityWinner(new[] { fullHd, ultraHd }, out _));
    }

    [Fact]
    public void SmallerPhysicalFile_WithClearlyHigherBitrate_StillWins() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-video-quality-over-size");
        var largeLow = Video(group, Path.Combine(root, "large-low.mkv"), 8_000_000, 6_000);
        var smallHigh = Video(group, Path.Combine(root, "small-high.mkv"), 3_000_000, 12_000);

        BestRecommendation recommendation = MainWindowVM.RecommendBest(new[] { largeLow, smallHigh });

        Assert.True(recommendation.IsConfirmed);
        Assert.Same(smallHigh, recommendation.Winner);
        Assert.Contains("视频码率更高", recommendation.Reason);
    }

    [Fact]
    public void UserBestCriteriaOrder_ImmediatelyChangesRecommendedWinner() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-best-order");
        var longer = Video(group, Path.Combine(root, "longer.mkv"), 5_000_000, 6_000);
        var sharper = Video(group, Path.Combine(root, "sharper.mkv"), 5_000_000, 6_000);
        longer.ItemInfo.Duration = TimeSpan.FromMinutes(30);
        longer.ItemInfo.FrameSizeInt = 3000;
        longer.ItemInfo.FrameSize = "1920x1080";
        sharper.ItemInfo.Duration = TimeSpan.FromMinutes(20);
        sharper.ItemInfo.FrameSizeInt = 6000;
        sharper.ItemInfo.FrameSize = "3840x2160";

        BestRecommendation durationFirst = MainWindowVM.RecommendBest(
            new[] { longer, sharper }, new[] { "Duration", "Resolution", "Bitrate", "FPS", "Bits per pixel", "Audio Bitrate" });
        BestRecommendation resolutionFirst = MainWindowVM.RecommendBest(
            new[] { longer, sharper }, new[] { "Resolution", "Duration", "Bitrate", "FPS", "Bits per pixel", "Audio Bitrate" });

        Assert.Same(longer, durationFirst.Winner);
        Assert.Same(sharper, resolutionFirst.Winner);
    }

    [Fact]
    public void ResultsBuilder_AlwaysMarksExactlyOneBest_WithoutVerboseHeaderReason() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-results-recommended-best");
        var a = Image(group, Path.Combine(root, "a.jpg"), 3000, 1_000_000);
        var b = Image(group, Path.Combine(root, "b.jpg"), 3000, 2_000_000);

        ResultsBuildResult result = ResultsListBuilder.Build(new ResultsBuildRequest {
            Items = new[] { a, b },
            RecommendBest = MainWindowVM.RecommendBest,
            IsTombstone = _ => false,
            IsOffline = _ => false,
        });

        ResultsGroupHeader header = Assert.Single(result.Groups);
        ResultsItemRow best = Assert.Single(header.Rows.Where(row => row.IsBest));
        Assert.True(best.IsBestNeedsReview);
        Assert.False(string.IsNullOrWhiteSpace(best.BestReason));
        Assert.DoesNotContain("推荐 BEST", header.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("BEST：", header.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedReclaim_UsesActualLoserSizes_AndDeduplicatesPath() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-reclaim");
        string repeated = Path.Combine(root, "duplicate.mkv");
        var firstView = Video(group, repeated, 1_000_000, 6_000);
        var secondView = Video(group, repeated, 2_000_000, 6_000);
        var other = Video(group, Path.Combine(root, "other.mkv"), 3_000_000, 6_000);

        long bytes = MainWindowVM.ComputeConfirmedReclaimBytes(new[] { firstView, secondView, other });

        Assert.Equal(5_000_000, bytes);
        Assert.True(bytes > 0);
    }

    [Fact]
    public void SimilarityGroupHeader_OffersDirectMergeWithBestAndGroupFolderChoices() {
        string root = RepoRoot();
        string vmCode = File.ReadAllText(Path.Combine(root, "VDF.GUI", "ViewModels", "MainWindowVM_CheckedGroupConsolidation.cs"));
        string viewXaml = File.ReadAllText(Path.Combine(root, "VDF.GUI", "Views", "DuplicateResultsView.xaml"));
        string dialogCode = File.ReadAllText(Path.Combine(root, "VDF.GUI", "Views", "CheckedGroupConsolidationDialog.xaml.cs"));
        string dialogXaml = File.ReadAllText(Path.Combine(root, "VDF.GUI", "Views", "CheckedGroupConsolidationDialog.xaml"));

        Assert.Contains("ConsolidateGroupHeaderCommand", vmCode, StringComparison.Ordinal);
        Assert.Contains("item.ItemInfo.GroupId == header.GroupId", vmCode, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding $parent[UserControl].((vm:MainWindowVM)DataContext).ConsolidateGroupHeaderCommand}\"", viewXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"合并…\"", viewXaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding}\"", viewXaml, StringComparison.Ordinal);
        Assert.Contains("folders = candidates.Select(CandidateFolder)", dialogCode, StringComparison.Ordinal);
        Assert.Contains("KeeperComboBox.SelectedIndex = bestIndex", dialogCode, StringComparison.Ordinal);
        Assert.Contains("FolderComboBox.ItemsSource = folders", dialogCode, StringComparison.Ordinal);
        Assert.Contains("DestinationFolderTextBox.Text = folder", dialogCode, StringComparison.Ordinal);
        Assert.Contains("候选目录直接来自本组参与副本所在路径", dialogXaml, StringComparison.Ordinal);
    }
}
