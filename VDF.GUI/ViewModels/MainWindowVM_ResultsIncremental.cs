// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Collections.Specialized;
using System.Linq;
using ReactiveUI;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		readonly HashSet<Guid> resultsDirtyGroupIds = new();
		bool resultsMutationTrackingInstalled;
		bool resultsIncrementalInvalidated = true;
		ResultsIncrementalSignature? lastResultsIncrementalSignature;

		readonly record struct ResultsIncrementalSignature(
			string FileType,
			string PathFilter,
			int SimilarityFrom,
			int SimilarityTo,
			bool CheckedGroupsOnly,
			ResultsSortMode SortMode,
			bool SortDescending,
			bool BestFirst,
			ResultsDisplayMode DisplayMode,
			bool QualityDiagnostics,
			string QualityOrder);

		/// <summary>
		/// Result mutation tracking is intentionally presentation-only. It never reads or writes
		/// ScannedFiles.db and therefore cannot migrate or invalidate a long-running scan database.
		/// A Reset (new scan/import) invalidates incremental state until the next full rebuild.
		/// </summary>
		void EnsureResultsMutationTracking() {
			if (resultsMutationTrackingInstalled) return;
			Duplicates.CollectionChanged += ResultsDuplicatesCollectionChanged;
			resultsMutationTrackingInstalled = true;
		}

		void ResultsDuplicatesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
			if (e.Action == NotifyCollectionChangedAction.Reset) {
				resultsIncrementalInvalidated = true;
				resultsDirtyGroupIds.Clear();
				return;
			}

			// During a reset/import the collection may receive hundreds of thousands of Add
			// notifications. Ignore them until the next full build establishes a baseline.
			if (resultsIncrementalInvalidated) return;

			if (e.OldItems != null) {
				foreach (object? value in e.OldItems) {
					if (value is DuplicateItemVM item)
						resultsDirtyGroupIds.Add(item.ItemInfo.GroupId);
				}
			}

			if (e.NewItems != null) {
				foreach (object? value in e.NewItems) {
					if (value is DuplicateItemVM item)
						resultsDirtyGroupIds.Add(item.ItemInfo.GroupId);
				}
			}
		}

		ResultsIncrementalSignature CaptureResultsIncrementalSignature() => new(
			FileType.Name,
			FilterByPath ?? string.Empty,
			FilterSimilarityFrom,
			FilterSimilarityTo,
			FilterGroupsWithCheckedItems,
			SettingsFile.Instance.ResultsSortMode,
			SettingsFile.Instance.ResultsSortDescending,
			SettingsFile.Instance.ResultsBestFirst,
			ActiveResultsDisplayMode,
			EnableLightweightQualityDiagnostics,
			string.Join('\u001f', QualityCriteriaOrder));

		/// <summary>
		/// Called after an intentional full rebuild to establish a safe incremental baseline.
		/// Do not build a second all-results membership index here: on very large result sets that
		/// duplicates hundreds of thousands of lists during application startup.
		/// </summary>
		void AfterFullResultsRebuild() {
			resultsDirtyGroupIds.Clear();
			resultsIncrementalInvalidated = false;
			lastResultsIncrementalSignature = CaptureResultsIncrementalSignature();
			// Install the O(1)-per-mutation group-stat tracker while we already have a canonical
			// full result baseline. Reset/import invalidates it; the next intentional rebuild
			// primes it once, so later deletes/marks do not need another all-group aggregation.
			PrimeFastGroupStats();
		}

		/// <summary>
		/// Rebuilds only groups touched by collection mutations. Large global filters are routed
		/// through the cancellable background builder; ordinary local changes stay group-local.
		/// </summary>
		bool TryRefreshResultsIncrementally() {
			EnsureResultsMutationTracking();
			if (TryStartAsyncFilterResultsRefresh())
				return true;
			if (resultsIncrementalInvalidated || resultsDirtyGroupIds.Count == 0)
				return false;
			if (ActiveResultsDisplayMode != ResultsDisplayMode.SimilarityGroups)
				return false;

			ResultsIncrementalSignature signature = CaptureResultsIncrementalSignature();
			if (lastResultsIncrementalSignature is not { } previous || previous != signature)
				return false;

			// A removed/moved path can change whether an entire group matches the path search.
			// For large collections rebuild that global path hit set on the worker; small sets
			// keep the original synchronous path and then continue with the dirty-group refresh.
			if (!string.IsNullOrEmpty(FilterByPath)) {
				RequestFilterResultsRefresh();
				if (TryStartAsyncFilterResultsRefresh())
					return true;
			}

			var dirtyIds = resultsDirtyGroupIds.ToHashSet();
			var oldGroupOrder = resultsGroups.Select(group => group.GroupId).ToList();
			// Keep only the old headers for the handful of groups that are changing. The previous
			// implementation allocated a 200k-entry GroupId->order dictionary and then indexed the
			// entire flattened ResultsRows list merely to refresh one or two groups.
			var oldDirtyGroups = resultsGroups
				.Where(group => dirtyIds.Contains(group.GroupId))
				.ToDictionary(group => group.GroupId);

			// Keep startup memory flat. Resolve the handful of dirty groups with one linear pass
			// only when a local mutation actually occurs instead of maintaining a full duplicate
			// GroupId -> List index for the lifetime of the application.
			var dirtyItems = Duplicates
				.Where(item => dirtyIds.Contains(item.ItemInfo.GroupId))
				.ToList();

			var partial = ResultsListBuilder.Build(new ResultsBuildRequest {
				Items = dirtyItems,
				Filter = DuplicatesFilterCore,
				SortMode = SettingsFile.Instance.ResultsSortMode,
				SortDescending = SettingsFile.Instance.ResultsSortDescending,
				BestFirst = SettingsFile.Instance.ResultsBestFirst,
				CollapsedGroups = collapsedResultsGroups,
				ExpandedDetails = expandedResultsDetails,
				RecommendBest = members => RecommendBestUsingCurrentRules(members),
				Formats = BuildGroupSummaryFormats(),
			});
			ApplyFolderStats(partial.Groups);
			ReuseDirtyGroupItemRows(partial.Groups, oldDirtyGroups);
			foreach (ResultsGroupHeader group in partial.Groups) {
				// Preserve the old display position as a zero-allocation stable-sort tiebreaker.
				// A newly appearing local group has no previous position and therefore sorts last
				// among otherwise-equal groups until the next canonical rebuild renumbers it.
				group.GroupNumber = oldDirtyGroups.TryGetValue(group.GroupId, out ResultsGroupHeader? oldGroup)
					? oldGroup.GroupNumber
					: int.MaxValue;
				string warning = BuildLightweightQualityGroupSummary(group);
				if (warning.Length > 0)
					group.Summary += " · " + warning;
			}

			var merged = resultsGroups.Where(group => !dirtyIds.Contains(group.GroupId)).ToList();
			merged.AddRange(partial.Groups);
			SortIncrementalResultGroups(merged);

			GroupSummaryFormats formats = BuildGroupSummaryFormats();
			var displayRows = new List<object>();
			bool hasPartialClips = false;
			for (int i = 0; i < merged.Count; i++) {
				ResultsGroupHeader header = merged[i];
				header.GroupNumber = i + 1;
				header.Title = string.Format(formats.GroupTitle, header.GroupNumber);
				displayRows.Add(header);
				foreach (ResultsItemRow row in header.Rows) {
					hasPartialClips |= row.Item.ItemInfo.Flags.HasFlag(VDF.Core.DuplicateFlags.PartialClip);
					if (header.IsCollapsed) continue;
					displayRows.Add(row);
					if (expandedResultsDetails.Contains(row.Item))
						displayRows.Add(new ResultsDetailsRow(row));
				}
			}

			ResultsScrollAnchor.Capture? anchor = ResultsAnchorProvider?.Invoke();
			resultsGroups = merged;
			resultsHavePartialClips = hasPartialClips;
			ResultsRowReconciler.Apply(ResultsRows, displayRows);
			this.RaisePropertyChanged(nameof(ResultsShowClipOffsetColumn));
			if (anchor is { } a && ResultsScrollAnchor.FindRestoreTarget(a.Row, oldGroupOrder, displayRows) is { } target)
				ResultsScrollToRow?.Invoke(target, a.ViewportOffsetY);

			resultsDirtyGroupIds.Clear();
			lastResultsIncrementalSignature = signature;
			return true;
		}

		static void ReuseDirtyGroupItemRows(
			IReadOnlyList<ResultsGroupHeader> freshGroups,
			IReadOnlyDictionary<Guid, ResultsGroupHeader> oldGroups) {
			foreach (ResultsGroupHeader freshGroup in freshGroups) {
				if (!oldGroups.TryGetValue(freshGroup.GroupId, out ResultsGroupHeader? oldGroup))
					continue;

				var stableRows = new List<ResultsItemRow>(freshGroup.Rows.Count);
				foreach (ResultsItemRow freshRow in freshGroup.Rows) {
					ResultsItemRow? stable = oldGroup.Rows.FirstOrDefault(
						candidate => ReferenceEquals(candidate.Item, freshRow.Item));
					if (stable != null) {
						stable.RefreshPresentationFrom(freshRow);
						stable.Group = freshGroup;
						stableRows.Add(stable);
					}
					else {
						freshRow.Group = freshGroup;
						stableRows.Add(freshRow);
					}
				}
				freshGroup.RebindRows(stableRows);
			}
		}

		void SortIncrementalResultGroups(List<ResultsGroupHeader> groups) {
			Comparison<ResultsGroupHeader> comparison = SettingsFile.Instance.ResultsSortMode switch {
				ResultsSortMode.WastedSpace => (a, b) => a.WastedBytes.CompareTo(b.WastedBytes),
				ResultsSortMode.TotalSize => (a, b) => a.TotalBytes.CompareTo(b.TotalBytes),
				ResultsSortMode.LargestFile => (a, b) => IncrementalMaxSize(a).CompareTo(IncrementalMaxSize(b)),
				ResultsSortMode.FileCount => (a, b) => a.FileCount.CompareTo(b.FileCount),
				ResultsSortMode.Similarity => (a, b) => a.SimilarityMax.CompareTo(b.SimilarityMax),
				ResultsSortMode.DateCreated => (a, b) => IncrementalMaxDate(a).CompareTo(IncrementalMaxDate(b)),
				ResultsSortMode.Duration => (a, b) => IncrementalMaxDuration(a).CompareTo(IncrementalMaxDuration(b)),
				ResultsSortMode.Resolution => (a, b) => IncrementalMaxFrameSize(a).CompareTo(IncrementalMaxFrameSize(b)),
				ResultsSortMode.FolderPath => (a, b) => string.Compare(IncrementalFirstPath(a), IncrementalFirstPath(b), StringComparison.OrdinalIgnoreCase),
				ResultsSortMode.GroupsWithCheckedItems => (a, b) => a.HasCheckedItems.CompareTo(b.HasCheckedItems),
				_ => (a, b) => 0,
			};
			if (SettingsFile.Instance.ResultsSortDescending) {
				Comparison<ResultsGroupHeader> inner = comparison;
				comparison = (a, b) => inner(b, a);
			}
			groups.Sort((a, b) => {
				int value = comparison(a, b);
				if (value != 0) return value;
				value = a.GroupNumber.CompareTo(b.GroupNumber);
				return value != 0 ? value : a.GroupId.CompareTo(b.GroupId);
			});
		}

		static long IncrementalMaxSize(ResultsGroupHeader group) =>
			group.Rows.Count == 0 ? 0 : group.Rows.Max(row => row.Item.ItemInfo.SizeLong);
		static DateTime IncrementalMaxDate(ResultsGroupHeader group) =>
			group.Rows.Count == 0 ? DateTime.MinValue : group.Rows.Max(row => row.Item.ItemInfo.DateCreated);
		static TimeSpan IncrementalMaxDuration(ResultsGroupHeader group) =>
			group.Rows.Count == 0 ? TimeSpan.Zero : group.Rows.Max(row => row.Item.ItemInfo.Duration);
		static int IncrementalMaxFrameSize(ResultsGroupHeader group) =>
			group.Rows.Count == 0 ? 0 : group.Rows.Max(row => row.Item.ItemInfo.FrameSizeInt);
		static string IncrementalFirstPath(ResultsGroupHeader group) =>
			group.Rows.Count == 0 ? string.Empty : group.Rows[0].Item.ItemInfo.Path;
	}
}
