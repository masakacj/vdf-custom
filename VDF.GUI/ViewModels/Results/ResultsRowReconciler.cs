// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Collections;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// Keeps the flattened results collection stable across presentation-only rebuilds.
	/// ResultsListBuilder intentionally stays pure and creates fresh row objects; this
	/// reconciler reuses file rows backed by the same DuplicateItemVM and applies the
	/// smallest practical collection changes instead of Clear()+AddRange().
	/// </summary>
	internal static class ResultsRowReconciler {
		internal static void ReuseItemRows(
			IReadOnlyList<object> currentRows,
			ResultsBuildResult build,
			IReadOnlySet<DuplicateItemVM> expandedDetails) {
			var existing = new Dictionary<DuplicateItemVM, ResultsItemRow>(
				System.Collections.Generic.ReferenceEqualityComparer.Instance);
			foreach (object row in currentRows) {
				if (row is ResultsItemRow itemRow)
					existing.TryAdd(itemRow.Item, itemRow);
			}

			foreach (ResultsGroupHeader header in build.Groups) {
				var stableRows = new List<ResultsItemRow>(header.Rows.Count);
				foreach (ResultsItemRow fresh in header.Rows) {
					if (existing.TryGetValue(fresh.Item, out ResultsItemRow? stable)) {
						stable.RefreshPresentationFrom(fresh);
						stable.Group = header;
						stableRows.Add(stable);
					}
					else {
						fresh.Group = header;
						stableRows.Add(fresh);
					}
				}
				header.RebindRows(stableRows);
			}

			// The builder's flattened list still references the fresh item rows. Recreate
			// only this cheap projection after the canonical headers have been rebound.
			build.Rows.Clear();
			foreach (ResultsGroupHeader header in build.Groups) {
				build.Rows.Add(header);
				if (header.IsCollapsed) continue;
				foreach (ResultsItemRow row in header.Rows) {
					build.Rows.Add(row);
					if (expandedDetails.Contains(row.Item))
						build.Rows.Add(new ResultsDetailsRow(row));
				}
			}
		}

		internal static bool HasSameStructure(IReadOnlyList<object> currentRows, IReadOnlyList<object> desiredRows) {
			if (currentRows.Count != desiredRows.Count) return false;
			for (int i = 0; i < currentRows.Count; i++) {
				if (!SameIdentity(currentRows[i], desiredRows[i])) return false;
			}
			return true;
		}

		/// <summary>
		/// Applies a desired row sequence without resetting the collection.
		///
		/// The old implementation searched the remainder of both collections for every
		/// mismatch. A single removed group near the top of a very large result set could
		/// therefore degrade toward O(N²). Large VDF databases can contain hundreds of
		/// thousands of groups, so that turns an otherwise tiny edit into a long UI stall.
		///
		/// Result rebuilds are normally highly similar: an unchanged prefix, a small changed
		/// region, then an unchanged suffix. Detect those identity-equivalent edges in two
		/// linear passes, replace only the middle span, and refresh newly-created header/detail
		/// objects on the stable edges. This is O(N + changedRows) and never touches the scan DB.
		/// </summary>
		internal static void Apply(AvaloniaList<object> target, IReadOnlyList<object> desiredRows) {
			int oldCount = target.Count;
			int newCount = desiredRows.Count;
			int commonLimit = Math.Min(oldCount, newCount);

			int prefix = 0;
			while (prefix < commonLimit && SameIdentity(target[prefix], desiredRows[prefix]))
				prefix++;

			int suffix = 0;
			while (suffix < commonLimit - prefix &&
				SameIdentity(target[oldCount - 1 - suffix], desiredRows[newCount - 1 - suffix]))
				suffix++;

			// Identity can be the same while the presentation object itself is new (most
			// notably ResultsGroupHeader after a rebuild). Refresh those edge slots in place;
			// stable ResultsItemRow references produced by ReuseItemRows remain untouched.
			for (int i = 0; i < prefix; i++)
				if (!ReferenceEquals(target[i], desiredRows[i]))
					target[i] = desiredRows[i];

			int oldMiddleCount = oldCount - prefix - suffix;
			int newMiddleCount = newCount - prefix - suffix;
			for (int i = 0; i < oldMiddleCount; i++)
				target.RemoveAt(prefix);
			for (int i = 0; i < newMiddleCount; i++)
				target.Insert(prefix + i, desiredRows[prefix + i]);

			int targetSuffixStart = prefix + newMiddleCount;
			int desiredSuffixStart = newCount - suffix;
			for (int i = 0; i < suffix; i++) {
				int targetIndex = targetSuffixStart + i;
				object desired = desiredRows[desiredSuffixStart + i];
				if (!ReferenceEquals(target[targetIndex], desired))
					target[targetIndex] = desired;
			}
		}

		static bool SameIdentity(object current, object desired) {
			if (ReferenceEquals(current, desired)) return true;
			return (current, desired) switch {
				(ResultsGroupHeader a, ResultsGroupHeader b) => a.GroupId == b.GroupId,
				(ResultsItemRow a, ResultsItemRow b) => ReferenceEquals(a.Item, b.Item),
				(ResultsDetailsRow a, ResultsDetailsRow b) => ReferenceEquals(a.Item, b.Item),
				(ResourceRelationHeader a, ResourceRelationHeader b) =>
					a.SelectionKey.Equals(b.SelectionKey, StringComparison.OrdinalIgnoreCase),
				(ResourceFolderContentHeader a, ResourceFolderContentHeader b) =>
					a.RoleLabel == b.RoleLabel &&
					a.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase) &&
					a.RelationRoot.Equals(b.RelationRoot, StringComparison.OrdinalIgnoreCase),
				(ResourceUnassignedHeader, ResourceUnassignedHeader) => true,
				_ => false,
			};
		}
	}
}
