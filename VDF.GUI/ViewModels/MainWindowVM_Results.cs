// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Linq;
using System.Reactive;
using Avalonia.Collections;
using Avalonia.Input.Platform;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public sealed record ResultsSortOption(string Name, ResultsSortMode Mode);

	// Flattened results view (redesign Stage 1; the classic DataGrid view was retired
	// in Stage 6).
	public partial class MainWindowVM : ReactiveObject {

		/// <summary>
		/// The rendered list. Traditional mode is the unchanged VDF GroupId list; resource
		/// mode adds folder-relation headers above those SAME canonical groups.
		/// </summary>
		public AvaloniaList<object> ResultsRows { get; } = new();

		readonly HashSet<Guid> collapsedResultsGroups = new();
		readonly HashSet<DuplicateItemVM> expandedResultsDetails = new();
		/// <summary>
		/// Canonical traditional groups of the last build. This deliberately NEVER becomes
		/// the resource-folder grouping: navigation and PikPak quick rules keep their stable,
		/// original file-group semantics no matter which presentation the user selects.
		/// </summary>
		List<ResultsGroupHeader> resultsGroups = new();
		bool resultsHavePartialClips;
		ResultsViewSwitcherRow? resultsViewSwitcher;
		IReadOnlyDictionary<string, FolderMediaStats>? resultsFolderStatsCache;

		/// <summary>
		/// Stable, fixed-toolbar controller for switching between similarity groups and
		/// folder consolidation. It used to be recreated as the first scrollable result
		/// row on every rebuild, which made the list less stable and hid the mode control
		/// once the user scrolled down.
		/// </summary>
		public ResultsViewSwitcherRow ResultsViewSwitcher => resultsViewSwitcher ??= new(
			ResultsDisplayModeOptions,
			ActiveResultsDisplayMode,
			SetResultsDisplayMode);

		ResultsDisplayMode _ActiveResultsDisplayMode = ResultsDisplayMode.SimilarityGroups;
		public ResultsDisplayMode ActiveResultsDisplayMode {
			get => _ActiveResultsDisplayMode;
			private set => this.RaiseAndSetIfChanged(ref _ActiveResultsDisplayMode, value);
		}

		public ResultsDisplayModeOption[] ResultsDisplayModeOptions { get; } = {
			new("相似文件组", ResultsDisplayMode.SimilarityGroups),
			new("文件夹合并", ResultsDisplayMode.ResourceConsolidation),
		};

		internal void SetResultsDisplayMode(ResultsDisplayMode mode) {
			if (mode == ActiveResultsDisplayMode) return;
			ActiveResultsDisplayMode = mode;
			RebuildResultsList();
		}

		/// <summary>
		/// The Clip offset column only exists when it can carry data: partial-clip
		/// detection is enabled, or the current results actually contain partial clips.
		/// </summary>
		public bool ResultsShowClipOffsetColumn =>
			SettingsFile.Instance.EnablePartialClipDetection || resultsHavePartialClips;

		internal Func<List<DuplicateItemVM>>? NewResultsSelectionProvider;
		internal Action<ResultsItemRow>? NewResultsSelectAndScrollTo;
		internal Func<ResultsScrollAnchor.Capture?>? ResultsAnchorProvider;
		internal Action<object, double>? ResultsScrollToRow;

		public ResultsSortOption[] ResultsSortOptions { get; } = {
			new(App.Lang["Results.Sort.WastedSpace"], ResultsSortMode.WastedSpace),
			new(App.Lang["Results.Sort.TotalSize"], ResultsSortMode.TotalSize),
			new(App.Lang["Results.Sort.LargestFile"], ResultsSortMode.LargestFile),
			new(App.Lang["Results.Sort.FileCount"], ResultsSortMode.FileCount),
			new(App.Lang["Results.Sort.Similarity"], ResultsSortMode.Similarity),
			new(App.Lang["Results.Sort.DateCreated"], ResultsSortMode.DateCreated),
			new(App.Lang["Results.Sort.Duration"], ResultsSortMode.Duration),
			new(App.Lang["Results.Sort.Resolution"], ResultsSortMode.Resolution),
			new(App.Lang["Results.Sort.FolderPath"], ResultsSortMode.FolderPath),
			new(App.Lang["Results.Sort.GroupsWithChecked"], ResultsSortMode.GroupsWithCheckedItems),
		};

		public ResultsSortOption SelectedResultsSort {
			get => ResultsSortOptions.FirstOrDefault(o => o.Mode == SettingsFile.Instance.ResultsSortMode) ?? ResultsSortOptions[0];
			set {
				if (value == null || value.Mode == SettingsFile.Instance.ResultsSortMode) return;
				SettingsFile.Instance.ResultsSortMode = value.Mode;
				this.RaisePropertyChanged(nameof(SelectedResultsSort));
				RebuildResultsList();
			}
		}

		public bool ResultsSortDescending {
			get => SettingsFile.Instance.ResultsSortDescending;
			set {
				if (value == SettingsFile.Instance.ResultsSortDescending) return;
				SettingsFile.Instance.ResultsSortDescending = value;
				this.RaisePropertyChanged(nameof(ResultsSortDescending));
				RebuildResultsList();
			}
		}

		public bool ResultsBestFirst {
			get => SettingsFile.Instance.ResultsBestFirst;
			set {
				if (value == SettingsFile.Instance.ResultsBestFirst) return;
				SettingsFile.Instance.ResultsBestFirst = value;
				this.RaisePropertyChanged(nameof(ResultsBestFirst));
				RebuildResultsList();
			}
		}

		internal static string BestBadgeTooltip(VDF.Core.Utils.QualityRanker.Criterion<DuplicateItemVM>? decidedBy) {
			if (decidedBy == null)
				return App.Lang["Results.Row.BestTipTied"];
			if (decidedBy.Name == LightweightQualityDiagnostics.QualityCriterionName)
				return "BEST：轻量画质诊断优先避开了疑似二次转码/放大或固定水印版本。该判断仅用于保留建议，不会自动删除文件。";
			return string.Format(App.Lang["Results.Row.BestTip"], App.Lang[$"QualityCriteria.{decidedBy.Name}"]);
		}

		GroupSummaryFormats BuildGroupSummaryFormats() => new() {
			GroupTitle = App.Lang["Results.GroupTitle"],
			Files = App.Lang["Results.Summary.Files"],
			SingleFile = App.Lang["Results.Summary.SingleFile"],
			SaveUpTo = App.Lang["Results.Summary.SaveUpTo"],
			OnDisk = App.Lang["Results.Summary.OnDisk"],
			PreviouslyDeleted = App.Lang["Results.Summary.PreviouslyDeleted"],
		};

		/// <summary>Rebuilds the flattened list from the current duplicates, filter and sort.</summary>
		internal void RebuildResultsList() {
			List<Guid> oldGroupOrder = resultsGroups.ConvertAll(g => g.GroupId);

			var result = ResultsListBuilder.Build(new ResultsBuildRequest {
				Items = Duplicates.ToList(),
				Filter = DuplicatesFilterCore,
				SortMode = SettingsFile.Instance.ResultsSortMode,
				SortDescending = SettingsFile.Instance.ResultsSortDescending,
				BestFirst = SettingsFile.Instance.ResultsBestFirst,
				CollapsedGroups = collapsedResultsGroups,
				ExpandedDetails = expandedResultsDetails,
				// Every group receives one most-likely BEST recommendation. IsConfirmed is
				// separately carried by the result row and remains the gate for unattended work.
				RecommendBest = members => RecommendBestUsingCurrentRules(members),
				Formats = BuildGroupSummaryFormats(),
			});
			ApplyFolderStats(result.Groups);
			// Preserve ResultsItemRow identity before any presentation-specific grouping
			// consumes the canonical rows. This lets the virtualized ListBox retain realized
			// file containers through ordinary selection/BEST/status refreshes.
			ResultsRowReconciler.ReuseItemRows(ResultsRows, result, expandedResultsDetails);
			resultsGroups = result.Groups;
			resultsHavePartialClips = result.HasPartialClips;

			foreach (var group in resultsGroups) {
				string warning = BuildLightweightQualityGroupSummary(group);
				if (warning.Length > 0)
					group.Summary += " · " + warning;
			}

			var displayRows = new List<object>();

			if (ActiveResultsDisplayMode == ResultsDisplayMode.ResourceConsolidation) {
				var options = BuildResourceCoverageOptions(result.Groups);
				var resource = ResourceResultsBuilder.Build(result.Groups, options, expandedResultsDetails);
				displayRows.AddRange(resource.Rows);
			}
			else {
				displayRows.AddRange(result.Rows);
			}

			// Presentation-only refreshes (for example checkbox/BEST state changes) keep
			// the same logical row sequence. In that case there is no reason to capture or
			// force a scroll position at all. Structural changes still use the anchor as a
			// fallback, but the collection itself is reconciled incrementally rather than reset.
			bool sameStructure = ResultsRowReconciler.HasSameStructure(ResultsRows, displayRows);
			ResultsScrollAnchor.Capture? anchor = sameStructure ? null : ResultsAnchorProvider?.Invoke();
			ResultsRowReconciler.Apply(ResultsRows, displayRows);
			this.RaisePropertyChanged(nameof(ResultsShowClipOffsetColumn));

			if (anchor is { } a && ResultsScrollAnchor.FindRestoreTarget(a.Row, oldGroupOrder, displayRows) is { } target)
				ResultsScrollToRow?.Invoke(target, a.ViewportOffsetY);
		}

		IReadOnlyList<PikPakFolderCoverageOption> BuildResourceCoverageOptions(IReadOnlyList<ResultsGroupHeader> canonicalGroups) {
			if (canonicalGroups.Count == 0)
				return Array.Empty<PikPakFolderCoverageOption>();

			// Folder identity is a level above the current presentation/filter. Build relation
			// evidence from the complete in-memory duplicate groups so hiding one item/group,
			// changing sort order, or collapsing details cannot change whether two directories
			// are considered the same collection. ResourceResultsBuilder intersects these
			// stable relations with canonicalGroups when deciding which level-2 details to show.
			var groups = Duplicates
				.GroupBy(item => item.ItemInfo.GroupId)
				.Select(group => group.ToList())
				.Where(group => group.Count >= 2)
				.ToList();
			if (groups.Count == 0)
				return Array.Empty<PikPakFolderCoverageOption>();

			var folders = groups
				.SelectMany(group => group)
				.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
					? GetPikPakFolder(item.ItemInfo.Path)
					: item.ItemInfo.Folder)
				.Where(folder => !string.IsNullOrWhiteSpace(folder))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			var stats = Scanner.GetDirectFolderMediaStats(folders);
			return ComputePikPakFolderCoverageOptions(groups, stats);
		}

		internal void RefreshResultsView() => RebuildResultsList();

		void ApplyFolderStats(IReadOnlyList<ResultsGroupHeader> groups) {
			if (resultsFolderStatsCache == null) {
				var folders = Duplicates
					.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
						? Path.GetDirectoryName(item.ItemInfo.Path) ?? string.Empty
						: item.ItemInfo.Folder)
					.Where(folder => !string.IsNullOrWhiteSpace(folder))
					.Distinct(CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
					.ToList();
				resultsFolderStatsCache = Scanner.GetExactFolderMediaStats(folders);
			}

			foreach (ResultsItemRow row in groups.SelectMany(group => group.Rows)) {
				string folder = string.IsNullOrWhiteSpace(row.Item.ItemInfo.Folder)
					? Path.GetDirectoryName(row.Item.ItemInfo.Path) ?? string.Empty
					: row.Item.ItemInfo.Folder;
				string key = NormalizeFolderStatsKey(folder);
				row.FolderStatsText = resultsFolderStatsCache.TryGetValue(key, out FolderMediaStats stats) && stats.FileCount > 0
					? $"{stats.FileCount:N0} 文件 · {stats.TotalBytes.BytesToString()}"
					: string.Empty;
			}
		}

		static string NormalizeFolderStatsKey(string? folder) {
			string value = (folder ?? string.Empty).Trim().Replace('\\', '/');
			while (value.Length > 1 && value.EndsWith("/", StringComparison.Ordinal))
				value = value[..^1];
			return value;
		}

		void BuildActiveResultsView(bool resetSessionStats = true) {
			resultsFolderStatsCache = null;
			RunLightweightQualityDiagnostics();
			RebuildResultsList();
			if (resetSessionStats)
				TotalSizeRemovedInternal = 0;
		}

		public ReactiveCommand<ResultsGroupHeader, Unit> ToggleGroupCollapsedCommand => ReactiveCommand.Create<ResultsGroupHeader>(header => {
			if (header == null) return;
			if (!collapsedResultsGroups.Remove(header.GroupId))
				collapsedResultsGroups.Add(header.GroupId);
			RebuildResultsList();
		});

		public ReactiveCommand<DuplicateItemVM, Unit> ToggleItemDetailsCommand => ReactiveCommand.Create<DuplicateItemVM>(item => {
			if (item == null) return;
			if (!expandedResultsDetails.Remove(item))
				expandedResultsDetails.Add(item);
			RebuildResultsList();
		});

		public ReactiveCommand<DuplicateItemVM, Unit> CopyItemDetailsCommand => ReactiveCommand.CreateFromTask<DuplicateItemVM>(async item => {
			if (item == null) return;
			if (ApplicationHelpers.MainWindow.Clipboard is { } clipboard) {
				string text = ResultsBadgeRules.BuildDetailsText(item.ItemInfo);
				string warning = LightweightQualityDiagnostics.WarningText(item);
				if (warning.Length > 0) text += Environment.NewLine + "Quality: " + warning;
				await clipboard.SetTextAsync(text);
			}
		});

		public ReactiveCommand<Unit, Unit> DismissResultsHintCommand => ReactiveCommand.Create(() => {
			SettingsFile.Instance.ResultsHintDismissed = true;
		});

		public ReactiveCommand<ResultsGroupHeader, Unit> CompareGroupHeaderCommand => ReactiveCommand.Create<ResultsGroupHeader>(header => {
			if (header != null) CompareGroup(header.GroupId);
		});

		/// <summary>
		/// Explicit user action: uses the current recommendation even when the system marks it
		/// as review-needed. The user is deliberately choosing "keep recommended BEST" here.
		/// </summary>
		public ReactiveCommand<ResultsGroupHeader, Unit> KeepBestInGroupHeaderCommand => ReactiveCommand.Create<ResultsGroupHeader>(header => {
			if (header == null) return;
			var members = header.Rows.Select(row => row.Item).ToList();
			if (members.Count < 2) return;
			BestRecommendation recommendation = RecommendBestUsingCurrentRules(members);
			using (BeginSelectionUndoBatch()) {
				foreach (DuplicateItemVM item in members)
					item.Checked = !ReferenceEquals(item, recommendation.Winner);
			}
			// Checked is a live property on DuplicateItemVM; counters/action bar update from
			// PropertyChanged. Rebuilding every result group here made a two-file click scan
			// the entire result set and folder relations, causing the visible pause reported
			// on large databases. Only the special checked-group sort needs a structural refresh.
			if (SettingsFile.Instance.ResultsSortMode == ResultsSortMode.GroupsWithCheckedItems)
				RebuildResultsList();
		});

		public ReactiveCommand<ResultsGroupHeader, Unit> MarkGroupHeaderNotAMatchCommand => ReactiveCommand.CreateFromTask<ResultsGroupHeader>(async header => {
			if (header != null) await MarkGroupAsNotAMatch(header.GroupId);
		});

		public ReactiveCommand<ResultsGroupHeader, Unit> LoadThumbnailsForGroupHeaderCommand => ReactiveCommand.CreateFromTask<ResultsGroupHeader>(async header => {
			if (header == null) return;
			var items = Duplicates.Where(d => d.ItemInfo.GroupId == header.GroupId).Select(d => d.ItemInfo).ToList();
			if (items.Count == 0) return;
			SyncCoreSettings();
			await Scanner.RetrieveThumbnailsForItems(items);
		});

		public ReactiveCommand<Unit, Unit> ToggleResultsDensityCommand => ReactiveCommand.Create(() => {
			SettingsFile.Instance.ResultsCompactRows = !SettingsFile.Instance.ResultsCompactRows;
		});

		Guid? NavigateGroupNewView(bool forward, Guid? fromGroupId = null) {
			if (resultsGroups.Count == 0) return null;

			Guid? referenceGroupId = fromGroupId ?? GetSelectedDuplicateItem()?.ItemInfo.GroupId;
			int currentIndex = -1;
			if (referenceGroupId.HasValue)
				currentIndex = resultsGroups.FindIndex(g => g.GroupId == referenceGroupId.Value);

			int targetIndex = forward
				? (currentIndex + 1 < resultsGroups.Count ? currentIndex + 1 : 0)
				: (currentIndex - 1 >= 0 ? currentIndex - 1 : resultsGroups.Count - 1);

			var target = resultsGroups[targetIndex];
			if (target.IsCollapsed) {
				collapsedResultsGroups.Remove(target.GroupId);
				RebuildResultsList();
				target = resultsGroups.FirstOrDefault(g => g.GroupId == target.GroupId) ?? target;
			}
			var firstRow = target.Rows.FirstOrDefault();
			if (firstRow == null) return null;
			NewResultsSelectAndScrollTo?.Invoke(firstRow);
			return target.GroupId;
		}
	}
}
