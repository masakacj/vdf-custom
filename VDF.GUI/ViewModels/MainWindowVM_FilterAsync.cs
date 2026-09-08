// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Threading;
using Avalonia.Threading;
using ReactiveUI;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		const int BackgroundFilterThreshold = 20_000;
		bool filterResultsRefreshRequested;
		CancellationTokenSource? filterResultsCancellation;
		int filterResultsGeneration;
		int filterResultsCollectionVersion;
		bool filterResultsVersionTrackingInstalled;

		internal void RequestFilterResultsRefresh() => filterResultsRefreshRequested = true;

		void EnsureFilterResultsVersionTracking() {
			if (filterResultsVersionTrackingInstalled) return;
			Duplicates.CollectionChanged += (_, __) => Interlocked.Increment(ref filterResultsCollectionVersion);
			filterResultsVersionTrackingInstalled = true;
		}

		/// <summary>
		/// Large global filters used to run path matching, GroupBy, BEST ranking, sorting and
		/// disk-status probes synchronously on the Avalonia UI thread. For large result sets,
		/// snapshot lightweight inputs, do the expensive pure build on a worker, cancel stale
		/// requests, and only reconcile the finished rows on the UI thread.
		/// </summary>
		bool TryStartAsyncFilterResultsRefresh() {
			if (!filterResultsRefreshRequested)
				return false;

			// Resource consolidation has additional whole-collection relationship state that is
			// deliberately kept on the UI path for now. SimilarityGroups is the common 200k-group
			// browsing/filtering path and gets the cancellable background build.
			if (ActiveResultsDisplayMode != ResultsDisplayMode.SimilarityGroups || Duplicates.Count < BackgroundFilterThreshold) {
				filterResultsRefreshRequested = false;
				_groupsWithPathHit = BuildPathHitGroups(Duplicates, FilterByPath);
				return false;
			}

			filterResultsRefreshRequested = false;
			EnsureFilterResultsVersionTracking();
			filterResultsCancellation?.Cancel();
			filterResultsCancellation?.Dispose();
			var cts = filterResultsCancellation = new CancellationTokenSource();
			CancellationToken token = cts.Token;
			int generation = Interlocked.Increment(ref filterResultsGeneration);
			int collectionVersion = Volatile.Read(ref filterResultsCollectionVersion);

			var items = Duplicates.ToList(); // references only; no DB/file content is copied
			string pathFilter = FilterByPath ?? string.Empty;
			FileTypeFilter fileType = FileType.Value;
			int similarityFrom = FilterSimilarityFrom;
			int similarityTo = FilterSimilarityTo;
			bool checkedOnly = FilterGroupsWithCheckedItems;
			var checkedGroups = checkedCountByGroup.Keys.ToHashSet();
			ResultsSortMode sortMode = SettingsFile.Instance.ResultsSortMode;
			bool sortDescending = SettingsFile.Instance.ResultsSortDescending;
			bool bestFirst = SettingsFile.Instance.ResultsBestFirst;
			var collapsed = collapsedResultsGroups.ToHashSet();
			var expanded = expandedResultsDetails.ToHashSet(ReferenceEqualityComparer<DuplicateItemVM>.Instance);
			var qualityOrder = QualityCriteriaOrder.ToArray();
			GroupSummaryFormats formats = BuildGroupSummaryFormats();

			_ = Task.Run(() => {
				try {
					token.ThrowIfCancellationRequested();
					HashSet<Guid> pathHitGroups = BuildPathHitGroups(items, pathFilter, token);
					int predicateCounter = 0;
					bool Filter(DuplicateItemVM item) {
						if ((predicateCounter++ & 4095) == 0) token.ThrowIfCancellationRequested();
						bool ok = string.IsNullOrEmpty(pathFilter) || pathHitGroups.Contains(item.ItemInfo.GroupId);
						if (ok && fileType != FileTypeFilter.All)
							ok = fileType == FileTypeFilter.Images ? item.ItemInfo.IsImage : !item.ItemInfo.IsImage;
						if (ok)
							ok = item.ItemInfo.Similarity >= similarityFrom && item.ItemInfo.Similarity <= similarityTo;
						if (ok && checkedOnly)
							ok = checkedGroups.Contains(item.ItemInfo.GroupId);
						item.IsVisibleInFilter = ok;
						return ok;
					}

					ResultsBuildResult result = ResultsListBuilder.Build(new ResultsBuildRequest {
						Items = items,
						Filter = Filter,
						SortMode = sortMode,
						SortDescending = sortDescending,
						BestFirst = bestFirst,
						CollapsedGroups = collapsed,
						ExpandedDetails = expanded,
						RecommendBest = members => RecommendBest(members, qualityOrder),
						Formats = formats,
					});
					token.ThrowIfCancellationRequested();

					Dispatcher.UIThread.Post(() => ApplyAsyncFilterResult(
						generation, collectionVersion, pathHitGroups, result, token));
				}
				catch (OperationCanceledException) { }
				catch (Exception ex) {
					VDF.Core.Utils.Logger.Instance.Error($"Background result filter failed: {ex}");
				}
			}, token);
			return true;
		}

		void ApplyAsyncFilterResult(
			int generation,
			int collectionVersion,
			HashSet<Guid> pathHitGroups,
			ResultsBuildResult result,
			CancellationToken token) {
			if (token.IsCancellationRequested || generation != Volatile.Read(ref filterResultsGeneration))
				return;
			if (collectionVersion != Volatile.Read(ref filterResultsCollectionVersion)) {
				// The result set changed while the worker was building. Never paint a stale snapshot;
				// queue one fresh pass instead.
				RequestFilterResultsRefresh();
				RefreshResultsView();
				return;
			}

			_groupsWithPathHit = pathHitGroups;
			List<Guid> oldGroupOrder = resultsGroups.ConvertAll(group => group.GroupId);
			ApplyFolderStats(result.Groups);
			ResultsRowReconciler.ReuseItemRows(ResultsRows, result, expandedResultsDetails);
			resultsGroups = result.Groups;
			resultsHavePartialClips = result.HasPartialClips;
			foreach (ResultsGroupHeader group in resultsGroups) {
				string warning = BuildLightweightQualityGroupSummary(group);
				if (warning.Length > 0)
					group.Summary += " · " + warning;
			}

			var displayRows = new List<object>(result.Rows);
			bool sameStructure = ResultsRowReconciler.HasSameStructure(ResultsRows, displayRows);
			ResultsScrollAnchor.Capture? anchor = sameStructure ? null : ResultsAnchorProvider?.Invoke();
			ResultsRowReconciler.Apply(ResultsRows, displayRows);
			this.RaisePropertyChanged(nameof(ResultsShowClipOffsetColumn));
			if (anchor is { } a && ResultsScrollAnchor.FindRestoreTarget(a.Row, oldGroupOrder, displayRows) is { } target)
				ResultsScrollToRow?.Invoke(target, a.ViewportOffsetY);
			AfterFullResultsRebuild();
			PrimeFastGroupStats();
		}
	}
}
