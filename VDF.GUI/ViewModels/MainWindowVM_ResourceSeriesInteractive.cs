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
	public partial class MainWindowVM : ReactiveObject {
		/// <summary>
		/// Interactive folder-merge workflow. Ambiguous groups keep a system recommendation,
		/// and the user can accept/change those recommendations directly in the preview.
		/// </summary>
		internal async Task ConsolidateSelectedResourceSeriesInteractiveAsync(IReadOnlyList<ResourceRelationHeader> headers) {
			if (headers == null || headers.Count == 0 || IsScanning || IsBusy)
				return;

			string initial = SuggestedSeriesConsolidationDestination(headers);
			var targetDialog = new ResourceConsolidationDialog(
				headers.Count == 1
					? $"已选择系列：{headers[0].TargetFolder}"
					: $"已选择 {headers.Count:N0} 个系列。",
				initial,
				headers.Count > 1,
				headers.Count == 1
					? new[] { headers[0].TargetFolder }.Concat(headers[0].SourceFolders).ToList()
					: null);
			string? selectedPath = await targetDialog.ShowDialog<string?>(ApplicationHelpers.MainWindow);
			if (string.IsNullOrWhiteSpace(selectedPath))
				return;

			var destinations = ResolveSeriesDestinations(headers, selectedPath);
			if (destinations == null) {
				await MessageBoxService.Show(
					"多个已选系列会映射到相同的目标系列目录。为避免自动改名，请分批合并这些同名系列。",
					title: "文件夹合并");
				return;
			}

			var manualReviews = BuildResourceSeriesManualReviews(headers);
			var emptyOverrides = new Dictionary<Guid, DuplicateItemVM>();
			var initialPlans = headers.Select(header => BuildResourceSeriesConsolidationPlanInteractive(
				header, destinations[header.SelectionKey], emptyOverrides)).ToList();
			var confirmedReviews = BuildResourceSeriesConfirmedReviews(initialPlans);

			ResourceSeriesConsolidationPreview BuildPreview(IReadOnlyDictionary<Guid, DuplicateItemVM> overrides) {
				var previewPlans = headers.Select(header => BuildResourceSeriesConsolidationPlanInteractive(
					header, destinations[header.SelectionKey], overrides)).ToList();
				return BuildResourceSeriesConsolidationPreview(previewPlans);
			}

			ResourceSeriesConsolidationPreview initialPreview = BuildResourceSeriesConsolidationPreview(initialPlans);
			var previewDialog = new ResourceConsolidationPreviewDialog(
				initialPreview, confirmedReviews, manualReviews, BuildPreview);
			Dictionary<Guid, DuplicateItemVM>? keeperOverrides =
				await previewDialog.ShowDialog<Dictionary<Guid, DuplicateItemVM>?>(ApplicationHelpers.MainWindow);
			if (keeperOverrides == null)
				return;

			var plans = headers.Select(header => BuildResourceSeriesConsolidationPlanInteractive(
				header, destinations[header.SelectionKey], keeperOverrides)).ToList();
			int manual = plans.Sum(plan => plan.ManualReviewGroupIds.Count);
			int conflicts = plans.Sum(plan => plan.PathConflictCount);

			IsBusyOverlayText = "正在按文件夹结构安全合并资源…";
			IsBusy = true;
			try {
				var results = new List<ResourceSeriesConsolidationResult>();
				foreach (ResourceSeriesConsolidationPlan plan in plans)
					results.Add(await ExecuteResourceSeriesConsolidationAsync(plan));

				await MessageBoxService.Show(
					$"文件夹合并完成。\n\n" +
					$"成功处理资源组：{results.Sum(r => r.GroupsPrepared):N0}\n" +
					$"BEST 移动/替换成功：{results.Sum(r => r.KeeperMovesSucceeded):N0}\n" +
					$"独有资源移动成功：{results.Sum(r => r.UniqueMovesSucceeded):N0}\n" +
					$"已标记可清理重复副本：{results.Sum(r => r.SafeLosersMarked):N0}\n" +
					$"移动失败：{results.Sum(r => r.GroupMoveFailures + r.UniqueMoveFailures):N0}\n" +
					$"仍待人工/冲突：{manual + conflicts:N0}\n\n" +
					"未在预览中确认的人工项、冲突项和失败项均保持原样。",
					title: "文件夹合并");
			}
			finally {
				IsBusy = false;
			}
		}

		internal IReadOnlyList<ResourceSeriesConfirmedReview> BuildResourceSeriesConfirmedReviews(
			IReadOnlyList<ResourceSeriesConsolidationPlan> plans) {
			var result = new List<ResourceSeriesConfirmedReview>();
			var seen = new HashSet<Guid>();
			foreach (ResourceSeriesGroupPlan group in plans.SelectMany(plan => plan.Groups)) {
				if (!seen.Add(group.GroupId)) continue;
				var candidates = new[] { group.Keeper }.Concat(group.Losers)
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
					.ToList();
				BestRecommendation recommendation = RecommendBest(candidates);
				result.Add(new ResourceSeriesConfirmedReview(
					group.GroupId,
					candidates,
					group.Keeper,
					recommendation.Reason));
			}
			return result;
		}

		internal IReadOnlyList<ResourceSeriesManualReview> BuildResourceSeriesManualReviews(
			IReadOnlyList<ResourceRelationHeader> headers) {
			var result = new List<ResourceSeriesManualReview>();
			var seen = new HashSet<Guid>();
			foreach (ResourceRelationHeader header in headers) {
				var matchesByGroup = header.SourceRelations
					.SelectMany(relation => relation.Option.Matches)
					.Where(match => header.DisplayedGroupIds.Contains(match.GroupId))
					.GroupBy(match => match.GroupId);
				foreach (var matchGroup in matchesByGroup) {
					if (!seen.Add(matchGroup.Key)) continue;
					var matches = matchGroup.ToList();
					var candidates = matches
						.SelectMany(match => match.FolderAItems.Concat(match.FolderBItems))
						.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
						.ToList();
					if (candidates.Count < 2) continue;
					bool automatic = !matches.Any(match => match.ReviewOnly) &&
						TryPickDecisiveQualityWinner(candidates, out _);
					if (automatic) continue;
					BestRecommendation recommendation = RecommendBest(candidates);
					result.Add(new ResourceSeriesManualReview(
						matchGroup.Key, candidates, recommendation.Winner, recommendation.Reason));
				}
			}
			return result;
		}

		internal ResourceSeriesConsolidationPlan BuildResourceSeriesConsolidationPlanInteractive(
			ResourceRelationHeader header,
			string destinationRoot,
			IReadOnlyDictionary<Guid, DuplicateItemVM>? keeperOverrides) {
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
				foreach (DuplicateItemVM candidate in candidates)
					matchedPaths.Add(Path.GetFullPath(candidate.ItemInfo.Path));

				DuplicateItemVM? keeper = null;
				if (!matches.Any(match => match.ReviewOnly) &&
					TryPickDecisiveQualityWinner(candidates, out DuplicateItemVM decisiveWinner)) {
					keeper = decisiveWinner;
				}
				else if (keeperOverrides != null && keeperOverrides.TryGetValue(matchGroup.Key, out DuplicateItemVM? manual) &&
					candidates.Any(candidate => ReferenceEquals(candidate, manual))) {
					keeper = manual;
				}
				if (keeper == null) {
					plan.ManualReviewGroupIds.Add(matchGroup.Key);
					continue;
				}

				string? sourceRoot = FindOwningSeriesRoot(keeper.ItemInfo.Path, roots);
				if (sourceRoot == null ||
					!TryBuildPreservedDestination(sourceRoot, keeper.ItemInfo.Path, plan.DestinationRoot, out string destination)) {
					plan.ManualReviewGroupIds.Add(matchGroup.Key);
					continue;
				}
				string source = Path.GetFullPath(keeper.ItemInfo.Path);
				bool samePath = source.Equals(destination, comparison);
				DuplicateItemVM? destinationMember = candidates.FirstOrDefault(item =>
					!ReferenceEquals(item, keeper) && Path.GetFullPath(item.ItemInfo.Path).Equals(destination, comparison));
				bool destinationIsGroupDuplicate = destinationMember != null;
				if ((!samePath && (Directory.Exists(destination) || (File.Exists(destination) && !destinationIsGroupDuplicate))) ||
					!plannedDestinations.Add(destination)) {
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
					DestinationMember = destinationMember,
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
	}
}
