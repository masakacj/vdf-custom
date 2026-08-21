// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class SmartNonBestSelectionTests {
    static DuplicateItemVM Video(Guid group, string folder, string fileName, decimal bitrate) {
        string path = Path.Combine(folder, fileName);
        return new DuplicateItemVM {
            IsVisibleInFilter = true,
            ItemInfo = new DuplicateItem {
                GroupId = group,
                Path = path,
                Folder = folder,
                SizeLong = bitrate >= 10_000 ? 4_000_000 : 3_000_000,
                Similarity = 99,
                Duration = TimeSpan.FromMinutes(10),
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
    }

    [Fact]
    public void NoKeywords_SelectsEveryNonBestAcrossGroups() {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-smart-nonbest");
        var best1 = Video(g1, Path.Combine(root, "A"), "best-a.mkv", 12_000);
        var low1 = Video(g1, Path.Combine(root, "A-copy"), "copy-a.mkv", 6_000);
        var best2 = Video(g2, Path.Combine(root, "B"), "best-b.mkv", 15_000);
        var low2 = Video(g2, Path.Combine(root, "B-copy"), "copy-b.mkv", 7_000);

        var selected = MainWindowVM.ComputeSmartNonBestSelection(
            new[] { best1, low1, best2, low2 },
            _ => true,
            new SmartNonBestSelectionOptions(string.Empty, string.Empty));

        Assert.Equal(2, selected.Count);
        Assert.Contains(low1, selected);
        Assert.Contains(low2, selected);
        Assert.DoesNotContain(best1, selected);
        Assert.DoesNotContain(best2, selected);
    }

    [Fact]
    public void FileNameKeyword_SelectsOnlyMatchingNonBest_AndNeverBest() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-smart-name");
        var best = Video(group, Path.Combine(root, "library"), "copy-BEST.mkv", 15_000);
        var matchingLoser = Video(group, Path.Combine(root, "inbox"), "copy-low.mkv", 6_000);
        var otherLoser = Video(group, Path.Combine(root, "archive"), "old-low.mkv", 5_000);

        var selected = MainWindowVM.ComputeSmartNonBestSelection(
            new[] { best, matchingLoser, otherLoser },
            _ => true,
            new SmartNonBestSelectionOptions("copy", string.Empty));

        Assert.Single(selected);
        Assert.Same(matchingLoser, selected[0]);
        Assert.DoesNotContain(best, selected);
    }

    [Fact]
    public void FileAndPathKeywords_AreAnded_WhileKeywordsWithinFieldAreOr() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-smart-and");
        var best = Video(group, Path.Combine(root, "library"), "master.mkv", 15_000);
        var both = Video(group, Path.Combine(root, "Downloads", "temp"), "copy-low.mkv", 7_000);
        var nameOnly = Video(group, Path.Combine(root, "archive"), "copy-old.mkv", 6_000);
        var pathOnly = Video(group, Path.Combine(root, "Downloads"), "other.mkv", 5_000);

        var selected = MainWindowVM.ComputeSmartNonBestSelection(
            new[] { best, both, nameOnly, pathOnly },
            _ => true,
            new SmartNonBestSelectionOptions("copy；duplicate", "Downloads,临时"));

        Assert.Single(selected);
        Assert.Same(both, selected[0]);
    }

    [Fact]
    public void EligibilityFilter_PreventsHiddenItemsFromBeingSelected() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-smart-visible");
        var best = Video(group, Path.Combine(root, "library"), "best.mkv", 15_000);
        var visibleLoser = Video(group, Path.Combine(root, "visible"), "copy-visible.mkv", 7_000);
        var hiddenLoser = Video(group, Path.Combine(root, "hidden"), "copy-hidden.mkv", 6_000);

        var selected = MainWindowVM.ComputeSmartNonBestSelection(
            new[] { best, visibleLoser, hiddenLoser },
            item => !ReferenceEquals(item, hiddenLoser),
            new SmartNonBestSelectionOptions("copy", string.Empty));

        Assert.Single(selected);
        Assert.Same(visibleLoser, selected[0]);
    }

    [Fact]
    public void HiddenFullGroupBest_DoesNotCauseVisibleBestToBeSelected() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-smart-visible-best");
        var hiddenBest = Video(group, Path.Combine(root, "hidden"), "master-hidden.mkv", 20_000);
        var visibleBest = Video(group, Path.Combine(root, "visible"), "best-visible.mkv", 12_000);
        var visibleLoser = Video(group, Path.Combine(root, "visible"), "copy-visible.mkv", 6_000);

        var selected = MainWindowVM.ComputeSmartNonBestSelection(
            new[] { hiddenBest, visibleBest, visibleLoser },
            item => !ReferenceEquals(item, hiddenBest),
            new SmartNonBestSelectionOptions(string.Empty, string.Empty),
            new[] { "Bitrate", "Resolution", "Duration" });

        Assert.Single(selected);
        Assert.Same(visibleLoser, selected[0]);
        Assert.DoesNotContain(visibleBest, selected);
    }

    [Fact]
    public void FullyTiedBest_IsIndependentOfDisplaySort_AndSmartSelectionNeverChecksIt() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-smart-sort-tie");
        var a = Video(group, Path.Combine(root, "A"), "a.mkv", 8_000);
        var b = Video(group, Path.Combine(root, "B"), "b.mkv", 8_000);
        a.ItemInfo.SizeLong = 1_000_000;
        b.ItemInfo.SizeLong = 9_000_000;
        string[] criteria = ["Duration", "Resolution", "Bitrate", "FPS", "Bits per pixel", "Audio Bitrate"];

        ResultsBuildResult result = ResultsListBuilder.Build(new ResultsBuildRequest {
            Items = new[] { a, b },
            SortMode = ResultsSortMode.WastedSpace,
            SortDescending = true,
            RecommendBest = members => MainWindowVM.RecommendBest(members, criteria),
            IsTombstone = _ => false,
            IsOffline = _ => false,
        });
        ResultsItemRow listBest = Assert.Single(Assert.Single(result.Groups).Rows, row => row.IsBest);
        var selected = MainWindowVM.ComputeSmartNonBestSelection(
            new[] { a, b }, _ => true,
            new SmartNonBestSelectionOptions(string.Empty, string.Empty), criteria);

        Assert.Same(a, listBest.Item);
        Assert.DoesNotContain(listBest.Item, selected);
        Assert.Single(selected);
        Assert.Same(b, selected[0]);
    }

    [Fact]
    public void ConfigurableBest_FullyTiedGroup_IsStableAcrossInputOrder() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-best-input-order");
        var a = Video(group, root, "a.mkv", 8_000);
        var b = Video(group, root, "b.mkv", 8_000);
        string[] criteria = ["Duration", "Resolution", "Bitrate", "FPS", "Bits per pixel", "Audio Bitrate"];

        BestRecommendation forward = MainWindowVM.RecommendBest(new[] { a, b }, criteria);
        BestRecommendation reversed = MainWindowVM.RecommendBest(new[] { b, a }, criteria);

        Assert.Same(a, forward.Winner);
        Assert.Same(a, reversed.Winner);
    }

    [Fact]
    public void KeywordParser_AcceptsChineseAndAsciiSeparators() {
        string[] keywords = MainWindowVM.ParseSmartSelectionKeywords("copy, low；预览\n临时，archive;copy");

        Assert.Equal(5, keywords.Length);
        Assert.Contains("copy", keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("low", keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("预览", keywords);
        Assert.Contains("临时", keywords);
        Assert.Contains("archive", keywords, StringComparer.OrdinalIgnoreCase);
    }
}
