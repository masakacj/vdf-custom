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
		readonly Dictionary<Guid, List<DuplicateItemVM>> resultsVisibleItemsByGroup = new();
		readonly Dictionary<string, List<DuplicateItemVM>> resultsVisibleItemsByFolder = new(
			CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

		void RebuildResultSelectionIndexes(IReadOnlyList<ResultsGroupHeader> groups) {
			resultsVisibleItemsByGroup.Clear();
			resultsVisibleItemsByFolder.Clear();
			foreach (ResultsGroupHeader group in groups) {
				var members = group.Rows.Select(row => row.Item).ToList();
				if (members.Count == 0) continue;
				resultsVisibleItemsByGroup[group.GroupId] = members;
				foreach (DuplicateItemVM item in members) {
					string folder = ResultFolderKey(item);
					if (!resultsVisibleItemsByFolder.TryGetValue(folder, out var folderItems))
						resultsVisibleItemsByFolder[folder] = folderItems = new List<DuplicateItemVM>();
					folderItems.Add(item);
				}
			}
		}

		static string ResultFolderKey(DuplicateItemVM item) {
			string folder = !string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
				? item.ItemInfo.Folder
				: Path.GetDirectoryName(item.ItemInfo.Path) ?? string.Empty;
			return NormalizeFolderStatsKey(folder);
		}

		public ReactiveCommand<DuplicateItemVM, Unit> CheckFolderResultHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				string folder = ResultFolderKey(anchor);
				if (!resultsVisibleItemsByFolder.TryGetValue(folder, out var matches) || matches.Count == 0)
					return;
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in matches)
						item.Checked = true;
				}
				RefreshAfterContextBulkCheck();
			});

		public ReactiveCommand<DuplicateItemVM, Unit> CheckOtherFolderGroupHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				if (!resultsVisibleItemsByGroup.TryGetValue(anchor.ItemInfo.GroupId, out var group) || group.Count == 0)
					return;
				string currentFolder = ResultFolderKey(anchor);
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in group)
						if (!ResultFolderKey(item).Equals(currentFolder,
							CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
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
