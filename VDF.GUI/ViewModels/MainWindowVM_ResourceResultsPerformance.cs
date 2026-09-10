// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Threading;
using Avalonia.Threading;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		const int BackgroundResourceResultsThreshold = 20_000;

		readonly record struct ResourceCoverageCacheSignature(
			int CollectionVersion,
			string ScanRoots,
			string QualityOrder,
			bool QualityDiagnostics);

		IReadOnlyList<PikPakFolderCoverageOption>? resourceCoverageOptionsCache;
		ResourceCoverageCacheSignature? resourceCoverageOptionsSignature;
		CancellationTokenSource? resourceResultsCancellation;
		int resourceResultsGeneration;
		bool resourceResultsOwnsBusyOverlay;

		ResourceCoverageCacheSignature CaptureResourceCoverageSignature() {
			// Reuse the lightweight collection-version tracker already used by the large-result
			// filter worker. MarkResultItemMutation also advances this version for in-place path
			// changes, so a rename/move cannot leave stale folder relations in this cache.
			EnsureFilterResultsVersionTracking();
			string roots = string.Join('\u001f', Scanner.Settings.IncludeList
				.Select(NormalizePikPakPath)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
			string qualityOrder = string.Join('\u001f', QualityCriteriaOrder);
			return new ResourceCoverageCacheSignature(
				Volatile.Read(ref filterResultsCollectionVersion),
				roots,
				qualityOrder,
				EnableLightweightQualityDiagnostics);
		}

		bool TryGetCachedResourceCoverage(
			ResourceCoverageCacheSignature signature,
			out IReadOnlyList<PikPakFolderCoverageOption> options) {
			if (resourceCoverageOptionsCache != null && resourceCoverageOptionsSignature == signature) {
				options = resourceCoverageOptionsCache;
				return true;
			}
			options = Array.Empty<PikPakFolderCoverageOption>();
			return false;
		}

		IReadOnlyList<PikPakFolderCoverageOption> GetOrBuildResourceCoverageOptions() {
			ResourceCoverageCacheSignature signature = CaptureResourceCoverageSignature();
			if (TryGetCachedResourceCoverage(signature, out var cached))
				return cached;

			var items = Duplicates.ToList(); // references only
			var options = BuildResourceCoverageOptionsSnapshot(items, CancellationToken.None);
			// Do not publish a cache computed across a concurrent result mutation.
			if (signature == CaptureResourceCoverageSignature()) {
				resourceCoverageOptionsCache = options;
				resourceCoverageOptionsSignature = signature;
			}
			return options;
		}

		IReadOnlyList<PikPakFolderCoverageOption> BuildResourceCoverageOptionsSnapshot(
			IReadOnlyList<DuplicateItemVM> items,
			CancellationToken token) {
			if (items.Count == 0)
				return Array.Empty<PikPakFolderCoverageOption>();

			var groupsById = new Dictionary<Guid, List<DuplicateItemVM>>();
			for (int i = 0; i < items.Count; i++) {
				if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
				DuplicateItemVM item = items[i];
				Guid groupId = item.ItemInfo.GroupId;
				if (!groupsById.TryGetValue(groupId, out List<DuplicateItemVM>? group))
					groupsById[groupId] = group = new List<DuplicateItemVM>(2);
				group.Add(item);
			}

			var groups = groupsById.Values.Where(group => group.Count >= 2).ToList();
			if (groups.Count == 0)
				return Array.Empty<PikPakFolderCoverageOption>();
			token.ThrowIfCancellationRequested();

			var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (List<DuplicateItemVM> group in groups) {
				foreach (DuplicateItemVM item in group) {
					string folder = string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
						? GetPikPakFolder(item.ItemInfo.Path)
						: item.ItemInfo.Folder;
					if (!string.IsNullOrWhiteSpace(folder))
						folders.Add(folder);
				}
			}
			token.ThrowIfCancellationRequested();

			// This is the formerly UI-blocking whole-database pass. It still uses only the
			// already-loaded VDF database (no media/file enumeration), but runs on this worker
			// for large result sets and is cached after the first successful calculation.
			var stats = Scanner.GetDirectFolderMediaStats(folders);
			token.ThrowIfCancellationRequested();
			var options = ComputePikPakFolderCoverageOptions(groups, stats);
			token.ThrowIfCancellationRequested();
			return options;
		}

		/// <summary>
		/// Switching result presentation does not change filtering, BEST, sort order or the
		/// canonical similarity groups. Reuse that already-built model instead of rebuilding
		/// hundreds of thousands of groups solely to change their visual hierarchy.
		/// </summary>
		void RefreshResultsDisplayModePresentation() {
			// A similarity-mode filter worker started immediately before the mode switch must
			// not paint its old flattened rows over the new folder view later.
			filterResultsCancellation?.Cancel();
			Interlocked.Increment(ref filterResultsGeneration);
			filterResultsRefreshRequested = false;

			if (ActiveResultsDisplayMode == ResultsDisplayMode.SimilarityGroups) {
				CancelResourceResultsBuild(clearBusy: true);
				ApplySimilarityPresentationFromCanonical();
				return;
			}

			if (resultsGroups.Count == 0) {
				CancelResourceResultsBuild(clearBusy: true);
				ResultsRows.Clear();
				return;
			}

			ResourceCoverageCacheSignature signature = CaptureResourceCoverageSignature();
			if (TryGetCachedResourceCoverage(signature, out var cached)) {
				CancelResourceResultsBuild(clearBusy: true);
				ApplyResourcePresentation(cached);
				return;
			}

			if (Duplicates.Count >= BackgroundResourceResultsThreshold) {
				StartResourceResultsBuild(signature);
				return;
			}

			CancelResourceResultsBuild(clearBusy: true);
			IReadOnlyList<PikPakFolderCoverageOption> options = GetOrBuildResourceCoverageOptions();
			ApplyResourcePresentation(options);
		}

		void StartResourceResultsBuild(ResourceCoverageCacheSignature signature) {
			CancelResourceResultsBuild(clearBusy: false);
			var cts = resourceResultsCancellation = new CancellationTokenSource();
			CancellationToken token = cts.Token;
			int generation = Interlocked.Increment(ref resourceResultsGeneration);
			var items = Duplicates.ToList(); // UI-thread reference snapshot only

			resourceResultsOwnsBusyOverlay = true;
			IsBusyOverlayText = "正在构建文件夹列表…";
			IsBusy = true;

			_ = Task.Run(() => {
				try {
				{
					IReadOnlyList<PikPakFolderCoverageOption> options =
						BuildResourceCoverageOptionsSnapshot(items, token);
					token.ThrowIfCancellationRequested();
					Dispatcher.UIThread.Post(() => {
						if (token.IsCancellationRequested || generation != Volatile.Read(ref resourceResultsGeneration))
							return;
						if (ActiveResultsDisplayMode != ResultsDisplayMode.ResourceConsolidation) {
							EndResourceResultsBusyOverlay();
							return;
						}
						if (signature != CaptureResourceCoverageSignature()) {
							// Data changed while the worker was scanning the database. Never display stale
							// folder relations; immediately restart from the newest reference snapshot.
							StartResourceResultsBuild(CaptureResourceCoverageSignature());
							return;
						}
						resourceCoverageOptionsCache = options;
						resourceCoverageOptionsSignature = signature;
						ApplyResourcePresentation(options);
						EndResourceResultsBusyOverlay();
					}, DispatcherPriority.Background);
				}
				catch (OperationCanceledException) { }
				catch (Exception ex) {
					VDF.Core.Utils.Logger.Instance.Error($"Background folder-results build failed: {ex}");
					Dispatcher.UIThread.Post(() => {
						if (generation == Volatile.Read(ref resourceResultsGeneration))
							EndResourceResultsBusyOverlay();
					});
				}
			}, token);
		}

		void CancelResourceResultsBuild(bool clearBusy) {
			Interlocked.Increment(ref resourceResultsGeneration);
			CancellationTokenSource? cts = resourceResultsCancellation;
			resourceResultsCancellation = null;
			cts?.Cancel();
			cts?.Dispose();
			if (clearBusy)
				EndResourceResultsBusyOverlay();
		}

		void EndResourceResultsBusyOverlay() {
			if (!resourceResultsOwnsBusyOverlay) return;
			resourceResultsOwnsBusyOverlay = false;
			IsBusy = false;
		}

		void ApplySimilarityPresentationFromCanonical() {
			var rows = new List<object>(resultsGroups.Count * 2);
			foreach (ResultsGroupHeader group in resultsGroups) {
				rows.Add(group);
				if (group.IsCollapsed) continue;
				foreach (ResultsItemRow row in group.Rows) {
					rows.Add(row);
					if (expandedResultsDetails.Contains(row.Item))
						rows.Add(new ResultsDetailsRow(row));
				}
			}
			ResultsRowReconciler.Apply(ResultsRows, rows);
			lastResultsIncrementalSignature = CaptureResultsIncrementalSignature();
			this.RaisePropertyChanged(nameof(ResultsShowClipOffsetColumn));
			if (rows.Count > 0)
				ResultsScrollToRow?.Invoke(rows[0], 0d);
		}

		void ApplyResourcePresentation(IReadOnlyList<PikPakFolderCoverageOption> options) {
			ResourceResultsBuildResult resource = ResourceResultsBuilder.Build(
				resultsGroups, options, expandedResultsDetails);
			ResultsRowReconciler.Apply(ResultsRows, resource.Rows);
			lastResultsIncrementalSignature = CaptureResultsIncrementalSignature();
			this.RaisePropertyChanged(nameof(ResultsShowClipOffsetColumn));
			if (resource.Rows.Count > 0)
				ResultsScrollToRow?.Invoke(resource.Rows[0], 0d);
		}

		/// <summary>
		/// Expanding one relation changes only the contiguous child span immediately below
		/// that header. Never rebuild canonical groups, folder coverage, or the DB statistics.
		/// </summary>
		internal bool TryRefreshResourceRelationPresentation(ResourceRelationHeader header) {
			if (ActiveResultsDisplayMode != ResultsDisplayMode.ResourceConsolidation || header == null)
				return false;

			int headerIndex = ResultsRows.IndexOf(header);
			if (headerIndex < 0)
				return false;

			int end = headerIndex + 1;
			while (end < ResultsRows.Count &&
				ResultsRows[end] is not ResourceRelationHeader &&
				ResultsRows[end] is not ResourceUnassignedHeader)
				end++;

			int oldChildCount = end - headerIndex - 1;
			if (oldChildCount > 0)
				ResultsRows.RemoveRange(headerIndex + 1, oldChildCount);

			if (header.IsExpanded) {
				List<object> children = ResourceResultsBuilder.BuildExpandedRows(header, expandedResultsDetails);
				if (children.Count > 0)
					ResultsRows.InsertRange(headerIndex + 1, children);
			}
			return true;
		}
	}
}
