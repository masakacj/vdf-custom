// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests {
	public class PikPakSelectionTests {
		static DuplicateItemVM Item(Guid group, string path, long size = 100,
			DateTime? created = null) => new() {
			IsVisibleInFilter = true,
			ItemInfo = new DuplicateItem {
				GroupId = group,
				Path = path,
				SizeLong = size,
				DateCreated = created ?? new DateTime(2020, 1, 1),
			}
		};

		[Fact]
		public void KeepLargest_SelectsEverythingExceptLargest() {
			var g = Guid.NewGuid();
			var a = Item(g, @"D:\Media\a.mp4", 100);
			var b = Item(g, @"D:\Media\b.mp4", 900);
			var c = Item(g, @"D:\Media\c.mp4", 500);

			var plan = MainWindowVM.ComputePikPakKeepSelection(
				new List<List<DuplicateItemVM>> { new() { a, b, c } }, PikPakKeepRule.Largest);

			Assert.Equal(new[] { b }, plan.Keepers);
			Assert.Equal(new[] { a, c }, plan.ToCheck);
			Assert.Equal(1, plan.MatchedGroups);
		}

		[Fact]
		public void KeepRule_TiePreservesCurrentGroupOrder() {
			var g = Guid.NewGuid();
			var first = Item(g, @"D:\First\a.mp4", 500);
			var second = Item(g, @"D:\Second\b.mp4", 500);

			var plan = MainWindowVM.ComputePikPakKeepSelection(
				new List<List<DuplicateItemVM>> { new() { second, first } }, PikPakKeepRule.Largest);

			Assert.Equal(new[] { second }, plan.Keepers);
			Assert.Equal(new[] { first }, plan.ToCheck);
		}

		[Fact]
		public void KeepNewest_UsesVdfCreationDateAndKeepsNewest() {
			var g = Guid.NewGuid();
			var old = Item(g, @"D:\Media\old.mp4", created: new DateTime(2019, 1, 1));
			var newest = Item(g, @"D:\Media\new.mp4", created: new DateTime(2025, 1, 1));

			var plan = MainWindowVM.ComputePikPakKeepSelection(
				new List<List<DuplicateItemVM>> { new() { old, newest } }, PikPakKeepRule.Newest);

			Assert.Equal(new[] { newest }, plan.Keepers);
			Assert.Equal(new[] { old }, plan.ToCheck);
		}

		[Fact]
		public void KeepShortestFileName_UsesFileNameNotWholePath() {
			var g = Guid.NewGuid();
			var shortNameDeepPath = Item(g, @"D:\Very\Long\Folder\Tree\a.mp4");
			var longNameShortPath = Item(g, @"D:\this-is-a-long-name.mp4");

			var plan = MainWindowVM.ComputePikPakKeepSelection(
				new List<List<DuplicateItemVM>> { new() { longNameShortPath, shortNameDeepPath } }, PikPakKeepRule.ShortestFileName);

			Assert.Equal(new[] { shortNameDeepPath }, plan.Keepers);
			Assert.Equal(new[] { longNameShortPath }, plan.ToCheck);
		}

		[Fact]
		public void KeywordKeeper_MultipleHitsKeepsFirstInCurrentOrder_AndSkipsNoHitGroup() {
			var g1 = Guid.NewGuid();
			var firstHit = Item(g1, @"D:\VIP\first.mp4");
			var secondHit = Item(g1, @"E:\VIP\second.mp4");
			var other = Item(g1, @"F:\Other\third.mp4");
			var g2 = Guid.NewGuid();
			var noHitA = Item(g2, @"D:\Other\a.mp4");
			var noHitB = Item(g2, @"E:\Other\b.mp4");

			var plan = MainWindowVM.ComputePikPakKeywordKeepSelection(
				new List<List<DuplicateItemVM>> {
					new() { firstHit, secondHit, other },
					new() { noHitA, noHitB },
				}, "vip", pathMode: true);

			Assert.Equal(new[] { firstHit }, plan.Keepers);
			Assert.Equal(new[] { secondHit, other }, plan.ToCheck);
			Assert.Equal(1, plan.MatchedGroups);
		}

		[Fact]
		public void DirectFileNameKeyword_SelectsEveryMatchingVisibleItem() {
			var g = Guid.NewGuid();
			var hit1 = Item(g, @"D:\Media\copy_keep.mp4");
			var miss = Item(g, @"D:\copy\other.mp4");
			var hit2 = Item(g, @"E:\Else\COPY_take2.mp4");

			var plan = MainWindowVM.ComputePikPakKeywordDirectSelection(
				new List<List<DuplicateItemVM>> { new() { hit1, miss, hit2 } }, "copy", pathMode: false);

			Assert.Equal(new[] { hit1, hit2 }, plan.ToCheck);
			Assert.Equal(1, plan.MatchedGroups);
		}

		[Fact]
		public void SameFolderExtras_KeepsFirstPerFolderAndNeverMarksOnlyCopyInFolder() {
			var g = Guid.NewGuid();
			var a1 = Item(g, @"D:\Media\a1.mp4");
			var a2 = Item(g, @"D:\Media\a2.mp4");
			var a3 = Item(g, @"D:\Media\a3.mp4");
			var onlyElsewhere = Item(g, @"E:\Archive\a.mp4");

			var plan = MainWindowVM.ComputePikPakSameFolderExtras(
				new List<List<DuplicateItemVM>> { new() { a2, a1, onlyElsewhere, a3 } });

			// Current display order is a2, a1, ..., a3, therefore a2 survives in D:\Media.
			Assert.Equal(new[] { a2 }, plan.Keepers);
			Assert.Equal(new[] { a1, a3 }, plan.ToCheck);
			Assert.DoesNotContain(onlyElsewhere, plan.ToCheck);
			Assert.Equal(1, plan.MatchedGroups);
		}

		[Fact]
		public void TargetPathInside_SelectsOnlyGroupsThatCrossTargetBoundary() {
			var crossing = Guid.NewGuid();
			var inside = Item(crossing, @"D:\Target\Series\a.mkv");
			var outside = Item(crossing, @"E:\Library\a.mkv");
			var allInside = Guid.NewGuid();
			var in2 = Item(allInside, @"D:\Target\b.mkv");
			var in3 = Item(allInside, @"D:\Target\Sub\b-copy.mkv");

			var groups = new List<List<DuplicateItemVM>> {
				new() { inside, outside },
				new() { in2, in3 },
			};

			var selectInside = MainWindowVM.ComputePikPakPathScopeSelection(groups, @"D:\Target", selectInside: true);
			var selectOutside = MainWindowVM.ComputePikPakPathScopeSelection(groups, @"D:\Target", selectInside: false);

			Assert.Equal(new[] { inside }, selectInside.ToCheck);
			Assert.Equal(new[] { outside }, selectOutside.ToCheck);
			Assert.Equal(1, selectInside.MatchedGroups);
			Assert.Equal(1, selectOutside.MatchedGroups);
		}

		[Fact]
		public void TargetPathParser_AcceptsLinesAndSemicolons_AndNormalizesSeparators() {
			var targets = MainWindowVM.ParsePikPakTargetPaths(" D:\\Media\\ ;\nE:/Archive/ \r\n");

			Assert.Equal(new[] { "D:/Media", "E:/Archive" }, targets);
			Assert.True(MainWindowVM.PikPakPathIsInScope(@"d:\Media\Sub\file.mkv", targets));
			Assert.True(MainWindowVM.PikPakPathIsInScope(@"E:\Archive\file.mkv", targets));
			Assert.False(MainWindowVM.PikPakPathIsInScope(@"E:\Archives\file.mkv", targets));
		}
	}
}
