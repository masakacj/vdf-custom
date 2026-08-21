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

		/// <summary>
		/// Direct group-level entry used by each traditional similarity-group header. Unlike
		/// the checked-items action this always loads the complete current GroupId from the
		/// duplicate collection, so path/type filters cannot silently omit a sibling copy.
		/// </summary>
		public ReactiveCommand<ResultsGroupHeader, Unit> ConsolidateGroupHeaderCommand =>
			ReactiveCommand.CreateFromTask<ResultsGroupHeader>(ConsolidateGroupHeaderAsync);

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

			await ConsolidateGroupCandidatesAsync(
				selected,
				title: "合并所勾选副本",
				dialogSummary: $"同一相似组中已勾选 {selected.Count:N0} 个副本。默认选中系统推荐 BEST；你可以改选保留副本和目标文件夹。",
				busyText: "正在安全合并所勾选副本…");
		}

		async Task ConsolidateGroupHeaderAsync(ResultsGroupHeader? header) {
			if (header == null || IsBusy || IsScanning) return;
			var candidates = Duplicates
				.Where(item => item.ItemInfo.GroupId == header.GroupId)
				.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
				.ToList();
			if (candidates.Count < 2) {
				await MessageBoxService.Show("这个相似组当前不足 2 个副本，无法合并。", title: "合并本组副本");
				return;
			}

			await ConsolidateGroupCandidatesAsync(
				candidates,
				title: "合并本组副本",
				dialogSummary: $"相似组 {header.GroupNumber:N0} 共 {candidates.Count:N0} 个副本。默认保留系统推荐 BEST；目标文件夹可直接从本组已有路径中选择，也可手工修改。",
				busyText: "正在安全合并本组副本…");
		}

		async Task ConsolidateGroupCandidatesAsync(
			IReadOnlyList<DuplicateItemVM> candidates,
			string title,
			string dialogSummary,
			string busyText) {
			if (candidates.Count < 2) return;
			var groupIds = candidates.Select(item => item.ItemInfo.GroupId).Distinct().ToList();
			if (groupIds.Count != 1) {
				await MessageBoxService.Show("本次候选不属于同一个相似组，已停止。", title: title);
				return;
			}

			var onDisk = candidates.Where(item => File.Exists(item.ItemInfo.Path)).ToList();
			if (onDisk.Count == 0) {
				await MessageBoxService.Show("当前候选文件都不在磁盘上，无法执行合并。", title: title);
				return;
			}
			BestRecommendation recommendation = RecommendBestUsingCurrentRules(onDisk);
			var dialog = new CheckedGroupConsolidationDialog(candidates, recommendation, title, dialogSummary);
			CheckedGroupConsolidationDialogResult? choice =
				await dialog.ShowDialog<CheckedGroupConsolidationDialogResult?>(ApplicationHelpers.MainWindow);
			if (choice == null) return;

			DuplicateItemVM keeper = choice.Keeper;
			if (!File.Exists(keeper.ItemInfo.Path)) {
				await MessageBoxService.Show("你选择保留的副本当前不在磁盘上，请改选可用文件。", title: title);
				return;
			}

			var originalPaths = candidates.ToDictionary(
				item => item, item => item.ItemInfo.Path,
				ReferenceEqualityComparer<DuplicateItemVM>.Instance);
			string destination = Path.GetFullPath(choice.DestinationPath);
			string keeperOriginal = originalPaths[keeper];
			bool keeperAlreadyThere = ConsolidationPathsEqual(keeperOriginal, destination);
			DuplicateItemVM? destinationMember = candidates.FirstOrDefault(item =>
				!ReferenceEquals(item, keeper) && ConsolidationPathsEqual(originalPaths[item], destination));

			if (Directory.Exists(destination)) {
				await MessageBoxService.Show("最终文件路径当前是一个目录，无法执行。", title: title);
				return;
			}
			if (File.Exists(destination) && destinationMember == null && !keeperAlreadyThere) {
				await MessageBoxService.Show(
					"最终位置已存在一个不属于本次相似组的文件。为避免覆盖无关资源，本次合并已停止。",
					title: title);
				return;
			}
			if (!ScanEngine.ValidateConsolidationDatabaseChange(
				keeperOriginal, destination, originalPaths.Values, out string dbValidationError)) {
				await MessageBoxService.Show(
					$"无法安全更新 VDF 数据库，因此没有开始移动文件。\n\n{dbValidationError}",
					title: title);
				return;
			}

			long reclaim = candidates.Where(item => !ReferenceEquals(item, keeper) && File.Exists(originalPaths[item]))
				.Sum(item => Math.Max(0, item.ItemInfo.SizeLong));
			string confidence = recommendation.IsConfirmed ? "确认 BEST" : "推荐 BEST（原组仍需人工判断）";
			var confirm = await MessageBoxService.Show(
				$"保留副本：\n{keeperOriginal}\n\n" +
				$"最终位置：\n{destination}\n\n" +
				$"系统判断：{confidence}\n{recommendation.Reason}\n\n" +
				$"本次处理其他副本：{candidates.Count - 1:N0} 个，全部成功清理后预计释放 {reclaim.BytesToString()}。\n\n" +
				"会先安全落盘并校验保留副本，再处理其他副本。确认开始？",
				MessageBoxButtons.Yes | MessageBoxButtons.No,
				title, MessageBoxButtons.No);
			if (confirm != MessageBoxButtons.Yes) return;

			IsBusy = true;
			IsBusyOverlayText = busyText;
			SingleResourceExecutionResult execution;
			try {
				execution = await Task.Run(() => ExecuteSingleResourceConsolidation(
					candidates, keeper, originalPaths, destination, destinationMember));
			}
			finally { IsBusy = false; }

			if (!execution.TransferSucceeded) {
				await MessageBoxService.Show(
					$"保留副本没有完成安全落盘，其他副本未删除。\n\n{execution.Error}",
					title: title);
				return;
			}

			keeper.ItemInfo.Path = destination;
			var removed = new HashSet<string>(
				execution.RemovedPaths.Select(NormalizeConsolidationSetPath), ConsolidationPathComparer());
			foreach (DuplicateItemVM item in candidates) {
				if (ReferenceEquals(item, keeper)) continue;
				if (removed.Contains(NormalizeConsolidationSetPath(originalPaths[item])))
					Duplicates.Remove(item);
			}

			keeper.Checked = false;
			foreach (DuplicateItemVM item in candidates) {
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
				title: title);
		}
	}
}
