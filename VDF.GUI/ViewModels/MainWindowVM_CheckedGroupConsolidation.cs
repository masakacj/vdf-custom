// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Linq;
using System.Reactive;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Utils;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
		/// <summary>
		/// Consolidates only the currently checked files when they all belong to one duplicate
		/// group. The dialog defaults to the system recommendation but the user explicitly
		/// chooses the keeper and destination folder before any file changes occur.
		/// </summary>
		public ReactiveCommand<Unit, Unit> ConsolidateCheckedGroupCommand =>
			ReactiveCommand.CreateFromTask(ConsolidateCheckedGroupAsync);

		async Task ConsolidateCheckedGroupAsync() {
			if (IsBusy || IsScanning) return;
			var selected = Duplicates.Where(item => item.Checked).ToList();
			if (selected.Count < 2) {
				await MessageBoxService.Show("请在同一个相似组中至少勾选 2 个文件。", title: "合并所勾选副本");
				return;
			}
			var groupIds = selected.Select(item => item.ItemInfo.GroupId).Distinct().ToList();
			if (groupIds.Count != 1) {
				await MessageBoxService.Show(
					"当前勾选跨越了多个相似组。这个操作只处理同一组副本，请先只保留一个组的勾选。",
					title: "合并所勾选副本");
				return;
			}

			var onDisk = selected.Where(item => File.Exists(item.ItemInfo.Path)).ToList();
			if (onDisk.Count == 0) {
				await MessageBoxService.Show("当前勾选文件都不在磁盘上，无法执行合并。", title: "合并所勾选副本");
				return;
			}
			BestRecommendation recommendation = RecommendBest(onDisk);
			var dialog = new CheckedGroupConsolidationDialog(selected, recommendation);
			CheckedGroupConsolidationDialogResult? choice =
				await dialog.ShowDialog<CheckedGroupConsolidationDialogResult?>(ApplicationHelpers.MainWindow);
			if (choice == null) return;

			DuplicateItemVM keeper = choice.Keeper;
			if (!File.Exists(keeper.ItemInfo.Path)) {
				await MessageBoxService.Show("你选择保留的副本当前不在磁盘上，请改选可用文件。", title: "合并所勾选副本");
				return;
			}

			var originalPaths = selected.ToDictionary(
				item => item, item => item.ItemInfo.Path,
				ReferenceEqualityComparer<DuplicateItemVM>.Instance);
			string destination = Path.GetFullPath(choice.DestinationPath);
			string keeperOriginal = originalPaths[keeper];
			bool keeperAlreadyThere = ConsolidationPathsEqual(keeperOriginal, destination);
			DuplicateItemVM? destinationMember = selected.FirstOrDefault(item =>
				!ReferenceEquals(item, keeper) && ConsolidationPathsEqual(originalPaths[item], destination));

			if (Directory.Exists(destination)) {
				await MessageBoxService.Show("最终文件路径当前是一个目录，无法执行。", title: "合并所勾选副本");
				return;
			}
			if (File.Exists(destination) && destinationMember == null && !keeperAlreadyThere) {
				await MessageBoxService.Show(
					"最终位置已存在一个不属于本次勾选副本的文件。为避免覆盖无关资源，本次合并已停止。",
					title: "合并所勾选副本");
				return;
			}
			if (!ScanEngine.ValidateConsolidationDatabaseChange(
				keeperOriginal, destination, originalPaths.Values, out string dbValidationError)) {
				await MessageBoxService.Show(
					$"无法安全更新 VDF 数据库，因此没有开始移动文件。\n\n{dbValidationError}",
					title: "合并所勾选副本");
				return;
			}

			long reclaim = selected.Where(item => !ReferenceEquals(item, keeper))
				.Sum(item => Math.Max(0, item.ItemInfo.SizeLong));
			string confidence = recommendation.IsConfirmed ? "确认 BEST" : "推荐 BEST（原组仍需人工判断）";
			var confirm = await MessageBoxService.Show(
				$"保留副本：\n{keeperOriginal}\n\n" +
				$"最终位置：\n{destination}\n\n" +
				$"系统判断：{confidence}\n{recommendation.Reason}\n\n" +
				$"本次处理其他副本：{selected.Count - 1:N0} 个，全部成功清理后预计释放 {reclaim.BytesToString()}。\n\n" +
				"会先安全落盘并校验保留副本，再处理其他副本。确认开始？",
				MessageBoxButtons.Yes | MessageBoxButtons.No,
				"合并所勾选副本", MessageBoxButtons.No);
			if (confirm != MessageBoxButtons.Yes) return;

			IsBusy = true;
			IsBusyOverlayText = "正在安全合并所勾选副本…";
			SingleResourceExecutionResult execution;
			try {
				execution = await Task.Run(() => ExecuteSingleResourceConsolidation(
					selected, keeper, originalPaths, destination, destinationMember));
			}
			finally { IsBusy = false; }

			if (!execution.TransferSucceeded) {
				await MessageBoxService.Show(
					$"保留副本没有完成安全落盘，其他副本未删除。\n\n{execution.Error}",
					title: "合并所勾选副本");
				return;
			}

			keeper.ItemInfo.Path = destination;
			var removed = new HashSet<string>(
				execution.RemovedPaths.Select(NormalizeConsolidationSetPath), ConsolidationPathComparer());
			foreach (DuplicateItemVM item in selected) {
				if (ReferenceEquals(item, keeper)) continue;
				if (removed.Contains(NormalizeConsolidationSetPath(originalPaths[item])))
					Duplicates.Remove(item);
			}

			keeper.Checked = false;
			foreach (DuplicateItemVM item in selected) {
				if (ReferenceEquals(item, keeper) || !Duplicates.Contains(item)) continue;
				item.Checked = true;
			}
			bool anyOtherInGroup = Duplicates.Any(item =>
				!ReferenceEquals(item, keeper) && item.ItemInfo.GroupId == groupIds[0]);
			if (!anyOtherInGroup)
				Duplicates.Remove(keeper);

			RefreshResultsView();
			RefreshGroupStats();

			string dbWarning = execution.DatabaseCommitted ? string.Empty
				: $"\n\n⚠ 文件已经合并，但 VDF 数据库更新失败：{execution.Error}\n请重新扫描该目录刷新数据库。";
			string transferWarning = string.IsNullOrWhiteSpace(execution.TransferWarning) ? string.Empty
				: $"\n\n提示：{execution.TransferWarning}";
			string failed = execution.DeleteFailures.Count == 0 ? string.Empty
				: $"\n\n仍有 {execution.DeleteFailures.Count:N0} 个副本未能清理，已继续勾选供手动处理：\n" +
					string.Join("\n", execution.DeleteFailures.Take(8));
			await MessageBoxService.Show(
				$"合并完成。\n\n最终保留：\n{destination}\n\n已清理副本：{execution.RemovedPaths.Count:N0} 个" +
				failed + dbWarning + transferWarning,
				title: "合并所勾选副本");
		}
	}
}
