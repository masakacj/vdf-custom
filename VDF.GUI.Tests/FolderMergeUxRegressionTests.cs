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

    [Fact]
    public void DifferentFileSizeAlone_NeverCreatesStrictBest() {
        var group = Guid.NewGuid();
        var small = Video(group, @"D:\Series\small.mkv", 100_000_000);
        var large = Video(group, @"E:\Collection\large.mkv", 900_000_000);

        Assert.False(MainWindowVM.TryPickDecisiveQualityWinner(new[] { small, large }, out _));
        var presentation = MainWindowVM.PickDecisiveBestForResults(new[] { small, large });
        Assert.Null(presentation.Best);
        Assert.Null(presentation.Tooltip);
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
    public void ProductDefaultLanguage_IsSimplifiedChinese() {
        Assert.Equal("zh-Hans", SettingsFile.DefaultLanguageCode);
        var settings = new SettingsFile();
        Assert.Equal("zh-Hans", settings.LanguageCode);
    }
}
