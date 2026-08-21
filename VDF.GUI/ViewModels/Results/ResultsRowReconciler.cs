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
		/// Applies a desired row sequence without resetting the collection. Stable item
		/// rows are moved rather than recreated; headers/details are replaced in place when
		/// their logical position survives. This avoids invalidating every virtualized row.
		/// </summary>
		internal static void Apply(AvaloniaList<object> target, IReadOnlyList<object> desiredRows) {
			for (int i = 0; i < desiredRows.Count; i++) {
				object desired = desiredRows[i];
				if (i < target.Count && ReferenceEquals(target[i], desired)) continue;

				int existingIndex = IndexOfReference(target, desired, i + 1);
				if (existingIndex >= 0) {
					target.Move(existingIndex, i);
					continue;
				}

				if (i >= target.Count) {
					target.Add(desired);
					continue;
				}

				object current = target[i];
				if (ReferenceAppearsLater(desiredRows, current, i + 1))
					target.Insert(i, desired);
				else
					target[i] = desired;
			}

			while (target.Count > desiredRows.Count)
				target.RemoveAt(target.Count - 1);
		}

		static int IndexOfReference(IReadOnlyList<object> rows, object value, int start) {
			for (int i = Math.Max(0, start); i < rows.Count; i++)
				if (ReferenceEquals(rows[i], value)) return i;
			return -1;
		}

		static bool ReferenceAppearsLater(IReadOnlyList<object> rows, object value, int start) {
			for (int i = Math.Max(0, start); i < rows.Count; i++)
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
