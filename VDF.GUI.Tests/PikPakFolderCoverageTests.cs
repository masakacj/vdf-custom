// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests {
	public class PikPakFolderCoverageTests {
		static DuplicateItemVM Item(Guid group, string path, long size = 100, DateTime? created = null) => new() {
			IsVisibleInFilter = true,
			ItemInfo = new DuplicateItem {
				GroupId = group,
				Path = path,
				Folder = MainWindowVM.GetPikPakFolder(path).Replace('/', '\\'),
				SizeLong = size,
				DateCreated = created ?? new DateTime(2020, 1, 1),
			}
		};

		[Fact]
		public void OneStrayFile_StillCreatesFolderRelationship_WithDirectionalCoverage() {
			var g = Guid.NewGuid();
			var series = Item(g, @"D:\Series A\001.mkv");
			var stray = Item(g, @"E:\Misc\copy-001.mkv");
			var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
				[@"D:\Series A"] = new FolderMediaStats(100, 100_000),
				[@"E:\Misc"] = new FolderMediaStats(1, 1_000),
			};

			var options = MainWindowVM.ComputePikPakFolderCoverageOptions(
				new List<List<DuplicateItemVM>> { new() { series, stray } }, stats);

			var option = Assert.Single(options);
			Assert.Equal(1, option.MatchedGroupCount);
			Assert.Equal(1d, option.CoverageA, 6);
			Assert.Equal(100d, option.CoverageB, 6);
			Assert.Equal("D:/Series A", option.SuggestedTargetFolder);
			Assert.Equal("E:/Misc", option.SuggestedSourceFolder);
		}

		[Fact]
		public void EightyOfOneHundredVersusEightyOfEighty_SuggestsTheHundredFileFolder() {
			var groups = new List<List<DuplicateItemVM>>();
			for (int i = 0; i < 80; i++) {
				var g = Guid.NewGuid();
				groups.Add(new List<DuplicateItemVM> {
					Item(g, $@"D:\Series A\{i:000}.mkv"),
					Item(g, $@"E:\Series A Copy\{i:000}.mkv"),
				});
			}
			var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
				[@"D:\Series A"] = new FolderMediaStats(100, 0),
				[@"E:\Series A Copy"] = new FolderMediaStats(80, 0),
			};

			var option = Assert.Single(MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats));

			Assert.Equal(80d, option.CoverageA, 6);
			Assert.Equal(100d, option.CoverageB, 6);
			Assert.Equal("D:/Series A", option.SuggestedTargetFolder);
			Assert.Equal(80, option.MatchedGroupCount);
		}

		[Fact]
		public void MiscFolderCanTouchMultipleSeries_WithoutCreatingOneMegaFolderGroup() {
			var g1 = Guid.NewGuid();
			var g2 = Guid.NewGuid();
			var groups = new List<List<DuplicateItemVM>> {
				new() { Item(g1, @"D:\Series A\001.mkv"), Item(g1, @"E:\Misc\A-001.mkv") },
				new() { Item(g2, @"F:\Series B\001.mkv"), Item(g2, @"E:\Misc\B-001.mkv") },
			};

			var options = MainWindowVM.ComputePikPakFolderCoverageOptions(groups);

			Assert.Equal(2, options.Count);
			Assert.DoesNotContain(options, option =>
				(option.FolderA.Contains("Series A") && option.FolderB.Contains("Series B")) ||
				(option.FolderA.Contains("Series B") && option.FolderB.Contains("Series A")));
		}

		[Fact]
		public void MergeKeepTarget_ChecksOnlyTheSourceMembersForEachMatchedFileGroup() {
			var g1 = Guid.NewGuid();
			var g2 = Guid.NewGuid();
			var target1 = Item(g1, @"D:\Series A\001.mkv");
			var source1 = Item(g1, @"E:\Copy\001-copy.mkv");
			var target2 = Item(g2, @"D:\Series A\002.mkv");
			var source2 = Item(g2, @"E:\Copy\002-copy.mkv");
			var groups = new List<List<DuplicateItemVM>> {
				new() { target1, source1 },
				new() { target2, source2 },
			};
			var stats = new Dictionary<string, FolderMediaStats>(StringComparer.OrdinalIgnoreCase) {
				[@"D:\Series A"] = new FolderMediaStats(100, 0),
				[@"E:\Copy"] = new FolderMediaStats(2, 0),
			};
			var option = Assert.Single(MainWindowVM.ComputePikPakFolderCoverageOptions(groups, stats));

			var plan = MainWindowVM.ComputePikPakFolderMergeSelection(
				option, swapSuggestedDirection: false, PikPakFolderMergeKeepRule.KeepTarget);

			Assert.Equal(new[] { target1, target2 }, plan.Keepers);
			Assert.Equal(new[] { source1, source2 }, plan.ToCheck);
			Assert.Equal(2, plan.MatchedGroups);
		}

		[Fact]
		public void MergePair_DoesNotTouchThirdFolderCopyInTheSameDuplicateGroup() {
			var g = Guid.NewGuid();
			var a = Item(g, @"D:\Series A\001.mkv", 100);
			var b = Item(g, @"E:\Copy\001.mkv", 200);
			var third = Item(g, @"F:\Archive\001.mkv", 300);
			var options = MainWindowVM.ComputePikPakFolderCoverageOptions(
				new List<List<DuplicateItemVM>> { new() { a, b, third } });
			var option = options.Single(o =>
				o.FolderA.Contains("Series A", StringComparison.OrdinalIgnoreCase) &&
				o.FolderB.Contains("Copy", StringComparison.OrdinalIgnoreCase));

			var plan = MainWindowVM.ComputePikPakFolderMergeSelection(
				option, swapSuggestedDirection: false, PikPakFolderMergeKeepRule.Largest);

			Assert.DoesNotContain(third, plan.ToCheck);
			Assert.DoesNotContain(third, plan.Keepers);
			Assert.Single(plan.ToCheck);
		}

		[Fact]
		public void MergeKeepBestQuality_UsesProvidedPickerAndLeavesOtherFoldersAlone() {
			var g = Guid.NewGuid();
			var a = Item(g, @"D:\Series A\001-low.mkv", 100);
			var b = Item(g, @"E:\Copy\001-high.mkv", 900);
			var option = Assert.Single(MainWindowVM.ComputePikPakFolderCoverageOptions(
				new List<List<DuplicateItemVM>> { new() { a, b } }));

			var plan = MainWindowVM.ComputePikPakFolderMergeSelection(
				option,
				swapSuggestedDirection: false,
				PikPakFolderMergeKeepRule.BestQuality,
				members => members.OrderByDescending(item => item.ItemInfo.SizeLong).First());

			Assert.Equal(new[] { b }, plan.Keepers);
			Assert.Equal(new[] { a }, plan.ToCheck);
		}
	}
}
