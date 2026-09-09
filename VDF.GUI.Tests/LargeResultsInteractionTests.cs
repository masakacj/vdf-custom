// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Collections;
using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class LargeResultsInteractionTests {
    static DuplicateItemVM Video(string path, Guid groupId, decimal bitrate = 8_000, DuplicateFlags flags = DuplicateFlags.None) => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = groupId,
            Path = path,
            Folder = Path.GetDirectoryName(path) ?? string.Empty,
            SizeLong = 2_000_000,
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
            Flags = flags,
        }
    };

    [Fact]
    public void QualityTie_PrefersLongerVideoFilenameStem_IndependentOfInputOrder() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-best-filename");
        var shortName = Video(Path.Combine(root, "Movie.mkv"), group);
        var informativeName = Video(Path.Combine(root, "Movie.2026.1080p.WEB-DL.H264.AAC.mkv"), group);

        BestRecommendation forward = MainWindowVM.RecommendBest(
            new[] { shortName, informativeName }, Array.Empty<string>());
        BestRecommendation reverse = MainWindowVM.RecommendBest(
            new[] { informativeName, shortName }, Array.Empty<string>());

        Assert.Same(informativeName, forward.Winner);
        Assert.Same(informativeName, reverse.Winner);
        Assert.False(forward.IsConfirmed);
    }

    [Fact]
    public void BetterQuality_StillBeatsLongerFilename() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-best-filename-quality");
        var highQualityShortName = Video(Path.Combine(root, "A.mkv"), group, bitrate: 12_000);
        var lowerQualityLongName = Video(
            Path.Combine(root, "A.2026.1080p.WEB-DL.REMUX.RELEASE.GROUP.mkv"), group, bitrate: 6_000);

        BestRecommendation recommendation = MainWindowVM.RecommendBest(
            new[] { lowerQualityLongName, highQualityShortName }, Array.Empty<string>());

        Assert.Same(highQualityShortName, recommendation.Winner);
    }

    [Fact]
    public void ByteIdenticalVideos_PreferLongerFilenameStem() {
        Guid group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-best-byte-identical-name");
        var shortName = Video(Path.Combine(root, "Episode01.mkv"), group, flags: DuplicateFlags.ByteIdentical);
        var informativeName = Video(
            Path.Combine(root, "Series.Name.S01E01.1080p.WEB-DL.H264.AAC.mkv"), group,
            flags: DuplicateFlags.ByteIdentical);

        BestRecommendation recommendation = MainWindowVM.RecommendBest(new[] { shortName, informativeName });

        Assert.Same(informativeName, recommendation.Winner);
        Assert.True(recommendation.IsConfirmed);
    }

    [Fact]
    public void FolderBulkSelection_UsesDirectFolderAndCurrentVisibleHits() {
        Guid groupA = Guid.NewGuid();
        Guid groupB = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-folder-bulk");
        string folderA = Path.Combine(root, "A");
        string folderB = Path.Combine(root, "B");

        var anchor = Video(Path.Combine(folderA, "one.mkv"), groupA);
        var sameFolderSameGroup = Video(Path.Combine(folderA, "one-copy.mkv"), groupA);
        var otherFolderSameGroup = Video(Path.Combine(folderB, "one-other.mkv"), groupA);
        var sameFolderOtherGroup = Video(Path.Combine(folderA, "two.mkv"), groupB);
        var visible = new[] { anchor, sameFolderSameGroup, otherFolderSameGroup, sameFolderOtherGroup };

        IReadOnlyList<DuplicateItemVM> folderHits = MainWindowVM.ComputeFolderResultHits(visible, anchor);
        IReadOnlyList<DuplicateItemVM> otherFolderHits = MainWindowVM.ComputeOtherFolderGroupHits(visible, anchor);

        Assert.Equal(3, folderHits.Count);
        Assert.Contains(anchor, folderHits);
        Assert.Contains(sameFolderSameGroup, folderHits);
        Assert.Contains(sameFolderOtherGroup, folderHits);
        Assert.DoesNotContain(otherFolderSameGroup, folderHits);

        Assert.Single(otherFolderHits);
        Assert.Same(otherFolderSameGroup, otherFolderHits[0]);
    }

    [Fact]
    public void InitialLargeResultRestore_UsesBulkCollectionChange() {
        var target = new AvaloniaList<object>();
        object[] desired = Enumerable.Range(0, 50_000).Select(i => (object)i).ToArray();
        int notifications = 0;
        target.CollectionChanged += (_, _) => notifications++;

        ResultsRowReconciler.Apply(target, desired);

        Assert.Equal(desired.Length, target.Count);
        Assert.Equal(desired[0], target[0]);
        Assert.Equal(desired[^1], target[^1]);
        Assert.InRange(notifications, 1, 4);
    }

    [Fact]
    public void AvaloniaList_RemoveAll_UsesOneBulkCollectionNotification() {
        var target = new AvaloniaList<int>();
        target.AddRange(Enumerable.Range(0, 20_000));
        int notifications = 0;
        int removed = 0;
        target.CollectionChanged += (_, e) => {
            notifications++;
            removed += e.OldItems?.Count ?? 0;
        };

        int[] toRemove = Enumerable.Range(5_000, 10_000).ToArray();
        target.RemoveAll(toRemove);

        Assert.Equal(10_000, target.Count);
        Assert.Equal(10_000, removed);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void PathFilter_BuildsGroupHitSetOnce_AndOneMatchingSiblingExposesWholeGroup() {
        Guid matchingGroup = Guid.NewGuid();
        Guid otherGroup = Guid.NewGuid();
        var matchingSibling = Video(@"D:\Library\Season 01\Episode.01.mkv", matchingGroup);
        var nonMatchingSibling = Video(@"D:\Archive\different-name.mkv", matchingGroup);
        var unrelated = Video(@"D:\Archive\Episode.02.mkv", otherGroup);

        HashSet<Guid> hits = MainWindowVM.BuildPathHitGroups(
            new[] { matchingSibling, nonMatchingSibling, unrelated }, "Season 01");

        Assert.Single(hits);
        Assert.Contains(matchingGroup, hits);
        Assert.DoesNotContain(otherGroup, hits);
    }

    [Fact]
    public void PathFilter_WildcardSemanticsRemainSupported() {
        Guid group = Guid.NewGuid();
        var item = Video(@"D:\Shows\Season 01\Episode.07.1080p.mkv", group);

        // '?' matches exactly one character. Season "01" therefore needs two wildcards;
        // this keeps the production filter's established FileSystemName semantics intact.
        HashSet<Guid> hits = MainWindowVM.BuildPathHitGroups(new[] { item }, @"Season ??\Episode.*1080p");

        Assert.Contains(group, hits);
    }
}
