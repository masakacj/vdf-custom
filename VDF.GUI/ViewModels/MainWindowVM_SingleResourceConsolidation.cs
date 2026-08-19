// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Linq;
using System.Reactive;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Utils;
using VDF.GUI.Views;
using CoreFileUtils = VDF.Core.Utils.FileUtils;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
		sealed record SingleResourceExecutionResult(
			bool TransferSucceeded,
			bool DatabaseCommitted,
			string? Error,
			string? TransferWarning,
			IReadOnlyList<string> RemovedPaths,
			IReadOnlyList<string> DeleteFailures);

		public ReactiveCommand<ResultsGroupHeader, Unit> ConsolidateGroupToSeriesCommand =>
			ReactiveCommand.CreateFromTask<ResultsGroupHeader>(ConsolidateGroupToSeriesAsync);

		async Task ConsolidateGroupToSeriesAsync(ResultsGroupHeader header) {
			if (header == null || IsBusy || IsScanning) return;

			var members = header.Rows.Select(row => row.Item)
				.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance).ToList();
			if (members.Count < 2) return;

			if (!TryPickDecisiveQualityWinner(members, out DuplicateItemVM best)) {
				await MessageBoxService.Show(
					"这个相似资源组没有唯一、无冲突的明确 BEST。\n\n质量打平、关键元数据不足或质量指标互有胜负时不会自动整合，请先手动判断。",
					title: "整合到系列");
				return;
			}
			if (!File.Exists(best.ItemInfo.Path)) {
				await MessageBoxService.Show("明确 BEST 文件当前不在磁盘上，无法执行整合。", title: "整合到系列");
				return;
			}

			var originalPaths = members.ToDictionary(
				item => item, item => item.ItemInfo.Path,
				ReferenceEqualityComparer<DuplicateItemVM>.Instance);
			var anchors = members.Where(item => !ReferenceEquals(item, best))
				.Select(item => item.ItemInfo.Path)
				.OrderByDescending(File.Exists)
				.ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToList();

			var suggestion = FindSingleResourceSeriesSuggestion(header.GroupId, members, best);
			var dialog = new SingleResourceConsolidationDialog(
				best.ItemInfo.Path, anchors, suggestion.AnchorPath, suggestion.DestinationPath);
			SingleResourceConsolidationDialogResult? dialogResult =
				await dialog.ShowDialog<SingleResourceConsolidationDialogResult?>(ApplicationHelpers.MainWindow);
			if (dialogResult == null) return;

			string destination = Path.GetFullPath(dialogResult.DestinationPath);
			string bestOriginalPath = originalPaths[best];
			DuplicateItemVM? destinationMember = members.FirstOrDefault(item =>
				!ReferenceEquals(item, best) && ConsolidationPathsEqual(originalPaths[item], destination));
			bool bestAlreadyAtDestination = ConsolidationPathsEqual(bestOriginalPath, destination);

			if (Directory.Exists(destination)) {
				await MessageBoxService.Show("最终文件路径当前是一个目录，不能执行整合。", title: "整合到系列");
				return;
			}
			if (File.Exists(destination) && destinationMember == null && !bestAlreadyAtDestination) {
				await MessageBoxService.Show(
					"最终路径已经存在一个不属于当前相似组的文件。为了避免覆盖无关资源，本次整合已停止。",
					title: "整合到系列");
				return;
			}
			if (!ScanEngine.ValidateConsolidationDatabaseChange(
				bestOriginalPath, destination, originalPaths.Values, out string dbValidationError)) {
				await MessageBoxService.Show(
					$"无法安全更新 VDF 数据库，因此没有开始移动文件。\n\n{dbValidationError}",
					title: "整合到系列");
				return;
			}

			string anchorLine = string.IsNullOrWhiteSpace(dialogResult.AnchorPath)
				? "自定义目标位置" : dialogResult.AnchorPath;
			string replacementLine = destinationMember != null && !bestAlreadyAtDestination
				? "\n目标处的低质量副本会在 BEST 校验完成后被替换。" : string.Empty;
			var confirm = await MessageBoxService.Show(
				$"明确 BEST：\n{bestOriginalPath}\n\n位置锚点：\n{anchorLine}\n\n最终保留：\n{destination}\n\n" +
				$"计划移除其他副本：{members.Count - 1:N0} 个。{replacementLine}\n\n" +
				"会先完整校验最终 BEST，再移除其他副本。确认开始整合？",
				MessageBoxButtons.Yes | MessageBoxButtons.No,
				"整合到系列", MessageBoxButtons.No);
			if (confirm != MessageBoxButtons.Yes) return;

			IsBusy = true;
			IsBusyOverlayText = "正在安全整合资源…";
			SingleResourceExecutionResult execution;
			try {
				execution = await Task.Run(() => ExecuteSingleResourceConsolidation(
					members, best, originalPaths, destination, destinationMember));
			}
			finally { IsBusy = false; }

			if (!execution.TransferSucceeded) {
				await MessageBoxService.Show(
					$"BEST 没有完成安全落盘，其他副本未删除。\n\n{execution.Error}",
					title: "整合到系列");
				return;
			}

			best.ItemInfo.Path = destination;
			var removed = new HashSet<string>(
				execution.RemovedPaths.Select(NormalizeConsolidationSetPath), ConsolidationPathComparer());
			foreach (DuplicateItemVM item in members) {
				if (ReferenceEquals(item, best)) continue;
				if (removed.Contains(NormalizeConsolidationSetPath(originalPaths[item])))
					Duplicates.Remove(item);
			}
			int remainingLosers = members.Count(item =>
				!ReferenceEquals(item, best) && Duplicates.Contains(item));
			if (remainingLosers == 0)
				Duplicates.Remove(best);
			else
				best.Checked = false;

			RefreshResultsView();
			RefreshGroupStats();

			string dbWarning = execution.DatabaseCommitted ? string.Empty
				: $"\n\n⚠ 文件整合已经完成，但 VDF 数据库更新失败：{execution.Error}\n请重新扫描该目录以刷新数据库。";
			string cleanupWarning = string.IsNullOrWhiteSpace(execution.TransferWarning) ? string.Empty
				: $"\n\n提示：{execution.TransferWarning}";
			string failedDeletes = execution.DeleteFailures.Count == 0 ? string.Empty
				: $"\n\n仍有 {execution.DeleteFailures.Count:N0} 个副本未能移除，已保留在结果中供手动处理：\n" +
					string.Join("\n", execution.DeleteFailures.Take(8));
			await MessageBoxService.Show(
				$"整合完成。\n\n最终 BEST：\n{destination}\n\n已移除副本：{execution.RemovedPaths.Count:N0} 个" +
				failedDeletes + dbWarning + cleanupWarning,
				title: "整合到系列");
		}

		(string? AnchorPath, string? DestinationPath) FindSingleResourceSeriesSuggestion(
			Guid groupId, IReadOnlyList<DuplicateItemVM> members, DuplicateItemVM best) {
			try {
				var options = BuildResourceCoverageOptions(resultsGroups);
				double minimum = Math.Max(50d, ResourceFolderMatchPreference.MinimumPercent);
				var suggestions = new List<(string? Anchor, string Destination)>();
				foreach (PikPakFolderCoverageOption option in options) {
					if (option.FolderMatchPercent + 0.0001d < minimum ||
						!option.Matches.Any(match => match.GroupId == groupId)) continue;
					bool targetIsA = ResourceRelationHeader.ChooseTargetIsA(option);
					string normalizedRoot = NormalizePikPakPath(targetIsA ? option.FolderA : option.FolderB);
					if (PikPakPathIsInScope(best.ItemInfo.Path, new[] { normalizedRoot })) {
						suggestions.Add((null, best.ItemInfo.Path));
						continue;
					}
					var inside = members.Where(item => !ReferenceEquals(item, best) &&
						PikPakPathIsInScope(item.ItemInfo.Path, new[] { normalizedRoot })).ToList();
					if (inside.Count == 1) {
						string anchor = inside[0].ItemInfo.Path;
						suggestions.Add((anchor,
							SingleResourceConsolidationDialog.BuildAnchoredDestination(anchor, best.ItemInfo.Path)));
					}
				}
				if (suggestions.Count == 0) return (null, null);
				string first = NormalizeConsolidationSetPath(suggestions[0].Destination);
				StringComparer comparer = ConsolidationPathComparer();
				if (suggestions.Any(s => !comparer.Equals(NormalizeConsolidationSetPath(s.Destination), first)))
					return (null, null);
				return suggestions[0];
			}
			catch { return (null, null); }
		}

		SingleResourceExecutionResult ExecuteSingleResourceConsolidation(
			IReadOnlyList<DuplicateItemVM> members,
			DuplicateItemVM best,
			IReadOnlyDictionary<DuplicateItemVM, string> originalPaths,
			string destination,
			DuplicateItemVM? destinationMember) {
			string bestOriginal = originalPaths[best];
			bool alreadyThere = ConsolidationPathsEqual(bestOriginal, destination);
			SafeMoveResult transfer = alreadyThere
				? new SafeMoveResult(true, destination, null)
				: File.Exists(destination)
					? SafeFileTransfer.ReplaceVerifiedExact(bestOriginal, destination)
					: SafeFileTransfer.MoveVerifiedExact(bestOriginal, destination);
			if (!transfer.Success)
				return new SingleResourceExecutionResult(false, false, transfer.Error, null,
					Array.Empty<string>(), Array.Empty<string>());

			var removedPaths = new List<string>();
			var failures = new List<string>();
			if (destinationMember != null && !ReferenceEquals(destinationMember, best))
				removedPaths.Add(originalPaths[destinationMember]);
			foreach (DuplicateItemVM loser in members) {
				if (ReferenceEquals(loser, best) || ReferenceEquals(loser, destinationMember)) continue;
				string path = originalPaths[loser];
				if (ConsolidationPathsEqual(path, destination)) {
					removedPaths.Add(path);
					continue;
				}
				if (TryRemoveConsolidatedDuplicate(path, out string? error))
					removedPaths.Add(path);
				else
					failures.Add($"{path}（{error}）");
			}

			bool committed = ScanEngine.CommitConsolidationDatabaseChange(
				bestOriginal, destination, removedPaths, out string dbError);
			return new SingleResourceExecutionResult(
				true, committed, committed ? null : dbError, transfer.Error, removedPaths, failures);
		}

		static bool TryRemoveConsolidatedDuplicate(string path, out string? error) {
			if (!File.Exists(path)) { error = null; return true; }
			try {
				if (OperatingSystem.IsWindows()) {
					var op = new CoreFileUtils.SHFILEOPSTRUCT {
						wFunc = CoreFileUtils.FileOperationType.FO_DELETE,
						pFrom = path + '\0' + '\0',
						pTo = string.Empty,
						fFlags = CoreFileUtils.FileOperationFlags.FOF_ALLOWUNDO |
							CoreFileUtils.FileOperationFlags.FOF_NOCONFIRMATION |
							CoreFileUtils.FileOperationFlags.FOF_NOERRORUI |
							CoreFileUtils.FileOperationFlags.FOF_SILENT,
						lpszProgressTitle = string.Empty,
					};
					int result = CoreFileUtils.SHFileOperation(ref op);
					if (result != 0 || op.fAnyOperationsAborted || File.Exists(path)) {
						error = result != 0 ? $"回收站操作失败：0x{result:X}" : "文件仍然存在";
						return false;
					}
					error = null;
					return true;
				}
				if (!CoreFileUtils.MoveToTrash(path)) File.Delete(path);
				if (File.Exists(path)) { error = "删除后文件仍然存在"; return false; }
				error = null;
				return true;
			}
			catch (Exception ex) { error = ex.Message; return false; }
		}

		static bool ConsolidationPathsEqual(string a, string b) =>
			ConsolidationPathComparer().Equals(
				NormalizeConsolidationSetPath(a), NormalizeConsolidationSetPath(b));

		static string NormalizeConsolidationSetPath(string path) {
			try { return Path.GetFullPath(path); }
			catch { return path; }
		}

		static StringComparer ConsolidationPathComparer() =>
			OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	}
}
