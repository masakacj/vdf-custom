// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Controls;
using Avalonia.Headless;
using VDF.Core;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI.Tests;

public class ResultsContextMenuSelectionTests {
	static DuplicateItemVM CreateItem(string path, Guid groupId, bool isChecked = false) => new() {
		IsVisibleInFilter = true,
		Checked = isChecked,
		ItemInfo = new DuplicateItem {
			GroupId = groupId,
			Path = path,
			Folder = Path.GetDirectoryName(path) ?? string.Empty,
			SizeLong = 1_000_000,
			Similarity = 99,
			Duration = TimeSpan.FromMinutes(10),
		}
	};

	static ResultsGroupHeader CreateGroup(Guid groupId, params DuplicateItemVM[] items) {
		var rows = items.Select(item => new ResultsItemRow(item)).ToList();
		var header = new ResultsGroupHeader {
			GroupId = groupId,
			Rows = rows,
		};
		foreach (var row in rows)
			row.Group = header;
		return header;
	}

	[Fact]
	public void Action1_SelectOtherFolderSharedGroupHits_MatchesOutsideMembersOfQualifyingGroups() {
		Guid group1 = Guid.NewGuid();
		Guid group2 = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";
		string folderC = CoreUtils.IsWindows ? @"C:\Media\FolderC" : "/media/FolderC";

		var anchor = CreateItem(Path.Combine(folderA, "video1.mkv"), group1);
		var sameFolderGroup1 = CreateItem(Path.Combine(folderA, "video1_copy.mkv"), group1);
		var outsideGroup1 = CreateItem(Path.Combine(folderB, "video1_dup.mkv"), group1);

		var sameFolderGroup2 = CreateItem(Path.Combine(folderA, "video2.mkv"), group2);
		var outsideGroup2 = CreateItem(Path.Combine(folderC, "video2_dup.mkv"), group2);

		var visibleItems = new[] { anchor, sameFolderGroup1, outsideGroup1, sameFolderGroup2, outsideGroup2 };

		IReadOnlyList<DuplicateItemVM> hits = MainWindowVM.ComputeOtherFolderSharedGroupHits(visibleItems, anchor);

		Assert.Equal(2, hits.Count);
		Assert.Contains(outsideGroup1, hits);
		Assert.Contains(outsideGroup2, hits);
		Assert.DoesNotContain(anchor, hits);
		Assert.DoesNotContain(sameFolderGroup1, hits);
		Assert.DoesNotContain(sameFolderGroup2, hits);
	}

	[Fact]
	public void Action2_SelectSameFolderSharedGroupHits_MatchesInsideMembersOfQualifyingGroups() {
		Guid group1 = Guid.NewGuid();
		Guid group2 = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";
		string folderC = CoreUtils.IsWindows ? @"C:\Media\FolderC" : "/media/FolderC";

		var anchor = CreateItem(Path.Combine(folderA, "video1.mkv"), group1);
		var sameFolderGroup1 = CreateItem(Path.Combine(folderA, "video1_copy.mkv"), group1);
		var outsideGroup1 = CreateItem(Path.Combine(folderB, "video1_dup.mkv"), group1);

		var sameFolderGroup2 = CreateItem(Path.Combine(folderA, "video2.mkv"), group2);
		var outsideGroup2 = CreateItem(Path.Combine(folderC, "video2_dup.mkv"), group2);

		var visibleItems = new[] { anchor, sameFolderGroup1, outsideGroup1, sameFolderGroup2, outsideGroup2 };

		IReadOnlyList<DuplicateItemVM> hits = MainWindowVM.ComputeSameFolderSharedGroupHits(visibleItems, anchor);

		Assert.Equal(3, hits.Count);
		Assert.Contains(anchor, hits);
		Assert.Contains(sameFolderGroup1, hits);
		Assert.Contains(sameFolderGroup2, hits);
		Assert.DoesNotContain(outsideGroup1, hits);
		Assert.DoesNotContain(outsideGroup2, hits);
	}

	[Fact]
	public void Action3_SelectSameGroupOtherHits_MatchesOtherMembersInGroup_ExcludesAnchor() {
		Guid group1 = Guid.NewGuid();
		Guid group2 = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";

		var anchor = CreateItem(Path.Combine(folderA, "video1.mkv"), group1);
		var sameFolderSibling = CreateItem(Path.Combine(folderA, "video1_sibling.mkv"), group1);
		var otherFolderMember = CreateItem(Path.Combine(folderB, "video1_other.mkv"), group1);
		var otherGroupMember = CreateItem(Path.Combine(folderA, "video2.mkv"), group2);

		var visibleItems = new[] { anchor, sameFolderSibling, otherFolderMember, otherGroupMember };

		IReadOnlyList<DuplicateItemVM> hits = MainWindowVM.ComputeSameGroupOtherHits(visibleItems, anchor);

		Assert.Equal(2, hits.Count);
		Assert.Contains(sameFolderSibling, hits);
		Assert.Contains(otherFolderMember, hits);
		Assert.DoesNotContain(anchor, hits);
		Assert.DoesNotContain(otherGroupMember, hits);
	}

	[Fact]
	public void SameFolderOnlyGroup_ExcludedFromCrossFolderActions() {
		Guid group1 = Guid.NewGuid();
		Guid group2 = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";

		var anchor = CreateItem(Path.Combine(folderA, "v1.mkv"), group1);
		var outsideGroup1 = CreateItem(Path.Combine(folderB, "v1_dup.mkv"), group1);

		var sameFolderOnly1 = CreateItem(Path.Combine(folderA, "v2_a.mkv"), group2);
		var sameFolderOnly2 = CreateItem(Path.Combine(folderA, "v2_b.mkv"), group2);

		var visibleItems = new[] { anchor, outsideGroup1, sameFolderOnly1, sameFolderOnly2 };

		IReadOnlyList<DuplicateItemVM> outsideHits = MainWindowVM.ComputeOtherFolderSharedGroupHits(visibleItems, anchor);
		IReadOnlyList<DuplicateItemVM> insideHits = MainWindowVM.ComputeSameFolderSharedGroupHits(visibleItems, anchor);

		Assert.Single(outsideHits);
		Assert.Contains(outsideGroup1, outsideHits);

		Assert.Single(insideHits);
		Assert.Contains(anchor, insideHits);

		Assert.DoesNotContain(sameFolderOnly1, outsideHits);
		Assert.DoesNotContain(sameFolderOnly2, outsideHits);
		Assert.DoesNotContain(sameFolderOnly1, insideHits);
		Assert.DoesNotContain(sameFolderOnly2, insideHits);
	}

	[Fact]
	public void UnrelatedGroup_WithoutAnchorFolder_ExcludedFromCrossFolderActions() {
		Guid group1 = Guid.NewGuid();
		Guid group2 = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";
		string folderC = CoreUtils.IsWindows ? @"C:\Media\FolderC" : "/media/FolderC";

		var anchor = CreateItem(Path.Combine(folderA, "v1.mkv"), group1);
		var outsideGroup1 = CreateItem(Path.Combine(folderB, "v1_dup.mkv"), group1);

		var unrelated1 = CreateItem(Path.Combine(folderB, "v2_b.mkv"), group2);
		var unrelated2 = CreateItem(Path.Combine(folderC, "v2_c.mkv"), group2);

		var visibleItems = new[] { anchor, outsideGroup1, unrelated1, unrelated2 };

		IReadOnlyList<DuplicateItemVM> outsideHits = MainWindowVM.ComputeOtherFolderSharedGroupHits(visibleItems, anchor);
		IReadOnlyList<DuplicateItemVM> insideHits = MainWindowVM.ComputeSameFolderSharedGroupHits(visibleItems, anchor);

		Assert.Single(outsideHits);
		Assert.Contains(outsideGroup1, outsideHits);
		Assert.DoesNotContain(unrelated1, outsideHits);
		Assert.DoesNotContain(unrelated2, outsideHits);

		Assert.Single(insideHits);
		Assert.Contains(anchor, insideHits);
		Assert.DoesNotContain(unrelated1, insideHits);
		Assert.DoesNotContain(unrelated2, insideHits);
	}

	[Fact]
	public void DifferentBasename_SameGroupId_QualifiesForSelection() {
		Guid group = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";

		var anchor = CreateItem(Path.Combine(folderA, "Episode.01.720p.mkv"), group);
		var outside = CreateItem(Path.Combine(folderB, "Ep01.Remux.1080p.mp4"), group);

		var visibleItems = new[] { anchor, outside };

		IReadOnlyList<DuplicateItemVM> outsideHits = MainWindowVM.ComputeOtherFolderSharedGroupHits(visibleItems, anchor);
		IReadOnlyList<DuplicateItemVM> insideHits = MainWindowVM.ComputeSameFolderSharedGroupHits(visibleItems, anchor);

		Assert.Single(outsideHits);
		Assert.Same(outside, outsideHits[0]);

		Assert.Single(insideHits);
		Assert.Same(anchor, insideHits[0]);
	}

	[Fact]
	public void Action3_AnchorCheckedStateUntouched_AndAdditive() {
		Guid group = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";

		var anchorChecked = CreateItem(Path.Combine(folderA, "anchor.mkv"), group, isChecked: true);
		var siblingUnchecked = CreateItem(Path.Combine(folderA, "sibling.mkv"), group, isChecked: false);
		var outsideChecked = CreateItem(Path.Combine(folderB, "outside.mkv"), group, isChecked: true);

		var visibleItems = new[] { anchorChecked, siblingUnchecked, outsideChecked };
		IReadOnlyList<DuplicateItemVM> hits = MainWindowVM.ComputeSameGroupOtherHits(visibleItems, anchorChecked);

		Assert.Equal(2, hits.Count);
		Assert.DoesNotContain(anchorChecked, hits);

		foreach (var hit in hits)
			hit.Checked = true;

		Assert.True(anchorChecked.Checked);
		Assert.True(siblingUnchecked.Checked);
		Assert.True(outsideChecked.Checked);
	}

	[Fact]
	public void AdditiveSelection_PreservesPreExistingSelectionsOutsideTargetSet() {
		Guid group1 = Guid.NewGuid();
		Guid group2 = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";

		var anchor = CreateItem(Path.Combine(folderA, "a1.mkv"), group1, isChecked: false);
		var outside1 = CreateItem(Path.Combine(folderB, "b1.mkv"), group1, isChecked: false);
		var unrelatedPreChecked = CreateItem(Path.Combine(folderB, "unrelated.mkv"), group2, isChecked: true);

		var visibleItems = new[] { anchor, outside1, unrelatedPreChecked };
		IReadOnlyList<DuplicateItemVM> outsideHits = MainWindowVM.ComputeOtherFolderSharedGroupHits(visibleItems, anchor);

		Assert.Single(outsideHits);
		Assert.Same(outside1, outsideHits[0]);

		foreach (var hit in outsideHits)
			hit.Checked = true;

		Assert.False(anchor.Checked);
		Assert.True(outside1.Checked);
		Assert.True(unrelatedPreChecked.Checked);
	}

	[Fact]
	public void WindowsCaseInsensitive_AndPathSeparatorsNormalization() {
		Guid group = Guid.NewGuid();

		string pathA = @"C:\Media\SubFolder\file1.mkv";
		string pathASameFolderAltCase = @"c:/media/subfolder/file2.mkv";
		string pathB = @"C:\Media\OtherFolder\file3.mkv";

		var itemA = CreateItem(pathA, group);
		var itemASame = CreateItem(pathASameFolderAltCase, group);
		var itemB = CreateItem(pathB, group);

		string keyA = MainWindowVM.ResultFolderKey(itemA);
		string keyASame = MainWindowVM.ResultFolderKey(itemASame);

		Assert.Equal("C:/Media/SubFolder", keyA);
		Assert.Equal("c:/media/subfolder", keyASame);

		if (CoreUtils.IsWindows) {
			IReadOnlyList<DuplicateItemVM> outsideHits = MainWindowVM.ComputeOtherFolderSharedGroupHits(
				new[] { itemA, itemASame, itemB }, itemA);
			Assert.Single(outsideHits);
			Assert.Same(itemB, outsideHits[0]);

			IReadOnlyList<DuplicateItemVM> insideHits = MainWindowVM.ComputeSameFolderSharedGroupHits(
				new[] { itemA, itemASame, itemB }, itemA);
			Assert.Equal(2, insideHits.Count);
			Assert.Contains(itemA, insideHits);
			Assert.Contains(itemASame, insideHits);
		}
	}

	[Fact]
	public void DifferentDrive_AndSubfolder_TreatedAsDifferentFolders() {
		Guid group = Guid.NewGuid();

		var rootItem = CreateItem(@"C:\Media\file1.mkv", group);
		var subfolderItem = CreateItem(@"C:\Media\Sub\file2.mkv", group);
		var differentDriveItem = CreateItem(@"D:\Media\file3.mkv", group);

		string rootKey = MainWindowVM.ResultFolderKey(rootItem);
		string subKey = MainWindowVM.ResultFolderKey(subfolderItem);
		string driveKey = MainWindowVM.ResultFolderKey(differentDriveItem);

		Assert.Equal("C:/Media", rootKey);
		Assert.Equal("C:/Media/Sub", subKey);
		Assert.Equal("D:/Media", driveKey);

		Assert.NotEqual(rootKey, subKey);
		Assert.NotEqual(rootKey, driveKey);

		IReadOnlyList<DuplicateItemVM> outsideHits = MainWindowVM.ComputeOtherFolderSharedGroupHits(
			new[] { rootItem, subfolderItem, differentDriveItem }, rootItem);

		Assert.Equal(2, outsideHits.Count);
		Assert.Contains(subfolderItem, outsideHits);
		Assert.Contains(differentDriveItem, outsideHits);
		Assert.DoesNotContain(rootItem, outsideHits);
	}

	[Fact]
	public void UNCPaths_DistinguishesServers() {
		Guid group = Guid.NewGuid();

		var unc1 = CreateItem(@"\\server1\share\folder\file1.mkv", group);
		var unc2 = CreateItem(@"\\server2\share\folder\file2.mkv", group);

		string key1 = MainWindowVM.ResultFolderKey(unc1);
		string key2 = MainWindowVM.ResultFolderKey(unc2);

		Assert.NotEqual(key1, key2);

		IReadOnlyList<DuplicateItemVM> outsideHits = MainWindowVM.ComputeOtherFolderSharedGroupHits(
			new[] { unc1, unc2 }, unc1);

		Assert.Single(outsideHits);
		Assert.Same(unc2, outsideHits[0]);
	}

	[Fact]
	public void EmptyAndNullSafeguards_ReturnEmptyListsSafely() {
		Guid group = Guid.NewGuid();
		var item = CreateItem(@"C:\Media\file.mkv", group);

		Assert.Empty(MainWindowVM.ComputeOtherFolderSharedGroupHits(Array.Empty<DuplicateItemVM>(), item));
		Assert.Empty(MainWindowVM.ComputeSameFolderSharedGroupHits(Array.Empty<DuplicateItemVM>(), item));
		Assert.Empty(MainWindowVM.ComputeSameGroupOtherHits(Array.Empty<DuplicateItemVM>(), item));

		Assert.Empty(MainWindowVM.ComputeOtherFolderSharedGroupHits(new[] { item }, null!));
		Assert.Empty(MainWindowVM.ComputeSameFolderSharedGroupHits(new[] { item }, null!));
		Assert.Empty(MainWindowVM.ComputeSameGroupOtherHits(new[] { item }, null!));

		var loneItem = CreateItem(@"C:\Media\lone.mkv", Guid.NewGuid());
		Assert.Empty(MainWindowVM.ComputeSameGroupOtherHits(new[] { loneItem }, loneItem));

		var noInfoItem = new DuplicateItemVM();
		Assert.Empty(MainWindowVM.ComputeOtherFolderSharedGroupHits(new[] { item }, noInfoItem));
		Assert.Empty(MainWindowVM.ComputeSameFolderSharedGroupHits(new[] { item }, noInfoItem));
		Assert.Empty(MainWindowVM.ComputeSameGroupOtherHits(new[] { item }, noInfoItem));
	}

	[Fact]
	public void ResultsGroupHeader_OverloadsMatchItemOverloads() {
		Guid group1 = Guid.NewGuid();
		Guid group2 = Guid.NewGuid();

		string folderA = CoreUtils.IsWindows ? @"C:\Media\FolderA" : "/media/FolderA";
		string folderB = CoreUtils.IsWindows ? @"C:\Media\FolderB" : "/media/FolderB";

		var anchor = CreateItem(Path.Combine(folderA, "v1.mkv"), group1);
		var outside1 = CreateItem(Path.Combine(folderB, "v1_dup.mkv"), group1);
		var sameFolder2 = CreateItem(Path.Combine(folderA, "v2.mkv"), group2);
		var outside2 = CreateItem(Path.Combine(folderB, "v2_dup.mkv"), group2);

		var g1 = CreateGroup(group1, anchor, outside1);
		var g2 = CreateGroup(group2, sameFolder2, outside2);
		var groups = new[] { g1, g2 };

		IReadOnlyList<DuplicateItemVM> outsideHits = MainWindowVM.ComputeOtherFolderSharedGroupHits(groups, anchor);
		IReadOnlyList<DuplicateItemVM> insideHits = MainWindowVM.ComputeSameFolderSharedGroupHits(groups, anchor);
		IReadOnlyList<DuplicateItemVM> sameGroupHits = MainWindowVM.ComputeSameGroupOtherHits(groups, anchor);

		Assert.Equal(2, outsideHits.Count);
		Assert.Contains(outside1, outsideHits);
		Assert.Contains(outside2, outsideHits);

		Assert.Equal(2, insideHits.Count);
		Assert.Contains(anchor, insideHits);
		Assert.Contains(sameFolder2, insideHits);

		Assert.Single(sameGroupHits);
		Assert.Same(outside1, sameGroupHits[0]);
	}

	[Fact]
	public async Task EnsureBulkCheckMenu_CreatesAllMenuItems_WithTagsHeadersAndCommands() {
		using var session = Avalonia.Application.Current == null
			? HeadlessUnitTestSession.StartNew(typeof(VDF.GUI.App))
			: null;

		var tcs = new TaskCompletionSource();
		void Run() {
			var vm = new MainWindowVM();
			var item1 = CreateItem(@"C:\Media\f1.mkv", Guid.NewGuid());
			var row1 = new ResultsItemRow(item1);

			var menu = new ContextMenu();
			// Pre-populate with an existing menu item like XAML does
			var existingItem = new MenuItem { Header = "OpenFile" };
			menu.Items.Add(existingItem);

			DuplicateResultsView.EnsureBulkCheckMenu(menu, vm, row1);

			var menuItems = menu.Items.OfType<MenuItem>().ToList();
			Assert.Equal(6, menuItems.Count); // 5 bulk-check items + 1 preexisting

			var folderHits = menuItems.FirstOrDefault(m => Equals(m.Tag, DuplicateResultsView.CheckFolderHitsMenuTag));
			var otherFolderHits = menuItems.FirstOrDefault(m => Equals(m.Tag, DuplicateResultsView.CheckOtherFolderGroupHitsMenuTag));
			var otherSharedHits = menuItems.FirstOrDefault(m => Equals(m.Tag, DuplicateResultsView.SelectOtherFolderSharedGroupHitsMenuTag));
			var sameSharedHits = menuItems.FirstOrDefault(m => Equals(m.Tag, DuplicateResultsView.SelectSameFolderSharedGroupHitsMenuTag));
			var sameGroupHits = menuItems.FirstOrDefault(m => Equals(m.Tag, DuplicateResultsView.SelectSameGroupOtherHitsMenuTag));

			Assert.NotNull(folderHits);
			Assert.NotNull(otherFolderHits);
			Assert.NotNull(otherSharedHits);
			Assert.NotNull(sameSharedHits);
			Assert.NotNull(sameGroupHits);

			Assert.Equal("勾选此文件夹下所有命中文件", folderHits!.Header);
			Assert.Equal("勾选其他文件夹中与此文件同组的命中文件", otherFolderHits!.Header);
			Assert.Equal("选择其他路径与此文件夹中项目相同的文件", otherSharedHits!.Header);
			Assert.Equal("选择此文件夹中与其他路径相同的文件", sameSharedHits!.Header);
			Assert.Equal("选择组内其他", sameGroupHits!.Header);

			Assert.NotNull(folderHits.Command);
			Assert.NotNull(otherFolderHits.Command);
			Assert.NotNull(otherSharedHits.Command);
			Assert.NotNull(sameSharedHits.Command);
			Assert.NotNull(sameGroupHits.Command);

			Assert.Same(item1, folderHits.CommandParameter);
			Assert.Same(item1, otherFolderHits.CommandParameter);
			Assert.Same(item1, otherSharedHits.CommandParameter);
			Assert.Same(item1, sameSharedHits.CommandParameter);
			Assert.Same(item1, sameGroupHits.CommandParameter);

			// Separator verification
			var separator = menu.Items.OfType<Separator>().FirstOrDefault(s => Equals(s.Tag, DuplicateResultsView.BulkCheckSeparatorTag));
			Assert.NotNull(separator);
			tcs.SetResult();
		}

		if (session != null) {
			await session.Dispatch(Run);
		} else {
			Run();
		}
		await tcs.Task;
	}

	[Fact]
	public async Task EnsureBulkCheckMenu_IsIdempotent_RefreshesParameterOnSubsequentCalls() {
		using var session = Avalonia.Application.Current == null
			? HeadlessUnitTestSession.StartNew(typeof(VDF.GUI.App))
			: null;

		var tcs = new TaskCompletionSource();
		void Run() {
			var vm = new MainWindowVM();
			var item1 = CreateItem(@"C:\Media\f1.mkv", Guid.NewGuid());
			var row1 = new ResultsItemRow(item1);

			var item2 = CreateItem(@"C:\Media\f2.mkv", Guid.NewGuid());
			var row2 = new ResultsItemRow(item2);

			var menu = new ContextMenu();
			var existingItem = new MenuItem { Header = "OpenFile" };
			menu.Items.Add(existingItem);

			// First call with row1
			DuplicateResultsView.EnsureBulkCheckMenu(menu, vm, row1);
			int countAfterFirst = menu.Items.Count;

			// Second call with row2
			DuplicateResultsView.EnsureBulkCheckMenu(menu, vm, row2);
			int countAfterSecond = menu.Items.Count;

			Assert.Equal(countAfterFirst, countAfterSecond);

			var menuItems = menu.Items.OfType<MenuItem>().ToList();
			var folderHits = menuItems.First(m => Equals(m.Tag, DuplicateResultsView.CheckFolderHitsMenuTag));
			var otherFolderHits = menuItems.First(m => Equals(m.Tag, DuplicateResultsView.CheckOtherFolderGroupHitsMenuTag));
			var otherSharedHits = menuItems.First(m => Equals(m.Tag, DuplicateResultsView.SelectOtherFolderSharedGroupHitsMenuTag));
			var sameSharedHits = menuItems.First(m => Equals(m.Tag, DuplicateResultsView.SelectSameFolderSharedGroupHitsMenuTag));
			var sameGroupHits = menuItems.First(m => Equals(m.Tag, DuplicateResultsView.SelectSameGroupOtherHitsMenuTag));

			Assert.Same(item2, folderHits.CommandParameter);
			Assert.Same(item2, otherFolderHits.CommandParameter);
			Assert.Same(item2, otherSharedHits.CommandParameter);
			Assert.Same(item2, sameSharedHits.CommandParameter);
			Assert.Same(item2, sameGroupHits.CommandParameter);
			tcs.SetResult();
		}

		if (session != null) {
			await session.Dispatch(Run);
		} else {
			Run();
		}
		await tcs.Task;
	}
}
