// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Collections;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class ResultsRowReconcilerTests {
	static DuplicateItemVM Item(Guid group, string path, long size = 100) => new() {
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
	public void ReuseItemRows_PreservesFileRowIdentityAcrossRebuild() {
		Guid group = Guid.NewGuid();
		var a = Item(group, "a.mkv", 200);
		var b = Item(group, "b.mkv", 100);
		var first = Build(a, b);
		var oldRows = new AvaloniaList<object>();
		oldRows.AddRange(first.Rows);
		ResultsItemRow oldA = first.Rows.OfType<ResultsItemRow>().Single(row => ReferenceEquals(row.Item, a));

		var second = Build(a, b);
		ResultsRowReconciler.ReuseItemRows(oldRows, second, new HashSet<DuplicateItemVM>());
		ResultsItemRow newA = second.Rows.OfType<ResultsItemRow>().Single(row => ReferenceEquals(row.Item, a));

		Assert.Same(oldA, newA);
		Assert.Same(second.Groups[0], newA.Group);
		Assert.Contains(newA, second.Groups[0].Rows);
	}

	[Fact]
	public void SameStructure_IgnoresFreshHeadersButTracksLogicalRows() {
		Guid group = Guid.NewGuid();
		var a = Item(group, "a.mkv", 200);
		var b = Item(group, "b.mkv", 100);
		var first = Build(a, b);
		var second = Build(a, b);
		ResultsRowReconciler.ReuseItemRows(first.Rows, second, new HashSet<DuplicateItemVM>());

		Assert.True(ResultsRowReconciler.HasSameStructure(first.Rows, second.Rows));

		var third = Build(a, b);
		third.Rows.RemoveAt(third.Rows.Count - 1);
		Assert.False(ResultsRowReconciler.HasSameStructure(first.Rows, third.Rows));
	}

	[Fact]
	public void Apply_MovesStableRowsWithoutResettingThem() {
		var a = new object();
		var b = new object();
		var c = new object();
		var rows = new AvaloniaList<object> { a, b, c };

		ResultsRowReconciler.Apply(rows, new[] { c, a, b });

		Assert.Equal(3, rows.Count);
		Assert.Same(c, rows[0]);
		Assert.Same(a, rows[1]);
		Assert.Same(b, rows[2]);
	}
}
