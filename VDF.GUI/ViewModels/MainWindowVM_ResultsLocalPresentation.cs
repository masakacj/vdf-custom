// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Linq;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		/// <summary>
		/// Collapse/expand is presentation-only. Rebuilding every group used to rerun BEST,
		/// filtering, folder statistics and disk-status work for a click that only changes one
		/// group's flattened rows. Rebuild the affected canonical group and splice that tiny
		/// span into the existing Avalonia list instead.
		/// </summary>
		bool TryRefreshSingleGroupPresentation(Guid groupId) {
			if (ActiveResultsDisplayMode != ResultsDisplayMode.SimilarityGroups)
				return false;

			int groupIndex = resultsGroups.FindIndex(group => group.GroupId == groupId);
			if (groupIndex < 0)
				return false;

			ResultsGroupHeader oldHeader = resultsGroups[groupIndex];
			var items = oldHeader.Rows.Select(row => row.Item).ToList();
			if (items.Count < 2)
				return false;

			ResultsBuildResult partial = ResultsListBuilder.Build(new ResultsBuildRequest {
				Items = items,
				// These are the already-filtered members of the currently visible group.
				// Collapse/expand cannot change filter membership, so do not touch the global
				// path/group filter indexes here.
				Filter = _ => true,
				SortMode = SettingsFile.Instance.ResultsSortMode,
				SortDescending = SettingsFile.Instance.ResultsSortDescending,
				BestFirst = SettingsFile.Instance.ResultsBestFirst,
				CollapsedGroups = collapsedResultsGroups,
				ExpandedDetails = expandedResultsDetails,
				RecommendBest = members => RecommendBestUsingCurrentRules(members),
				Formats = BuildGroupSummaryFormats(),
			});
			if (partial.Groups.Count != 1)
				return false;

			ApplyFolderStats(partial.Groups);
			ResultsRowReconciler.ReuseItemRows(ResultsRows, partial, expandedResultsDetails);
			ResultsGroupHeader freshHeader = partial.Groups[0];
			freshHeader.GroupNumber = oldHeader.GroupNumber;
			freshHeader.Title = oldHeader.Title;
			string warning = BuildLightweightQualityGroupSummary(freshHeader);
			if (warning.Length > 0)
				freshHeader.Summary += " · " + warning;

			int start = -1;
			for (int i = 0; i < ResultsRows.Count; i++) {
				if (ReferenceEquals(ResultsRows[i], oldHeader) ||
					ResultsRows[i] is ResultsGroupHeader candidate && candidate.GroupId == groupId) {
					start = i;
					break;
				}
			}
			if (start < 0)
				return false;

			resultsGroups[groupIndex] = freshHeader;
			ResultsRows[start] = freshHeader;

			// Remove the old member/details span, stopping before the next group header.
			while (start + 1 < ResultsRows.Count && ResultsRows[start + 1] is not ResultsGroupHeader)
				ResultsRows.RemoveAt(start + 1);

			// partial.Rows begins with the replacement header already installed above.
			for (int i = 1; i < partial.Rows.Count; i++)
				ResultsRows.Insert(start + i, partial.Rows[i]);

			return true;
		}

		/// <summary>
		/// Expanding one file's metadata only inserts/removes one details row. Avoid a full
		/// result rebuild, which is especially expensive with hundreds of thousands of groups.
		/// </summary>
		bool TryRefreshItemDetailsPresentation(DuplicateItemVM item) {
			if (ActiveResultsDisplayMode != ResultsDisplayMode.SimilarityGroups)
				return false;

			ResultsItemRow? row = null;
			foreach (ResultsGroupHeader group in resultsGroups) {
				row = group.Rows.FirstOrDefault(candidate => ReferenceEquals(candidate.Item, item));
				if (row != null) break;
			}
			if (row == null)
				return false;

			int rowIndex = -1;
			for (int i = 0; i < ResultsRows.Count; i++) {
				if (ReferenceEquals(ResultsRows[i], row)) {
					rowIndex = i;
					break;
				}
			}

			// A collapsed group deliberately has no item row in the flattened list. Keep the
			// expandedDetails state; when the group is opened, the local group refresh emits it.
			if (rowIndex < 0)
				return true;

			bool shouldShow = expandedResultsDetails.Contains(item);
			bool isShowing = rowIndex + 1 < ResultsRows.Count &&
				ResultsRows[rowIndex + 1] is ResultsDetailsRow details && ReferenceEquals(details.Item, item);
			if (shouldShow == isShowing)
				return true;

			if (shouldShow)
				ResultsRows.Insert(rowIndex + 1, new ResultsDetailsRow(row));
			else
				ResultsRows.RemoveAt(rowIndex + 1);
			return true;
		}
	}
}
