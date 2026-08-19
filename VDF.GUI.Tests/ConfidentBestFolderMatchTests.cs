// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class ConfidentBestFolderMatchTests {
    static DuplicateItemVM Video(
        Guid group,
        string path,
        string frameSize = "1920x1080",
        int frameSizeInt = 3000,
        decimal bitrate = 8_000,
        float fps = 30,
        string format = "h264",
        decimal audioBitrate = 192,
        int audioSampleRate = 48_000,
        string audioFormat = "aac") => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = group,
            Path = path,
            Folder = MainWindowVM.GetPikPakFolder(path).Replace('/', '\\'),
            SizeLong = 1_000_000,
            Similarity = 99,
            Duration = TimeSpan.FromMinutes(20),
            FrameSize = frameSize,
            FrameSizeInt = frameSizeInt,
            BitRateKbs = bitrate,
            Fps = fps,
            Format = format,
            AudioChannel = "stereo",
            AudioFormat = audioFormat,
            AudioBitRateKbs = audioBitrate,
            AudioSampleRate = audioSampleRate,
            HdrFormat = string.Empty,
        }
    };

    static ResultsBuildResult Canonical(IEnumerable<DuplicateItemVM> items) =>
        ResultsListBuilder.Build(new ResultsBuildRequest {
            Items = items.ToList(),
            IsTombstone = _ => false,
            IsOffline = _ => false,
        });

    [Fact]
    public void EqualQualityCopies_AreValidFolderEvidence_ButManualBest() {
        var g = Guid.NewGuid();
        var a = Video(g, @"D:\Series\001.mkv");
        var b = Video(g, @"E:\Copy\001.mkv");

        Assert.False(MainWindowVM.IsReviewOnlyResourceGroup(new[] { a, b }));
        Assert.False(MainWindowVM.TryPickDecisiveQualityWinner(new[] { a, b }, out _));

        var option = Assert.Single(MainWindowVM.ComputePikPakFolderCoverageOptions(
            new List<List<DuplicateItemVM>> { new() { a, b } }));
        var match = Assert.Single(option.Matches);
        Assert.False(match.ReviewOnly);
        Assert.True(match.AutoBestReviewOnly);
        Assert.Equal(1, option.ConfirmedMatchedGroupCount);
        Assert.Equal(1, option.AutoBestReviewOnlyGroupCount);
    }

    [Fact]
    public void ClearlyHigherBitrateSameEncode_IsDecisiveWinner() {
        var g = Guid.NewGuid();
        var lower = Video(g, @"D:\Series\001-low.mkv", bitrate: 6_000);
        var higher = Video(g, @"E:\Copy\001-high.mkv", bitrate: 12_000);

        Assert.True(MainWindowVM.TryPickDecisiveQualityWinner(new[] { lower, higher }, out var winner));
        Assert.Same(higher, winner);
    }

    [Fact]
    public void HigherResolutionButLowerBitsPerPixel_IsManualTradeoff() {
        var g = Guid.NewGuid();
        var fullHd = Video(g, @"D:\Series\001-1080.mkv", bitrate: 8_000);
        var ultraHd = Video(
            g,
            @"E:\Copy\001-4k.mkv",
            frameSize: "3840x2160",
            frameSizeInt: 6000,
            bitrate: 12_000);

        Assert.False(MainWindowVM.IsReviewOnlyResourceGroup(new[] { fullHd, ultraHd }));
        Assert.False(MainWindowVM.TryPickDecisiveQualityWinner(new[] { fullHd, ultraHd }, out _));
    }

    [Fact]
    public void DifferentVideoCodecs_AreManualReview() {
        var g = Guid.NewGuid();
        var h264 = Video(g, @"D:\Series\001-h264.mkv", format: "h264");
        var h265 = Video(g, @"E:\Copy\001-h265.mkv", format: "hevc");

        Assert.True(MainWindowVM.IsReviewOnlyResourceGroup(new[] { h264, h265 }));
        Assert.False(MainWindowVM.TryPickDecisiveQualityWinner(new[] { h264, h265 }, out _));
    }

    [Fact]
    public void BilateralFolderMatchThreshold_FiltersWeakRelations() {
        var items = new List<DuplicateItemVM>();
        for (int i = 0; i < 2; i++) {
            var g = Guid.NewGuid();
            items.Add(Video(g, $@"D:\Series\{i:000}.mkv"));
            items.Add(Video(g, $@"E:\Copy\{i:000}.mkv"));
        }

        var canonical = Canonical(items);
        var groups = canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList();
        var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
            [@"D:\Series"] = new FolderMediaStats(10, 10_000),
            [@"E:\Copy"] = new FolderMediaStats(2, 2_000),
        };
        var option = Assert.Single(MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats));

        Assert.Equal(20d, option.FolderMatchPercent, 6);

        var accepted = ResourceResultsBuilder.Build(
            canonical.Groups, new[] { option }, expandedDetails: null, minimumFolderMatchPercent: 20d);
        Assert.Single(accepted.Rows.OfType<ResourceRelationHeader>());

        var filtered = ResourceResultsBuilder.Build(
            canonical.Groups, new[] { option }, expandedDetails: null, minimumFolderMatchPercent: 20.1d);
        Assert.Empty(filtered.Rows.OfType<ResourceRelationHeader>());
        Assert.Equal(canonical.Groups.Count, filtered.UnassignedGroupCount);
    }

    [Fact]
    public void ResourceHeader_ExposesFolderMatchAndManualBestCount() {
        var g = Guid.NewGuid();
        var a = Video(g, @"D:\Series\001.mkv");
        var b = Video(g, @"E:\Copy\001.mkv");
        var canonical = Canonical(new[] { a, b });
        var groups = canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList();
        var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
            [@"D:\Series"] = new FolderMediaStats(1, 1_000),
            [@"E:\Copy"] = new FolderMediaStats(1, 1_000),
        };
        var option = Assert.Single(MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats));

        var resource = ResourceResultsBuilder.Build(
            canonical.Groups, new[] { option }, expandedDetails: null, minimumFolderMatchPercent: 0d);
        var header = Assert.Single(resource.Rows.OfType<ResourceRelationHeader>());

        Assert.Equal(100d, header.MinimumFolderMatchPercent, 6);
        Assert.Equal(1, header.ReviewOnlyMatches);
        Assert.Contains("文件夹匹配", header.SourceStats);
        Assert.Contains("推荐 BEST", header.RelationStats);
        Assert.Contains("待复核", header.RelationStats);
        Assert.False(header.WholeSourceEligible);
    }
}
