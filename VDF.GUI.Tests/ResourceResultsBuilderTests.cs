// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class ResourceResultsBuilderTests {
    static DuplicateItemVM Item(Guid group, string path, long size = 100, bool reviewOnly = false) => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = group,
            Path = path,
            Folder = MainWindowVM.GetPikPakFolder(path).Replace('/', '\\'),
            SizeLong = size,
            Similarity = 99,
            Duration = TimeSpan.FromMinutes(10),
            FrameSize = "1920x1080",
            FrameSizeInt = 3000,
            Format = "h264",
            AudioChannel = "stereo",
            Flags = reviewOnly ? DuplicateFlags.AiMatched : DuplicateFlags.None,
        }
    };

    static ResultsBuildResult Canonical(params List<DuplicateItemVM>[] groups) =>
        ResultsListBuilder.Build(new ResultsBuildRequest {
            Items = groups.SelectMany(g => g).ToList(),
            IsTombstone = _ => false,
            IsOffline = _ => false,
        });

    [Fact]
    public void ResourceView_AssignsEveryTraditionalGroupAtMostOnce() {
        var g = Guid.NewGuid();
        var canonical = Canonical(new List<DuplicateItemVM> {
            Item(g, @"D:\Series A\001.mkv"),
            Item(g, @"E:\Misc\001.mkv"),
            Item(g, @"F:\Archive\001.mkv"),
        });
        var options = MainWindowVM.ComputePikPakFolderCoverageOptions(
            canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList());

        var resource = ResourceResultsBuilder.Build(canonical.Groups, options);
        var renderedGroups = resource.Rows.OfType<ResultsGroupHeader>().ToList();

        Assert.Single(canonical.Groups);
        Assert.Single(renderedGroups);
        Assert.Equal(g, renderedGroups[0].GroupId);
        Assert.Single(renderedGroups.Select(h => h.GroupId).Distinct());
    }

    [Fact]
    public void ResourceHeader_AlwaysShowsFullPathsFileCountsAndSizes() {
        var g = Guid.NewGuid();
        var canonical = Canonical(new List<DuplicateItemVM> {
            Item(g, @"D:\Series A\001.mkv", 4_000),
            Item(g, @"E:\Source Copy\001.mkv", 3_000),
        });
        var groups = canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList();
        var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
            [@"D:\Series A"] = new FolderMediaStats(100, 1_000_000_000),
            [@"E:\Source Copy"] = new FolderMediaStats(1, 3_000),
        };
        var options = MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats);

        var resource = ResourceResultsBuilder.Build(canonical.Groups, options);
        var header = Assert.Single(resource.Rows.OfType<ResourceRelationHeader>());

        Assert.Contains("Series A", header.DirectionLine);
        Assert.Contains("Source Copy", header.DirectionLine);
        Assert.Contains("100", header.TargetStats + header.SourceStats);
        Assert.Contains("文件", header.TargetStats);
        Assert.Contains("文件", header.SourceStats);
        Assert.NotEmpty(header.TargetStats);
        Assert.NotEmpty(header.SourceStats);
    }

    [Fact]
    public void SameFolderOnlyGroup_RemainsVisibleUnderOtherGroups() {
        var g = Guid.NewGuid();
        var canonical = Canonical(new List<DuplicateItemVM> {
            Item(g, @"D:\Series A\001.mkv"),
            Item(g, @"D:\Series A\001-copy.mkv"),
        });

        var resource = ResourceResultsBuilder.Build(canonical.Groups, Array.Empty<PikPakFolderCoverageOption>());

        var other = Assert.Single(resource.Rows.OfType<ResourceUnassignedHeader>());
        Assert.Equal(1, other.GroupCount);
        Assert.Equal(2, other.FileCount);
        Assert.Single(resource.Rows.OfType<ResultsGroupHeader>());
    }

    [Fact]
    public void ReviewOnlyRelation_IsVisibleButNeverAdvertisedAsWholeCollectionMerge() {
        var g = Guid.NewGuid();
        var canonical = Canonical(new List<DuplicateItemVM> {
            Item(g, @"D:\Series A\001.mkv", reviewOnly: true),
            Item(g, @"E:\Series A Copy\001.mkv", reviewOnly: true),
        });
        var groups = canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList();
        var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
            [@"D:\Series A"] = new FolderMediaStats(1, 100),
            [@"E:\Series A Copy"] = new FolderMediaStats(1, 100),
        };
        var options = MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats);

        var resource = ResourceResultsBuilder.Build(canonical.Groups, options);
        var header = Assert.Single(resource.Rows.OfType<ResourceRelationHeader>());

        Assert.False(header.WholeSourceEligible);
        Assert.Equal("仅处理匹配资源", header.ActionLabel);
        Assert.Equal(1, header.ReviewOnlyMatches);
    }
}
