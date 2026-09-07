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
		/// Large result mutations are normally local: a huge unchanged prefix/suffix surrounds
		/// one changed duplicate group. Detect those edges in O(N), then run the original
		/// move-preserving reconciliation only inside the small middle span. This keeps stable
		/// virtualized row containers (Move rather than remove/reinsert) while avoiding the old
		/// whole-list O(N²) searches for a deletion near the top of a 200k-group result set.
		/// Global reorder operations can still have a large middle span, but those are explicit
		/// full-list operations rather than the common single-group delete/edit path.
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

			// Logical identity may survive through a freshly built presentation header. Refresh
			// those edge objects in place; reused ResultsItemRow references stay untouched.
			for (int i = 0; i < prefix; i++)
				if (!ReferenceEquals(target[i], desiredRows[i]))
					target[i] = desiredRows[i];

			int desiredMiddleEnd = newCount - suffix;
			for (int i = prefix; i < desiredMiddleEnd; i++) {
				object desired = desiredRows[i];
				if (i < target.Count && ReferenceEquals(target[i], desired)) continue;

				int targetMiddleEnd = target.Count - suffix;
				int existingIndex = IndexOfReference(target, desired, i + 1, targetMiddleEnd);
				if (existingIndex >= 0) {
					target.Move(existingIndex, i);
					continue;
				}

				// No old-middle row is left at this position; insert before the preserved suffix.
				if (i >= target.Count - suffix) {
					target.Insert(i, desired);
					continue;
				}

				object current = target[i];
				if (ReferenceAppearsLater(desiredRows, current, i + 1, desiredMiddleEnd))
					target.Insert(i, desired);
				else
					target[i] = desired;
			}

			// Remove only obsolete middle rows. The common suffix has deliberately been kept
			// alive and slides left automatically as rows before it are removed.
			while (target.Count - suffix > desiredMiddleEnd)
				target.RemoveAt(desiredMiddleEnd);

			// Headers/details in the logical suffix may be newly built objects with the same
			// identity. Replace those presentation objects in place while preserving stable rows.
			for (int i = 0; i < suffix; i++) {
				int index = desiredMiddleEnd + i;
				object desired = desiredRows[index];
				if (!ReferenceEquals(target[index], desired))
					target[index] = desired;
			}
		}

		static int IndexOfReference(IReadOnlyList<object> rows, object value, int start, int endExclusive) {
			int end = Math.Min(rows.Count, Math.Max(0, endExclusive));
			for (int i = Math.Max(0, start); i < end; i++)
				if (ReferenceEquals(rows[i], value)) return i;
			return -1;
		}

		static bool ReferenceAppearsLater(IReadOnlyList<object> rows, object value, int start, int endExclusive) {
			int end = Math.Min(rows.Count, Math.Max(0, endExclusive));
			for (int i = Math.Max(0, start); i < end; i++)
				if (ReferenceEquals(rows[i], value)) return true;
			return false;
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
