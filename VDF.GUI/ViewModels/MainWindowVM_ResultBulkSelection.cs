// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Reactive;
using ReactiveUI;
using VDF.Core.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		static string ResultFolderKey(DuplicateItemVM item) {
			string folder = !string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
				? item.ItemInfo.Folder
				: Path.GetDirectoryName(item.ItemInfo.Path) ?? string.Empty;
			return NormalizeFolderStatsKey(folder);
		}

		IEnumerable<DuplicateItemVM> EnumerateVisibleResultItems() =>
			resultsGroups.SelectMany(group => group.Rows.Select(row => row.Item));

		public ReactiveCommand<DuplicateItemVM, Unit> CheckFolderResultHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				var matches = ComputeFolderResultHits(EnumerateVisibleResultItems(), anchor);
				if (matches.Count == 0) return;
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in matches)
						item.Checked = true;
				}
				RefreshAfterContextBulkCheck();
			});

		public ReactiveCommand<DuplicateItemVM, Unit> CheckOtherFolderGroupHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				ResultsGroupHeader? visibleGroup = resultsGroups.FirstOrDefault(group => group.GroupId == anchor.ItemInfo.GroupId);
				if (visibleGroup == null) return;
				var matches = ComputeOtherFolderGroupHits(visibleGroup.Rows.Select(row => row.Item), anchor);
				if (matches.Count == 0) return;
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in matches)
						item.Checked = true;
				}
				RefreshAfterContextBulkCheck();
			});

		void RefreshAfterContextBulkCheck() {
			// Normal result rows bind Checked live. Only the checked-groups filter/sort changes
			// list structure and therefore needs a rebuild after a bulk check operation.
			if (FilterGroupsWithCheckedItems || SettingsFile.Instance.ResultsSortMode == ResultsSortMode.GroupsWithCheckedItems)
				RebuildResultsList();
		}

		internal static IReadOnlyList<DuplicateItemVM> ComputeFolderResultHits(
			IEnumerable<DuplicateItemVM> visibleItems, DuplicateItemVM anchor) {
			string key = ResultFolderKey(anchor);
			StringComparison comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			return visibleItems.Where(item => ResultFolderKey(item).Equals(key, comparison)).ToList();
		}

		internal static IReadOnlyList<DuplicateItemVM> ComputeOtherFolderGroupHits(
			IEnumerable<DuplicateItemVM> visibleItems, DuplicateItemVM anchor) {
			string key = ResultFolderKey(anchor);
			Guid groupId = anchor.ItemInfo.GroupId;
			StringComparison comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			return visibleItems
				.Where(item => item.ItemInfo.GroupId == groupId && !ResultFolderKey(item).Equals(key, comparison))
				.ToList();
		}
	}
}
