// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class SeriesRootConsolidationTests {
	static DuplicateItemVM Item(Guid group, string path, long size = 100) => new() {
		IsVisibleInFilter = true,
		ItemInfo = new DuplicateItem {
			GroupId = group,
			Path = path,
			Folder = MainWindowVM.GetPikPakFolder(path).Replace('/', '\\'),
			SizeLong = size,
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

	[Fact]
	public void SeriesRootPlanner_PromotesMultipleChildFoldersIntoOneRootPair() {
		var g1 = Guid.NewGuid();
		var g2 = Guid.NewGuid();
		var groups = new List<List<DuplicateItemVM>> {
			new() {
				Item(g1, @"D:\Library\Series A\2026-01\NewYear\001.mkv"),
				Item(g1, @"E:\Archive\Series A Copy\2026-01\NewYear\001.mkv"),
			},
			new() {
				Item(g2, @"D:\Library\Series A\2026-02\Valentine\002.mkv"),
				Item(g2, @"E:\Archive\Series A Copy\2026-02\Valentine\002.mkv"),
			},
		};
		var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
			[@"D:\Library\Series A\2026-01\NewYear"] = new(1, 100),
			[@"D:\Library\Series A\2026-02\Valentine"] = new(1, 100),
			[@"E:\Archive\Series A Copy\2026-01\NewYear"] = new(1, 100),
			[@"E:\Archive\Series A Copy\2026-02\Valentine"] = new(1, 100),
			[@"D:\Library\Series A\2026-01"] = new(1, 100),
			[@"D:\Library\Series A\2026-02"] = new(1, 100),
			[@"E:\Archive\Series A Copy\2026-01"] = new(1, 100),
			[@"E:\Archive\Series A Copy\2026-02"] = new(1, 100),
			[@"D:\Library\Series A"] = new(2, 200),
			[@"E:\Archive\Series A Copy"] = new(2, 200),
		};

		var options = MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats);

		var first = Assert.IsType<PikPakFolderCoverageOption>(options.First());
		Assert.Contains("Series A", first.FolderA + first.FolderB);
		Assert.DoesNotContain("2026-", first.FolderA + first.FolderB);
		Assert.Equal(2, first.ConfirmedMatchedGroupCount);
		Assert.Equal(100d, first.FolderMatchPercent, 6);
	}

	[Fact]
	public void LowCoverageBroadAncestor_DoesNotReplacePreciseChildren() {
		var g = Guid.NewGuid();
		var groups = new List<List<DuplicateItemVM>> {
			new() {
				Item(g, @"D:\Library\Series A\Theme\001.mkv"),
				Item(g, @"E:\Archive\Series A\Theme\001.mkv"),
			},
		};
		var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
			[@"D:\Library\Series A\Theme"] = new(1, 100),
			[@"E:\Archive\Series A\Theme"] = new(1, 100),
			[@"D:\Library\Series A"] = new(100, 10_000),
			[@"E:\Archive\Series A"] = new(100, 10_000),
		};

		var options = MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats);

		Assert.DoesNotContain(options, option =>
			option.FolderA.EndsWith("Series A", StringComparison.OrdinalIgnoreCase) &&
			option.FolderB.EndsWith("Series A", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(options, option => option.FolderMatchPercent >= 99.9d);
	}

	[Fact]
	public void PreservedDestination_KeepsDateAndThemeSubfolders() {
		string root = Path.Combine(Path.GetTempPath(), "vdf-series-source");
		string source = Path.Combine(root, "2026-02-14", "Valentine", "003.mkv");
		string destinationRoot = Path.Combine(Path.GetTempPath(), "vdf-series-destination");

		Assert.True(MainWindowVM.TryBuildPreservedDestination(root, source, destinationRoot, out string destination));
		Assert.Equal(
			Path.GetFullPath(Path.Combine(destinationRoot, "2026-02-14", "Valentine", "003.mkv")),
			destination);
	}

	[Fact]
	public void PreservedDestination_RejectsPathOutsideSeriesRoot() {
		string root = Path.Combine(Path.GetTempPath(), "vdf-series-a");
		string outside = Path.Combine(Path.GetTempPath(), "vdf-series-b", "001.mkv");
		string destinationRoot = Path.Combine(Path.GetTempPath(), "vdf-series-destination");

		Assert.False(MainWindowVM.TryBuildPreservedDestination(root, outside, destinationRoot, out _));
	}

	[Fact]
	public void ExactSafeMove_RefusesExistingDestinationInsteadOfRenaming() {
		string folder = Path.Combine(Path.GetTempPath(), "vdf-exact-move-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try {
			string source = Path.Combine(folder, "source.bin");
			string destination = Path.Combine(folder, "target.bin");
			File.WriteAllBytes(source, [1, 2, 3]);
			File.WriteAllBytes(destination, [9, 9, 9]);

			SafeMoveResult result = SafeFileTransfer.MoveVerifiedExact(source, destination);

			Assert.False(result.Success);
			Assert.True(File.Exists(source));
			Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(destination));
			Assert.Empty(Directory.GetFiles(folder, "*_best*"));
		}
		finally {
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void SelectingSeriesHeader_DoesNotMarkFilesForDeletion() {
		var g = Guid.NewGuid();
		var a = Item(g, @"D:\Series A\2026\001.mkv");
		var b = Item(g, @"E:\Series A Copy\2026\001.mkv");
		var canonical = Canonical(new List<DuplicateItemVM> { a, b });
		var options = MainWindowVM.ComputePikPakFolderCoverageOptions(
			canonical.Groups.Select(header => header.Rows.Select(row => row.Item).ToList()).ToList());
		var resource = ResourceResultsBuilder.Build(canonical.Groups, options);
		var header = Assert.Single(resource.Rows.OfType<ResourceRelationHeader>());

		header.IsSelected = true;

		Assert.True(header.IsSelected);
		Assert.False(a.Checked);
		Assert.False(b.Checked);
	}
}
