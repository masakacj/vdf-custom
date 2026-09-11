// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// Protects disk deletion when every canonical member of one or more duplicate
	/// groups is about to be removed. The checks deliberately use Duplicates rather
	/// than the current presentation rows, so filters and the folder-results view do
	/// not hide a surviving member and accidentally make a partial selection look full.
	/// </summary>
	public partial class MainWindowVM {
		// These compact overloads are intentional command-entry guards. Existing UI
		// commands call DeleteInternal with exactly these argument shapes; the original
		// full-parameter overload remains the deletion executor and is called below with
		// every argument supplied so it cannot recurse back into these guards.
		async void DeleteInternal(bool fromDisk) {
			if (!fromDisk) {
				DeleteInternal(false, null, false, false, false, false);
				return;
			}
			await DeleteFromDiskWithFullGroupWarningAsync(null, permanently: false);
		}

		async void DeleteInternal(bool fromDisk, bool permanently) {
			if (!fromDisk) {
				DeleteInternal(false, null, false, false, permanently, false);
				return;
			}
			await DeleteFromDiskWithFullGroupWarningAsync(null, permanently);
		}

		async void DeleteInternal(bool fromDisk, List<DuplicateItemVM>? toDelete) {
			if (!fromDisk) {
				DeleteInternal(false, toDelete, false, false, false, false);
				return;
			}
			await DeleteFromDiskWithFullGroupWarningAsync(toDelete, permanently: false);
		}

		async Task DeleteFromDiskWithFullGroupWarningAsync(List<DuplicateItemVM>? toDelete, bool permanently) {
			// Snapshot now: checkbox/filter state can change while the warning dialog is open.
			var deletionSet = (toDelete ?? CheckedItemsToDelete).ToList();
			if (deletionSet.Count == 0)
				return;

			var fullGroups = FullGroupDeleteGuard.FindFullySelectedGroups(Duplicates, deletionSet);
			if (fullGroups.Count > 0) {
				string message = FullGroupDeleteGuard.BuildWarningMessage(fullGroups, App.Lang.CurrentLanguage);
				MessageBoxButtons? result = await MessageBoxService.Show(
					message,
					MessageBoxButtons.Yes | MessageBoxButtons.No,
					defaultButton: MessageBoxButtons.No);
				if (result != MessageBoxButtons.Yes)
					return;
			}

			DeleteInternal(
				fromDisk: true,
				toDelete: deletionSet,
				blackList: false,
				createSymbolLinksInstead: false,
				permanently: permanently,
				createHardLinksInstead: false);
		}
	}

	internal sealed record FullySelectedDeleteGroup(Guid GroupId, int ItemCount, string RepresentativePath);

	internal static class FullGroupDeleteGuard {
		internal const int MaxDisplayedGroups = 8;

		internal static IReadOnlyList<FullySelectedDeleteGroup> FindFullySelectedGroups(
			IEnumerable<DuplicateItemVM> allItems,
			IReadOnlyCollection<DuplicateItemVM> toDelete) {
			var deleteSet = new HashSet<DuplicateItemVM>(toDelete);
			var result = new List<FullySelectedDeleteGroup>();

			foreach (var group in allItems.GroupBy(item => item.ItemInfo.GroupId)) {
				var members = group.ToList();
				// A one-item entry is not a duplicate group and should not get the warning.
				if (members.Count < 2 || !members.All(deleteSet.Contains))
					continue;

				string representative = members
					.Select(item => item.ItemInfo.Path)
					.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? group.Key.ToString();
				result.Add(new FullySelectedDeleteGroup(group.Key, members.Count, representative));
			}

			return result;
		}

		internal static string BuildWarningMessage(
			IReadOnlyList<FullySelectedDeleteGroup> groups,
			string? languageCode) {
			bool chinese = languageCode?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
			var text = new StringBuilder();
			if (chinese) {
				text.AppendLine($"警告：有 {groups.Count} 个重复组的成员已全部选中。继续操作会从磁盘删除这些组中的全部文件：");
				text.AppendLine();
			}
			else {
				text.AppendLine($"Warning: every file is selected in {groups.Count} duplicate group(s). Continuing will delete every file in these groups from disk:");
				text.AppendLine();
			}

			int shown = Math.Min(groups.Count, MaxDisplayedGroups);
			for (int i = 0; i < shown; i++) {
				var group = groups[i];
				text.AppendLine(chinese
					? $"{i + 1}. {group.ItemCount} 个文件 · {group.RepresentativePath}"
					: $"{i + 1}. {group.ItemCount} files · {group.RepresentativePath}");
			}

			int remaining = groups.Count - shown;
			if (remaining > 0) {
				text.AppendLine(chinese
					? $"……另有 {remaining} 个整组全选"
					: $"... and {remaining} more fully selected group(s)");
			}

			text.AppendLine();
			text.Append(chinese ? "仍要继续从磁盘删除吗？" : "Continue deleting from disk?");
			return text.ToString();
		}
	}
}
