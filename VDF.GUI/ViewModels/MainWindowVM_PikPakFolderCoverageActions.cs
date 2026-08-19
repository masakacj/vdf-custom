// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Utils;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
		internal int RunPikPakFolderMergeSelection(
			PikPakFolderCoverageOption option,
			bool swapSuggestedDirection,
			PikPakFolderMergeKeepRule keepRule) {
			if (option == null || keepRule == PikPakFolderMergeKeepRule.Manual)
				return 0;

			DuplicateItemVM? BestQuality(IReadOnlyList<DuplicateItemVM> members) =>
				TryPickDecisiveQualityWinner(members, out DuplicateItemVM keep) ? keep : null;

			var plan = ComputePikPakFolderMergeSelection(option, swapSuggestedDirection, keepRule, BestQuality);
			return ApplyPikPakFolderSelectionPlan(plan);
		}

		internal FolderBestSelectionResult RunPikPakFolderBestSelection(
			PikPakFolderCoverageOption option,
			bool swapSuggestedDirection) {
			DuplicateItemVM? BestQuality(IReadOnlyList<DuplicateItemVM> members) =>
				TryPickDecisiveQualityWinner(members, out DuplicateItemVM keep) ? keep : null;
			var plan = ComputePikPakFolderMergeSelection(
				option, swapSuggestedDirection, PikPakFolderMergeKeepRule.BestQuality, BestQuality);
			int selected = ApplyPikPakFolderSelectionPlan(plan);
			return new FolderBestSelectionResult(selected, plan.ReviewOnlyGroups);
		}

		int ApplyPikPakFolderSelectionPlan(PikPakSelectionPlan plan) {
			if (plan.MatchedGroups == 0 || plan.ToCheck.Count == 0)
				return 0;

			// Manual-review groups keep their existing check state; only decided groups are touched.
			var affected = plan.Keepers
				.Concat(plan.ToCheck)
				.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
				.ToList();
			using var undoBatch = BeginSelectionUndoBatch();
			foreach (var item in affected)
				item.Checked = false;
			foreach (var item in plan.ToCheck)
				item.Checked = true;
			RefreshResultsView();
			return plan.ToCheck.Count;
		}

		internal FolderConsolidationPlan BuildPikPakFolderConsolidationPlan(
			PikPakFolderCoverageOption option,
			bool swapSuggestedDirection) {
			var (targetFolder, sourceFolder) = option.ResolveDirection(swapSuggestedDirection);
			var (_, sourceCoverage) = option.ResolveCoverage(swapSuggestedDirection);
			var groups = new List<FolderConsolidationGroupPlan>();
			var manualReviewGroupIds = new List<Guid>();
			var matchedSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var match in option.Matches) {
				var target = targetFolder.Equals(match.FolderA, StringComparison.OrdinalIgnoreCase)
					? match.FolderAItems : match.FolderBItems;
				var source = sourceFolder.Equals(match.FolderA, StringComparison.OrdinalIgnoreCase)
					? match.FolderAItems : match.FolderBItems;
				if (target.Count == 0 || source.Count == 0)
					continue;

				foreach (var item in source)
					matchedSourcePaths.Add(item.ItemInfo.Path);

				var candidates = target.Concat(source)
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
					.ToList();
				if (match.AutoBestReviewOnly || !TryPickDecisiveQualityWinner(candidates, out DuplicateItemVM keeper)) {
					manualReviewGroupIds.Add(match.GroupId);
					continue;
				}

				bool keeperInSource = source.Any(item => ReferenceEquals(item, keeper));
				string? preferredName = null;
				if (keeperInSource) {
					string organizedStem = Path.GetFileNameWithoutExtension(target[0].ItemInfo.Path);
					preferredName = organizedStem + Path.GetExtension(keeper.ItemInfo.Path);
				}

				groups.Add(new FolderConsolidationGroupPlan {
					GroupId = match.GroupId,
					Keeper = keeper,
					Losers = candidates.Where(item => !ReferenceEquals(item, keeper)).ToList(),
					KeeperNeedsMove = keeperInSource,
					PreferredKeeperFileName = preferredName,
				});
			}

			bool wholeSourceEligible = MayMergeWholeSource(sourceCoverage, manualReviewGroupIds.Count);
			IReadOnlyList<FolderMediaFile> uniqueSourceFiles = Array.Empty<FolderMediaFile>();
			if (wholeSourceEligible) {
				uniqueSourceFiles = Scanner.GetDirectFolderMediaFiles(sourceFolder)
					.Where(file => !matchedSourcePaths.Contains(file.Path))
					.ToList();
			}

			return new FolderConsolidationPlan {
				TargetFolder = targetFolder,
				SourceFolder = sourceFolder,
				SourceCoverage = sourceCoverage,
				WholeSourceEligible = wholeSourceEligible,
				Groups = groups,
				ManualReviewGroupIds = manualReviewGroupIds,
				UniqueSourceFiles = uniqueSourceFiles,
			};
		}

		internal async Task<FolderConsolidationResult> ExecutePikPakFolderConsolidationAsync(FolderConsolidationPlan plan) {
			var successfulGroupLosers = new List<DuplicateItemVM>();
			var keeperPathUpdates = new List<(DuplicateItemVM Item, string NewPath)>();
			int groupMoveFailures = 0;
			int keeperMovesSucceeded = 0;
			int uniqueMovesSucceeded = 0;
			int uniqueMoveFailures = 0;

			await Task.Run(() => {
				foreach (var group in plan.Groups) {
					bool groupSafe = true;
					if (group.KeeperNeedsMove) {
						string oldPath = group.Keeper.ItemInfo.Path;
						FileEntry? dbEntry = null;
						ScanEngine.GetFromDatabase(oldPath, out dbEntry);
						var moved = SafeFileTransfer.MoveVerified(oldPath, plan.TargetFolder, group.PreferredKeeperFileName);
						if (!moved.Success) {
							Logger.Instance.Error($"Safe consolidation could not move BEST '{oldPath}': {moved.Error}");
							groupMoveFailures++;
							groupSafe = false;
						}
						else {
							keeperMovesSucceeded++;
							if (dbEntry != null)
								ScanEngine.UpdateFilePathInDatabase(moved.NewPath, dbEntry);
							keeperPathUpdates.Add((group.Keeper, moved.NewPath));
						}
					}
					if (groupSafe)
						successfulGroupLosers.AddRange(group.Losers);
				}

				foreach (var file in plan.UniqueSourceFiles) {
					FileEntry? dbEntry = null;
					ScanEngine.GetFromDatabase(file.Path, out dbEntry);
					var moved = SafeFileTransfer.MoveVerified(file.Path, plan.TargetFolder);
					if (!moved.Success) {
						Logger.Instance.Error($"Safe consolidation could not move source-only '{file.Path}': {moved.Error}");
						uniqueMoveFailures++;
						continue;
					}
					uniqueMovesSucceeded++;
					if (dbEntry != null)
						ScanEngine.UpdateFilePathInDatabase(moved.NewPath, dbEntry);
				}

				if (keeperMovesSucceeded > 0 || uniqueMovesSucceeded > 0)
					ScanEngine.SaveDatabase();
			});

			foreach (var (item, newPath) in keeperPathUpdates)
				item.ItemInfo.Path = newPath;

			using (var undoBatch = BeginSelectionUndoBatch()) {
				foreach (var group in plan.Groups)
					group.Keeper.Checked = false;
				foreach (var loser in successfulGroupLosers.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance))
					loser.Checked = true;
			}
			RefreshResultsView();
			RefreshGroupStats();

			return new FolderConsolidationResult {
				GroupsPrepared = plan.Groups.Count - groupMoveFailures,
				GroupMoveFailures = groupMoveFailures,
				KeeperMovesSucceeded = keeperMovesSucceeded,
				UniqueMovesSucceeded = uniqueMovesSucceeded,
				UniqueMoveFailures = uniqueMoveFailures,
				SafeLosersMarked = successfulGroupLosers.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance).Count(),
			};
		}

		internal static PikPakSelectionPlan ComputePikPakFolderMergeSelection(
			PikPakFolderCoverageOption option,
			bool swapSuggestedDirection,
			PikPakFolderMergeKeepRule keepRule,
			Func<IReadOnlyList<DuplicateItemVM>, DuplicateItemVM?>? bestQualityPicker = null) {
			var plan = new PikPakSelectionPlan();
			if (option == null)
				return plan;

			var (targetFolder, sourceFolder) = option.ResolveDirection(swapSuggestedDirection);
			foreach (var match in option.Matches) {
				IReadOnlyList<DuplicateItemVM> a = match.FolderAItems;
				IReadOnlyList<DuplicateItemVM> b = match.FolderBItems;
				var target = targetFolder.Equals(match.FolderA, StringComparison.OrdinalIgnoreCase) ? a : b;
				var source = sourceFolder.Equals(match.FolderA, StringComparison.OrdinalIgnoreCase) ? a : b;
				if (target.Count == 0 || source.Count == 0)
					continue;

				if (match.ReviewOnly) {
					plan.ReviewOnlyGroups++;
					continue;
				}

				if (keepRule == PikPakFolderMergeKeepRule.Manual) {
					plan.MatchedGroups++;
					continue;
				}

				var candidates = target
					.Concat(source)
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
					.ToList();

				DuplicateItemVM? keeper;
				if (keepRule == PikPakFolderMergeKeepRule.BestQuality) {
					keeper = bestQualityPicker != null
						? bestQualityPicker(candidates)
						: TryPickDecisiveQualityWinner(candidates, out DuplicateItemVM decisive) ? decisive : null;
					if (keeper == null) {
						plan.ReviewOnlyGroups++;
						continue;
					}
				}
				else {
					keeper = keepRule switch {
						PikPakFolderMergeKeepRule.KeepTarget => target[0],
						PikPakFolderMergeKeepRule.KeepSource => source[0],
						PikPakFolderMergeKeepRule.Largest => PickPikPakKeeper(candidates, PikPakKeepRule.Largest),
						PikPakFolderMergeKeepRule.Smallest => PickPikPakKeeper(candidates, PikPakKeepRule.Smallest),
						PikPakFolderMergeKeepRule.Newest => PickPikPakKeeper(candidates, PikPakKeepRule.Newest),
						PikPakFolderMergeKeepRule.Oldest => PickPikPakKeeper(candidates, PikPakKeepRule.Oldest),
						_ => target[0],
					};
				}

				plan.MatchedGroups++;
				plan.Keepers.Add(keeper);
				foreach (var item in candidates)
					if (!ReferenceEquals(item, keeper))
						plan.ToCheck.Add(item);
			}
			return plan;
		}

		static string PikPakItemFolder(DuplicateItemVM item) {
			string folder = item.ItemInfo.Folder;
			return NormalizePikPakPath(string.IsNullOrWhiteSpace(folder) ? GetPikPakFolder(item.ItemInfo.Path) : folder);
		}

		static double PercentForSuggestion(int matched, int total) =>
			total <= 0 ? 0d : Math.Min(100d, matched * 100d / total);
	}
}
