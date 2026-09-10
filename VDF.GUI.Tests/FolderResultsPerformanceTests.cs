// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Diagnostics;
using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class FolderResultsPerformanceTests {
    static DuplicateItemVM Item(Guid group, string path, long size = 100) => new() {
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
        }
    };

    static ResultsBuildResult Canonical(params List<DuplicateItemVM>[] groups) =>
        ResultsListBuilder.Build(new ResultsBuildRequest {
            Items = groups.SelectMany(group => group).ToList(),
            IsTombstone = _ => false,
            IsOffline = _ => false,
        });

    static string RepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void ExpandedRelation_CanBuildOnlyItsLocalChildRows() {
        ResourceSeriesSelectionSession.ResetForTests();
        try {
            Guid g1 = Guid.NewGuid();
            Guid g2 = Guid.NewGuid();
            DuplicateItemVM d1 = Item(g1, @"D:\Series A\001.mkv");
            DuplicateItemVM e1 = Item(g1, @"E:\Series A Copy\001.mkv");
            DuplicateItemVM d2 = Item(g2, @"D:\Series A\002.mkv");
            DuplicateItemVM e2 = Item(g2, @"E:\Series A Copy\002.mkv");
            var canonical = Canonical(
                new List<DuplicateItemVM> { d1, e1 },
                new List<DuplicateItemVM> { d2, e2 });

            // This test is deliberately about local presentation expansion, not the
            // folder-coverage confidence heuristic. Construct one known-valid relation
            // directly so future threshold changes cannot make this fast-path test flaky.
            var matches = new[] {
                new PikPakFolderCoverageMatch {
                    GroupId = g1,
                    FolderA = @"D:\Series A",
                    FolderB = @"E:\Series A Copy",
                    FolderAItems = new[] { d1 },
                    FolderBItems = new[] { e1 },
                    ReviewOnly = false,
                    AutoBestReviewOnly = false,
                },
                new PikPakFolderCoverageMatch {
                    GroupId = g2,
                    FolderA = @"D:\Series A",
                    FolderB = @"E:\Series A Copy",
                    FolderAItems = new[] { d2 },
                    FolderBItems = new[] { e2 },
                    ReviewOnly = false,
                    AutoBestReviewOnly = false,
                },
            };
            var option = new PikPakFolderCoverageOption(
                @"D:\Series A",
                @"E:\Series A Copy",
                matches,
                totalFilesA: 2,
                totalFilesB: 2,
                totalBytesA: 200,
                totalBytesB: 200,
                matchedFilesA: 2,
                matchedFilesB: 2,
                suggestAAsTarget: true);

            var collapsed = ResourceResultsBuilder.Build(
                canonical.Groups,
                new[] { option },
                minimumFolderMatchPercent: 0d);
            ResourceRelationHeader header = Assert.Single(collapsed.Rows.OfType<ResourceRelationHeader>());

            header.IsExpanded = true;
            List<object> children = ResourceResultsBuilder.BuildExpandedRows(header);

            Assert.Empty(children.OfType<ResourceRelationHeader>());
            Assert.Empty(children.OfType<ResourceUnassignedHeader>());
            Assert.Equal(4, children.OfType<ResultsItemRow>().Count());
            Assert.Equal(2, children.OfType<ResourceFolderContentHeader>().Count());
            Assert.Equal(2, header.DisplayedGroups.Count);
            Assert.All(header.DisplayedGroups, group => Assert.Contains(group, canonical.Groups));

            header.IsExpanded = false;
            Assert.Empty(ResourceResultsBuilder.BuildExpandedRows(header));
        }
        finally {
            ResourceSeriesSelectionSession.ResetForTests();
        }
    }

    [Fact]
    public void ResourceRelationSplit_LargeIndependentSet_CompletesWithoutQuadraticWalk() {
        const int relationCount = 10_000;
        var relations = new List<ResourceDirectedRelation>(relationCount);
        for (int i = 0; i < relationCount; i++) {
            Guid groupId = Guid.NewGuid();
            var match = new PikPakFolderCoverageMatch {
                GroupId = groupId,
                FolderA = @"D:\Target",
                FolderB = $@"E:\Source {i}",
                FolderAItems = Array.Empty<DuplicateItemVM>(),
                FolderBItems = Array.Empty<DuplicateItemVM>(),
                ReviewOnly = false,
                AutoBestReviewOnly = false,
            };
            var option = new PikPakFolderCoverageOption(
                @"D:\Target",
                $@"E:\Source {i}",
                new[] { match },
                1, 1, 100, 100, 1, 1,
                suggestAAsTarget: true);
            relations.Add(new ResourceDirectedRelation(option));
        }

        var sw = Stopwatch.StartNew();
        var components = ResourceResultsBuilder.SplitBySharedResourceEvidence(relations);
        sw.Stop();

        Assert.Equal(relationCount, components.Count);
        Assert.All(components, component => Assert.Single(component));
        // The inverted-index implementation is comfortably below this on GitHub runners;
        // the former all-pairs overlap walk performs ~100M HashSet comparisons here.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"split took {sw.Elapsed}");
    }

    [Fact]
    public void ModeSwitchAndExpand_AreWiredToPresentationOnlyFastPaths() {
        string root = RepoRoot();
        string results = File.ReadAllText(Path.Combine(root, "VDF.GUI", "ViewModels", "MainWindowVM_Results.cs"));
        string resource = File.ReadAllText(Path.Combine(root, "VDF.GUI", "ViewModels", "Results", "ResourceResultsBuilder.cs"));
        string performance = File.ReadAllText(Path.Combine(root, "VDF.GUI", "ViewModels", "MainWindowVM_ResourceResultsPerformance.cs"));

        const string modeMethod = "internal void SetResultsDisplayMode(ResultsDisplayMode mode)";
        int modeStart = results.IndexOf(modeMethod, StringComparison.Ordinal);
        Assert.True(modeStart >= 0);
        int modeEnd = results.IndexOf("/// <summary>", modeStart, StringComparison.Ordinal);
        string modeBody = results[modeStart..modeEnd];
        Assert.Contains("RefreshResultsDisplayModePresentation();", modeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RebuildResultsList();", modeBody, StringComparison.Ordinal);

        Assert.Contains("TryRefreshResourceRelationPresentation(this)", resource, StringComparison.Ordinal);
        Assert.Contains("ResultsRows.RemoveRange", performance, StringComparison.Ordinal);
        Assert.Contains("ResultsRows.InsertRange", performance, StringComparison.Ordinal);
        Assert.Contains("BackgroundResourceResultsThreshold = 20_000", performance, StringComparison.Ordinal);
    }
}
