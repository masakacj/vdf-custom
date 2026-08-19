// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Linq;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Utils;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	internal sealed class ResourceSeriesGroupPlan {
		internal required Guid GroupId { get; init; }
		internal required DuplicateItemVM Keeper { get; init; }
		internal required IReadOnlyList<DuplicateItemVM> Losers { get; init; }
		internal required string SourceRoot { get; init; }
		internal required string DestinationPath { get; init; }
		internal required bool KeeperNeedsMove { get; init; }
	}

	internal sealed class ResourceSeriesFileMovePlan {
		internal required string SourcePath { get; init; }
		internal required string DestinationPath { get; init; }
	}

	internal sealed class ResourceSeriesConsolidationPlan {
		internal required ResourceRelationHeader Header { get; init; }
		internal required string DestinationRoot { get; init; }
		internal List<ResourceSeriesGroupPlan> Groups { get; } = new();
		internal List<ResourceSeriesFileMovePlan> UniqueFiles { get; } = new();
		internal HashSet<Guid> ManualReviewGroupIds { get; } = new();
		internal int PathConflictCount { get; set; }
		internal int UniqueFilesSkippedByCoverage { get; set; }
		internal int KeeperMoves => Groups.Count(group => group.KeeperNeedsMove);
	}

	internal sealed class ResourceSeriesConsolidationResult {
		internal int GroupsPrepared { get; set; }
		internal int GroupMoveFailures { get; set; }
		internal int KeeperMovesSucceeded { get; set; }
		internal int UniqueMovesSucceeded { get; set; }
		internal int UniqueMoveFailures { get; set; }
		internal int SafeLosersMarked { get; set; }
	}

	public partial class MainWindowVM : ReactiveObject {
		internal async Task ConsolidateSelectedResourceSeriesAsync(IReadOnlyList<ResourceRelationHeader> headers) {
			if (headers == null || headers.Count == 0 || IsScanning || IsBusy)
				return;

			string initial = SuggestedSeriesConsolidationDestination(headers);
			var dialog = new ResourceConsolidationDialog(
				headers.Count == 1
					? $"已选择系列：{headers[0].TargetFolder}"
					: $"已选择 {headers.Count:N0} 个系列。",
				initial,
				headers.Count > 1);
			string? selectedPath = await dialog.ShowDialog<string?>(ApplicationHelpers.MainWindow);
			if (string.IsNullOrWhiteSpace(selectedPath))
				return;

			var destinations = ResolveSeriesDestinations(headers, selectedPath);
			if (destinations == null) {
				await MessageBoxService.Show(
					"多个已选系列会映射到相同的目标系列目录。为避免自动改名，请分批整合这些同名系列。",
					title: "资源整合");
				return;
			}

			var plans = headers.Select(header =>
				BuildResourceSeriesConsolidationPlan(header, destinations[header.SelectionKey])).ToList();
			int groups = plans.Sum(plan => plan.Groups.Count);
			int keeperMoves = plans.Sum(plan => plan.KeeperMoves);
			int uniqueMoves = plans.Sum(plan => plan.UniqueFiles.Count);
			int manual = plans.Sum(plan => plan.ManualReviewGroupIds.Count);
			int conflicts = plans.Sum(plan => plan.PathConflictCount);
			int skippedUnique = plans.Sum(plan => plan.UniqueFilesSkippedByCoverage);

			string preview =
				$"系列：{plans.Count:N0}\n" +
				$"明确 BEST 资源组：{groups:N0}\n" +
				$"其中 BEST 需要移动：{keeperMoves:N0}\n" +
				$"可安全带入的独有资源：{uniqueMoves:N0}\n" +
				$"质量/关系待人工：{manual:N0}\n" +
				$"目标路径冲突：{conflicts:N0}\n" +
				$"因来源覆盖不足而不自动带入的独有资源：{skippedUnique:N0}\n\n" +
				"所有移动都保留各自系列根目录以下的相对子目录；人工项和冲突项保持原样。\n\n确认执行整合？";
			var confirm = await MessageBoxService.Show(
				preview,
				MessageBoxButtons.Yes | MessageBoxButtons.No,
				"资源整合预览",
				MessageBoxButtons.No);
			if (confirm != MessageBoxButtons.Yes)
				return;

			IsBusyOverlayText = "正在按系列目录结构整合资源…";
			IsBusy = true;
			try {
				var results = new List<ResourceSeriesConsolidationResult>();
				foreach (var plan in plans)
					results.Add(await ExecuteResourceSeriesConsolidationAsync(plan));

				await MessageBoxService.Show(
					$"资源整合完成。\n\n" +
					$"成功处理明确 BEST 组：{results.Sum(r => r.GroupsPrepared):N0}\n" +
					$"BEST 移动成功：{results.Sum(r => r.KeeperMovesSucceeded):N0}\n" +
					$"独有资源移动成功：{results.Sum(r => r.UniqueMovesSucceeded):N0}\n" +
					$"已安全标记重复副本：{results.Sum(r => r.SafeLosersMarked):N0}\n" +
					$"移动失败：{results.Sum(r => r.GroupMoveFailures + r.UniqueMoveFailures):N0}\n" +
					$"预演阶段人工/冲突：{manual + conflicts:N0}\n\n" +
					"失败、人工复核和冲突文件均未覆盖或自动改名。",
					title: "资源整合");
			}
			finally {
				IsBusy = false;
			}
		}

		internal ResourceSeriesConsolidationPlan BuildResourceSeriesConsolidationPlan(
			ResourceRelationHeader header,
			string destinationRoot) {
			var plan = new ResourceSeriesConsolidationPlan {
				Header = header,
				DestinationRoot = Path.GetFullPath(destinationRoot),
			};
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			var roots = new[] { header.TargetFolder }.Concat(header.SourceFolders)
				.Select(NormalizePikPakPath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			var matchedPaths = new HashSet<string>(comparer);
			var plannedDestinations = new HashSet<string>(comparer);
			var plannedSources = new HashSet<string>(comparer);

			var matchesByGroup = header.SourceRelations
				.SelectMany(relation => relation.Option.Matches)
				.Where(match => header.DisplayedGroupIds.Contains(match.GroupId))
				.GroupBy(match => match.GroupId)
				.ToList();

			foreach (var matchGroup in matchesByGroup) {
				var matches = matchGroup.ToList();
				var candidates = matches
					.SelectMany(match => match.FolderAItems.Concat(match.FolderBItems))
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
					.ToList();
				foreach (var candidate in candidates)
					matchedPaths.Add(Path.GetFullPath(candidate.ItemInfo.Path));

				if (matches.Any(match => match.ReviewOnly) || !TryPickDecisiveQualityWinner(candidates, out DuplicateItemVM keeper)) {
					plan.ManualReviewGroupIds.Add(matchGroup.Key);
					continue;
				}

				string? sourceRoot = FindOwningSeriesRoot(keeper.ItemInfo.Path, roots);
				if (sourceRoot == null || !TryBuildPreservedDestination(sourceRoot, keeper.ItemInfo.Path, plan.DestinationRoot, out string destination)) {
					plan.ManualReviewGroupIds.Add(matchGroup.Key);
					continue;
				}
				string source = Path.GetFullPath(keeper.ItemInfo.Path);
				bool samePath = source.Equals(destination, comparison);
				if ((!samePath && (File.Exists(destination) || Directory.Exists(destination))) || !plannedDestinations.Add(destination)) {
					plan.PathConflictCount++;
					plan.ManualReviewGroupIds.Add(matchGroup.Key);
					continue;
				}

				plan.Groups.Add(new ResourceSeriesGroupPlan {
					GroupId = matchGroup.Key,
					Keeper = keeper,
					Losers = candidates.Where(item => !ReferenceEquals(item, keeper)).ToList(),
					SourceRoot = sourceRoot,
					DestinationPath = destination,
					KeeperNeedsMove = !samePath,
				});
				plannedSources.Add(source);
			}

			foreach (string root in roots) {
				bool mayBringUnique = root.Equals(NormalizePikPakPath(header.TargetFolder), StringComparison.OrdinalIgnoreCase) ||
					header.SourceRelations.Any(relation =>
						relation.SourceFolder.Equals(root, StringComparison.OrdinalIgnoreCase) && relation.WholeSourceEligible);
				var files = Scanner.GetRecursiveFolderMediaFiles(root);
				if (!mayBringUnique) {
					plan.UniqueFilesSkippedByCoverage += files.Count(file => !matchedPaths.Contains(Path.GetFullPath(file.Path)));
					continue;
				}

				foreach (FolderMediaFile file in files) {
					string source = Path.GetFullPath(file.Path);
					if (matchedPaths.Contains(source) || !plannedSources.Add(source))
						continue;
					if (!TryBuildPreservedDestination(root, source, plan.DestinationRoot, out string destination)) {
						plan.PathConflictCount++;
						continue;
					}
					if (source.Equals(destination, comparison)) {
						plannedDestinations.Add(destination);
						continue;
					}
					if (File.Exists(destination) || Directory.Exists(destination) || !plannedDestinations.Add(destination)) {
						plan.PathConflictCount++;
						continue;
					}
					plan.UniqueFiles.Add(new ResourceSeriesFileMovePlan {
						SourcePath = source,
						DestinationPath = destination,
					});
				}
			}
			return plan;
		}

		internal async Task<ResourceSeriesConsolidationResult> ExecuteResourceSeriesConsolidationAsync(
			ResourceSeriesConsolidationPlan plan) {
			var result = new ResourceSeriesConsolidationResult();
			var successfulLosers = new List<DuplicateItemVM>();
			var keeperPathUpdates = new List<(DuplicateItemVM Item, string Path)>();

			await Task.Run(() => {
				foreach (ResourceSeriesGroupPlan group in plan.Groups) {
					bool safe = true;
					if (group.KeeperNeedsMove) {
						string oldPath = group.Keeper.ItemInfo.Path;
						ScanEngine.GetFromDatabase(oldPath, out FileEntry? dbEntry);
						SafeMoveResult move = SafeFileTransfer.MoveVerifiedExact(oldPath, group.DestinationPath);
						if (!move.Success) {
							Logger.Instance.Error($"Series consolidation could not move BEST '{oldPath}' -> '{group.DestinationPath}': {move.Error}");
							result.GroupMoveFailures++;
							safe = false;
						}
						else {
							result.KeeperMovesSucceeded++;
							if (dbEntry != null)
								ScanEngine.UpdateFilePathInDatabase(move.NewPath, dbEntry);
							keeperPathUpdates.Add((group.Keeper, move.NewPath));
						}
					}
					if (safe) {
						result.GroupsPrepared++;
						successfulLosers.AddRange(group.Losers);
					}
				}

				foreach (ResourceSeriesFileMovePlan file in plan.UniqueFiles) {
					ScanEngine.GetFromDatabase(file.SourcePath, out FileEntry? dbEntry);
					SafeMoveResult move = SafeFileTransfer.MoveVerifiedExact(file.SourcePath, file.DestinationPath);
					if (!move.Success) {
						Logger.Instance.Error($"Series consolidation could not move unique media '{file.SourcePath}' -> '{file.DestinationPath}': {move.Error}");
						result.UniqueMoveFailures++;
						continue;
					}
					result.UniqueMovesSucceeded++;
					if (dbEntry != null)
						ScanEngine.UpdateFilePathInDatabase(move.NewPath, dbEntry);
				}

				if (result.KeeperMovesSucceeded > 0 || result.UniqueMovesSucceeded > 0)
					ScanEngine.SaveDatabase();
			});

			foreach (var update in keeperPathUpdates)
				update.Item.ItemInfo.Path = update.Path;

			using (var undoBatch = BeginSelectionUndoBatch()) {
				foreach (ResourceSeriesGroupPlan group in plan.Groups)
					group.Keeper.Checked = false;
				foreach (DuplicateItemVM loser in successfulLosers.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance))
					loser.Checked = true;
			}
			result.SafeLosersMarked = successfulLosers.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance).Count();
			RefreshResultsView();
			RefreshGroupStats();
			return result;
		}

		internal static bool TryBuildPreservedDestination(
			string sourceRoot,
			string sourcePath,
			string destinationRoot,
			out string destinationPath) {
			destinationPath = string.Empty;
			try {
				string sourceRootFull = Path.GetFullPath(sourceRoot);
				string sourceFull = Path.GetFullPath(sourcePath);
				string destinationRootFull = Path.GetFullPath(destinationRoot);
				string relative = Path.GetRelativePath(sourceRootFull, sourceFull);
				if (relative.Length == 0 || relative == "." || Path.IsPathRooted(relative))
					return false;
				if (relative.Equals("..", StringComparison.Ordinal) ||
					relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
					relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
					return false;
				destinationPath = Path.GetFullPath(Path.Combine(destinationRootFull, relative));
				string destinationRelative = Path.GetRelativePath(destinationRootFull, destinationPath);
				return !Path.IsPathRooted(destinationRelative) &&
					!destinationRelative.Equals("..", StringComparison.Ordinal) &&
					!destinationRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
					!destinationRelative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
			}
			catch {
				return false;
			}
		}

		static string? FindOwningSeriesRoot(string path, IReadOnlyList<string> roots) => roots
			.Where(root => PikPakPathIsWithin(path, root))
			.OrderByDescending(root => NormalizePikPakPath(root).Length)
			.FirstOrDefault();

		static string SuggestedSeriesConsolidationDestination(IReadOnlyList<ResourceRelationHeader> headers) {
			try {
				if (headers.Count == 1)
					return Path.GetFullPath(headers[0].TargetFolder);
				return Path.GetDirectoryName(Path.GetFullPath(headers[0].TargetFolder)) ?? string.Empty;
			}
			catch {
				return string.Empty;
			}
		}

		static Dictionary<string, string>? ResolveSeriesDestinations(
			IReadOnlyList<ResourceRelationHeader> headers,
			string selectedPath) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var used = new HashSet<string>(comparer);
			string basePath = Path.GetFullPath(selectedPath);
			foreach (ResourceRelationHeader header in headers) {
				string destination;
				if (headers.Count == 1) {
					destination = basePath;
				}
				else {
					string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(header.TargetFolder));
					string name = Path.GetFileName(target);
					if (string.IsNullOrWhiteSpace(name)) return null;
					destination = Path.Combine(basePath, name);
				}
				if (!used.Add(destination))
					return null;
				result[header.SelectionKey] = destination;
			}
			return result;
		}
	}
}
