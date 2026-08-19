// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class DecisiveBestAndMergePreviewTests {
    static DuplicateItemVM Video(
        string path,
        long size,
        decimal bitrate = 8_000,
        string frameSize = "1920x1080",
        int frameSizeInt = 3000) => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = Guid.NewGuid(),
            Path = path,
            Folder = Path.GetDirectoryName(path) ?? string.Empty,
            SizeLong = size,
            Similarity = 99,
            Duration = TimeSpan.FromMinutes(20),
            FrameSize = frameSize,
            FrameSizeInt = frameSizeInt,
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

    [Fact]
    public void DifferentFileSizeAlone_DoesNotCreateBestBadge() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-best-size-neutral");
        var small = Video(Path.Combine(root, "small.mkv"), size: 500_000);
        var large = Video(Path.Combine(root, "large.mkv"), size: 2_000_000);

        var (best, tooltip) = MainWindowVM.PickDecisiveBestForResults(new[] { small, large });

        Assert.Null(best);
        Assert.Null(tooltip);
        Assert.False(MainWindowVM.TryPickDecisiveQualityWinner(new[] { small, large }, out _));
    }

    [Fact]
    public void HigherBitrateCanWinEvenWhenPhysicalFileIsSmaller() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-best-quality-over-size");
        var lowerQualityLargeFile = Video(
            Path.Combine(root, "large-low-bitrate.mkv"), size: 4_000_000, bitrate: 6_000);
        var higherQualitySmallFile = Video(
            Path.Combine(root, "small-high-bitrate.mkv"), size: 2_000_000, bitrate: 12_000);

        var (best, tooltip) = MainWindowVM.PickDecisiveBestForResults(
            new[] { lowerQualityLargeFile, higherQualitySmallFile });

        Assert.Same(higherQualitySmallFile, best);
        Assert.NotNull(tooltip);
        Assert.Contains("文件大小不参与 BEST 判定", tooltip!);
    }

    [Fact]
    public void TreePreview_PreservesNestedFoldersAndOperationMarkers() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-preview", "Series A");
        var files = new Dictionary<string, (long Bytes, string Marker)> {
            [Path.Combine(root, "2026-02-14", "情人节", "003.mkv")] = (12_000_000, "↑ BEST替换"),
            [Path.Combine(root, "花絮", "004.mp4")] = (5_000_000, "＋ 新增"),
            [Path.Combine(root, "2026-01-01", "001.mkv")] = (9_000_000, "＝ BEST"),
        };

        string tree = MainWindowVM.BuildOneTreeSection(root, files);

        Assert.Contains("Series A", tree);
        Assert.Contains("2026-02-14", tree);
        Assert.Contains("情人节", tree);
        Assert.Contains("003.mkv", tree);
        Assert.Contains("↑ BEST替换", tree);
        Assert.Contains("花絮", tree);
        Assert.Contains("＋ 新增", tree);
        Assert.Contains("＝ BEST", tree);
    }

    [Fact]
    public void DefaultLanguage_IsSimplifiedChinese() {
        Assert.Equal("zh-Hans", SettingsFile.DefaultLanguageCode);
        Assert.Equal("zh-Hans", new SettingsFile().LanguageCode);
    }
}
