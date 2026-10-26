// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class FullGroupDeleteWarningTests {
	static DuplicateItemVM Item(Guid group, string path) => new() {
		ItemInfo = new DuplicateItem {
			GroupId = group,
			Path = path,
			SizeLong = 100,
			Similarity = 99,
			Duration = TimeSpan.FromMinutes(1),
		}
	};

	[Fact]
	public void DoesNotWarnWhenCanonicalGroupStillHasASurvivor() {
		Guid group = Guid.NewGuid();
		var a = Item(group, @"D:\videos\a.mp4");
		var b = Item(group, @"D:\videos\b.mp4");
		var hiddenSurvivor = Item(group, @"D:\videos\hidden.mp4");

		var found = FullGroupDeleteGuard.FindFullySelectedGroups(
			new[] { a, b, hiddenSurvivor },
			new[] { a, b });

		Assert.Empty(found);
	}

	[Fact]
	public void FindsEveryFullySelectedDuplicateGroupButNotSingletons() {
		Guid firstGroup = Guid.NewGuid();
		Guid secondGroup = Guid.NewGuid();
		Guid singletonGroup = Guid.NewGuid();
		var firstA = Item(firstGroup, @"D:\one\a.mp4");
		var firstB = Item(firstGroup, @"D:\one\b.mp4");
		var secondA = Item(secondGroup, @"D:\two\a.mp4");
		var secondB = Item(secondGroup, @"D:\two\b.mp4");
		var singleton = Item(singletonGroup, @"D:\single.mp4");
		var all = new[] { firstA, firstB, secondA, secondB, singleton };

		var found = FullGroupDeleteGuard.FindFullySelectedGroups(all, all);

		Assert.Equal(2, found.Count);
		Assert.Equal(firstGroup, found[0].GroupId);
		Assert.Equal(2, found[0].ItemCount);
		Assert.Equal(secondGroup, found[1].GroupId);
	}

	[Fact]
	public void WarningListsGroupsAndCapsVeryLongMessages() {
		var groups = Enumerable.Range(1, FullGroupDeleteGuard.MaxDisplayedGroups + 2)
			.Select(i => new FullySelectedDeleteGroup(Guid.NewGuid(), i + 1, $@"D:\group-{i}\sample.mp4"))
			.ToList();

		string message = FullGroupDeleteGuard.BuildWarningMessage(groups, "zh-Hans");

		Assert.Contains($"有 {groups.Count} 个重复组", message);
		Assert.Contains(@"D:\group-1\sample.mp4", message);
		Assert.Contains($"另有 {groups.Count - FullGroupDeleteGuard.MaxDisplayedGroups} 个整组全选", message);
		Assert.DoesNotContain($@"D:\group-{groups.Count}\sample.mp4", message);
		Assert.Contains("仍要继续从磁盘删除吗？", message);
	}
}
