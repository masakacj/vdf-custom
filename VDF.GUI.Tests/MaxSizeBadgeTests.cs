// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Collections;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class MaxSizeBadgeTests {
	static DuplicateItemVM Item(Guid group, string path, long size) => new() {
		ItemInfo = new DuplicateItem {
			GroupId = group,
			Path = path,
			SizeLong = size,
			Similarity = 99,
			Duration = TimeSpan.FromMinutes(1),
		}
	};

	static ResultsBuildResult Build(params DuplicateItemVM[] items) =>
		ResultsListBuilder.Build(new ResultsBuildRequest {
			Items = items,
			IsTombstone = _ => false,
			IsOffline = _ => false,
		});

	[Fact]
	public void MarksLargestMembersIncludingTies() {
		Guid group = Guid.NewGuid();
		var small = Item(group, "small.mkv", 100);
		var maxA = Item(group, "max-a.mkv", 900);
		var maxB = Item(group, "max-b.mkv", 900);

		ResultsBuildResult result = Build(small, maxA, maxB);

		Assert.False(result.Groups[0].Rows.Single(row => ReferenceEquals(row.Item, small)).IsMaxSize);
		Assert.True(result.Groups[0].Rows.Single(row => ReferenceEquals(row.Item, maxA)).IsMaxSize);
		Assert.True(result.Groups[0].Rows.Single(row => ReferenceEquals(row.Item, maxB)).IsMaxSize);
	}

	[Fact]
	public void ReusedRowRefreshesMaxSizeState() {
		Guid group = Guid.NewGuid();
		var a = Item(group, "a.mkv", 200);
		var b = Item(group, "b.mkv", 100);
		ResultsBuildResult first = Build(a, b);
		var currentRows = new AvaloniaList<object>();
		currentRows.AddRange(first.Rows);
		ResultsItemRow oldA = first.Rows.OfType<ResultsItemRow>().Single(row => ReferenceEquals(row.Item, a));
		Assert.True(oldA.IsMaxSize);

		a.ItemInfo.SizeLong = 50;
		b.ItemInfo.SizeLong = 300;
		ResultsBuildResult second = Build(a, b);
		ResultsRowReconciler.ReuseItemRows(currentRows, second, new HashSet<DuplicateItemVM>());

		ResultsItemRow reusedA = second.Rows.OfType<ResultsItemRow>().Single(row => ReferenceEquals(row.Item, a));
		ResultsItemRow rowB = second.Rows.OfType<ResultsItemRow>().Single(row => ReferenceEquals(row.Item, b));
		Assert.Same(oldA, reusedA);
		Assert.False(reusedA.IsMaxSize);
		Assert.True(rowB.IsMaxSize);
	}
}
