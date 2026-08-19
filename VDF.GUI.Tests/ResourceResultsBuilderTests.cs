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
    public void SparseSeriesVersusHugeMisc_UsesSeriesAsTarget() {
        var g = Guid.NewGuid();
        var canonical = Canonical(new List<DuplicateItemVM> {
            Item(g, @"D:\Series A\001.mkv"),
            Item(g, @"E:\Misc\copy-001.mkv"),
        });
        var groups = canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList();
        var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
            [@"D:\Series A"] = new FolderMediaStats(100, 100_000),
            [@"E:\Misc"] = new FolderMediaStats(5_000, 5_000_000),
        };
        var options = MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats);

        var resource = ResourceResultsBuilder.Build(canonical.Groups, options);
        var header = Assert.Single(resource.Rows.OfType<ResourceRelationHeader>());

        Assert.Contains("Series A", header.TargetFolder);
        Assert.Contains("Misc", header.SourceFolder);
        Assert.Equal("含待复核推荐", header.ActionLabel);
        Assert.Equal(1, header.ReviewOnlyMatches);
    }

    [Fact]
    public void SameSeriesMultipleSourceFolders_WithSharedResourceEvidence_UseOneHeader() {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var canonical = Canonical(
            new List<DuplicateItemVM> {
                Item(g1, @"D:\Series A\001.mkv"),
                Item(g1, @"E:\Series A Copy\001.mkv"),
                Item(g1, @"F:\Series A Archive\001.mkv"),
            },
            new List<DuplicateItemVM> {
                Item(g2, @"D:\Series A\002.mkv"),
                Item(g2, @"E:\Series A Copy\002.mkv"),
                Item(g2, @"F:\Series A Archive\002.mkv"),
            });
        var groups = canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList();
        var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
            [@"D:\Series A"] = new FolderMediaStats(2, 2_000),
            [@"E:\Series A Copy"] = new FolderMediaStats(2, 1_800),
            [@"F:\Series A Archive"] = new FolderMediaStats(2, 1_900),
        };

        var resource = ResourceResultsBuilder.Build(
            canonical.Groups,
            MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats));
        var headers = resource.Rows.OfType<ResourceRelationHeader>().ToList();

        var header = Assert.Single(headers);
        Assert.Equal(2, header.SourceFolderCount);
        Assert.Contains("Series A Copy", header.SourceStats);
        Assert.Contains("Series A Archive", header.SourceStats);
        Assert.Equal(2, header.DisplayedResourceGroups);
        Assert.Equal(0, header.ConfirmedMatches);
        Assert.Equal(2, header.ReviewOnlyMatches);
    }

    [Fact]
    public void SameTargetWithoutSharedResourceEvidence_RemainsSeparate() {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var canonical = Canonical(
            new List<DuplicateItemVM> {
                Item(g1, @"D:\Series A\001.mkv"),
                Item(g1, @"E:\Partial Copy One\001.mkv"),
            },
            new List<DuplicateItemVM> {
                Item(g2, @"D:\Series A\002.mkv"),
                Item(g2, @"F:\Partial Copy Two\002.mkv"),
            });
        var groups = canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList();
        var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
            [@"D:\Series A"] = new FolderMediaStats(100, 100_000),
            [@"E:\Partial Copy One"] = new FolderMediaStats(1, 1_000),
            [@"F:\Partial Copy Two"] = new FolderMediaStats(1, 1_000),
        };

        var resource = ResourceResultsBuilder.Build(
            canonical.Groups,
            MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats));
        var headers = resource.Rows.OfType<ResourceRelationHeader>().ToList();

        Assert.Equal(2, headers.Count);
        Assert.All(headers, header => Assert.Equal(1, header.SourceFolderCount));
        Assert.Equal(2, resource.AssignedGroupCount);
    }

    [Fact]
    public void SharedMiscFolder_DoesNotBridgeUnrelatedSeriesIntoOneHeader() {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var canonical = Canonical(
            new List<DuplicateItemVM> {
                Item(g1, @"D:\Series A\001.mkv"),
                Item(g1, @"E:\Misc\A-001.mkv"),
            },
            new List<DuplicateItemVM> {
                Item(g2, @"F:\Series B\001.mkv"),
                Item(g2, @"E:\Misc\B-001.mkv"),
            });
        var groups = canonical.Groups.Select(h => h.Rows.Select(r => r.Item).ToList()).ToList();
        var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
            [@"D:\Series A"] = new FolderMediaStats(100, 100_000),
            [@"F:\Series B"] = new FolderMediaStats(100, 100_000),
            [@"E:\Misc"] = new FolderMediaStats(2, 2_000),
        };

        var resource = ResourceResultsBuilder.Build(
            canonical.Groups,
            MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats));
        var headers = resource.Rows.OfType<ResourceRelationHeader>().ToList();

        Assert.Equal(2, headers.Count);
        Assert.All(headers, header => Assert.Equal(1, header.SourceFolderCount));
        Assert.Equal(2, resource.Rows.OfType<ResultsGroupHeader>().Select(h => h.GroupId).Distinct().Count());
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
        Assert.Equal("含待复核推荐", header.ActionLabel);
        Assert.Equal(1, header.ReviewOnlyMatches);
        Assert.Equal(0, header.ConfirmedMatches);
        Assert.Contains("推荐 BEST", header.ActionHint);
        Assert.Contains("预览", header.ActionHint);
    }
}