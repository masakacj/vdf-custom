// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Utils;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// One folder-pair bucket built FROM ordinary VDF file-duplicate groups. File matching
	/// remains the source of truth; the folder relationship is only an organization layer.
	/// Coverage is expressed in estimated logical resources, not raw duplicate-file count:
	/// multiple copies of the same matched group inside one folder count as one resource.
	/// </summary>
	public sealed class PikPakFolderCoverageOption {
		internal PikPakFolderCoverageOption(
			string folderA,
			string folderB,
			IReadOnlyList<PikPakFolderCoverageMatch> matches,
			int totalFilesA,
			int totalFilesB,
			long totalBytesA,
			long totalBytesB,
			int matchedFilesA,
			int matchedFilesB,
			bool suggestAAsTarget) {
			FolderA = folderA;
			FolderB = folderB;
			Matches = matches;
			TotalFilesA = Math.Max(totalFilesA, matchedFilesA);
			TotalFilesB = Math.Max(totalFilesB, matchedFilesB);
			TotalBytesA = Math.Max(0, totalBytesA);
			TotalBytesB = Math.Max(0, totalBytesB);
			MatchedFilesA = matchedFilesA;
			MatchedFilesB = matchedFilesB;
			EstimatedResourcesA = EstimateResourceTotal(TotalFilesA, matchedFilesA, MatchedGroupCount);
			EstimatedResourcesB = EstimateResourceTotal(TotalFilesB, matchedFilesB, MatchedGroupCount);
			CoverageA = Percent(MatchedGroupCount, EstimatedResourcesA);
			CoverageB = Percent(MatchedGroupCount, EstimatedResourcesB);
			SuggestedTargetIsA = suggestAAsTarget;
		}

		public string FolderA { get; }
		public string FolderB { get; }
		public int TotalFilesA { get; }
		public int TotalFilesB { get; }
		public long TotalBytesA { get; }
		public long TotalBytesB { get; }
		public int MatchedFilesA { get; }
		public int MatchedFilesB { get; }
		public int MatchedGroupCount => Matches.Count;
		public int EstimatedResourcesA { get; }
		public int EstimatedResourcesB { get; }
		public double CoverageA { get; }
		public double CoverageB { get; }
		public bool SuggestedTargetIsA { get; }
		public string SuggestedTargetFolder => SuggestedTargetIsA ? FolderA : FolderB;
		public string SuggestedSourceFolder => SuggestedTargetIsA ? FolderB : FolderA;
		public double SuggestedTargetCoverage => SuggestedTargetIsA ? CoverageA : CoverageB;
		public double SuggestedSourceCoverage => SuggestedTargetIsA ? CoverageB : CoverageA;
		public int SuggestedTargetTotalFiles => SuggestedTargetIsA ? TotalFilesA : TotalFilesB;
		public int SuggestedSourceTotalFiles => SuggestedTargetIsA ? TotalFilesB : TotalFilesA;

		/// <summary>Always show full path, file count and size: merge direction must be auditable.</summary>
		public string DisplayText => string.Format(
			CultureInfo.CurrentCulture,
			"目标 {0}（{1:N0} 文件 / {2} / 约 {3:N0} 资源）  ←  {4}（{5:N0} 文件 / {6} / 约 {7:N0} 资源）  ·  命中 {8:N0} 资源  ·  覆盖 {9:0.#}% / {10:0.#}%",
			SuggestedTargetFolder,
			SuggestedTargetIsA ? TotalFilesA : TotalFilesB,
			(SuggestedTargetIsA ? TotalBytesA : TotalBytesB).BytesToString(),
			SuggestedTargetIsA ? EstimatedResourcesA : EstimatedResourcesB,
			SuggestedSourceFolder,
			SuggestedTargetIsA ? TotalFilesB : TotalFilesA,
			(SuggestedTargetIsA ? TotalBytesB : TotalBytesA).BytesToString(),
			SuggestedTargetIsA ? EstimatedResourcesB : EstimatedResourcesA,
			MatchedGroupCount,
			SuggestedTargetCoverage,
			SuggestedSourceCoverage);

		internal IReadOnlyList<PikPakFolderCoverageMatch> Matches { get; }

		internal (string Target, string Source) ResolveDirection(bool swapSuggestedDirection) =>
			swapSuggestedDirection
				? (SuggestedSourceFolder, SuggestedTargetFolder)
				: (SuggestedTargetFolder, SuggestedSourceFolder);

		internal (double TargetCoverage, double SourceCoverage) ResolveCoverage(bool swapSuggestedDirection) =>
			swapSuggestedDirection
				? (SuggestedSourceCoverage, SuggestedTargetCoverage)
				: (SuggestedTargetCoverage, SuggestedSourceCoverage);

		internal (int TargetFiles, long TargetBytes, int SourceFiles, long SourceBytes) ResolveFileStats(bool swapSuggestedDirection) {
			bool targetA = swapSuggestedDirection ? !SuggestedTargetIsA : SuggestedTargetIsA;
			return targetA
				? (TotalFilesA, TotalBytesA, TotalFilesB, TotalBytesB)
				: (TotalFilesB, TotalBytesB, TotalFilesA, TotalBytesA);
		}

		internal static int EstimateResourceTotal(int totalFiles, int matchedFiles, int matchedGroups) {
			int duplicateExtrasInsideMatchedGroups = Math.Max(0, matchedFiles - matchedGroups);
			return Math.Max(matchedGroups, Math.Max(0, totalFiles - duplicateExtrasInsideMatchedGroups));
		}

		static double Percent(int matched, int total) => total <= 0 ? 0d : Math.Min(100d, matched * 100d / total);
	}

	internal sealed class PikPakFolderCoverageMatch {
		public required Guid GroupId { get; init; }
		public required string FolderA { get; init; }
		public required string FolderB { get; init; }
		public required IReadOnlyList<DuplicateItemVM> FolderAItems { get; init; }
		public required IReadOnlyList<DuplicateItemVM> FolderBItems { get; init; }
	}

	internal enum PikPakFolderMergeKeepRule {
		KeepTarget = 0,
		KeepSource = 1,
		BestQuality = 2,
		Largest = 3,
		Smallest = 4,
		Newest = 5,
		Oldest = 6,
		Manual = 7,
	}

	internal sealed class FolderConsolidationGroupPlan {
		public required Guid GroupId { get; init; }
		public required DuplicateItemVM Keeper { get; init; }
		public required IReadOnlyList<DuplicateItemVM> Losers { get; init; }
		public required bool KeeperNeedsMove { get; init; }
		public string? PreferredKeeperFileName { get; init; }
	}

	internal sealed class FolderConsolidationPlan {
		public required string TargetFolder { get; init; }
		public required string SourceFolder { get; init; }
		public required double SourceCoverage { get; init; }
		public required bool WholeSourceEligible { get; init; }
		public required IReadOnlyList<FolderConsolidationGroupPlan> Groups { get; init; }
		public required IReadOnlyList<FolderMediaFile> UniqueSourceFiles { get; init; }
		public int MatchedGroups => Groups.Count;
		public int KeeperMoveCount => Groups.Count(g => g.KeeperNeedsMove);
		public int LoserCount => Groups.Sum(g => g.Losers.Count);
		public long UniqueSourceBytes => UniqueSourceFiles.Sum(f => f.SizeBytes);
	}

	internal sealed class FolderConsolidationResult {
		public int GroupsPrepared { get; init; }
		public int GroupMoveFailures { get; init; }
		public int KeeperMovesSucceeded { get; init; }
		public int UniqueMovesSucceeded { get; init; }
		public int UniqueMoveFailures { get; init; }
		public int SafeLosersMarked { get; init; }
	}

	public partial class MainWindowVM : ReactiveObject {
		internal const double WholeSourceCoverageThreshold = 90d;

		internal List<PikPakFolderCoverageOption> BuildPikPakFolderCoverageOptions() {
			var groups = GetPikPakVisibleGroupsInDisplayOrder();
			if (groups.Count == 0)
				return new List<PikPakFolderCoverageOption>();

			var folders = groups
				.SelectMany(group => group)
				.Select(PikPakItemFolder)
				.Where(folder => folder.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			var stats = Scanner.GetDirectFolderMediaStats(folders);
			return ComputePikPakFolderCoverageOptions(groups, stats);
		}

		internal static List<PikPakFolderCoverageOption> ComputePikPakFolderCoverageOptions(
			IReadOnlyList<List<DuplicateItemVM>> groups,
			IReadOnlyDictionary<string, FolderMediaStats>? folderStats = null) {
			var comparer = StringComparer.OrdinalIgnoreCase;
			var participantCounts = new Dictionary<string, HashSet<DuplicateItemVM>>(comparer);
			var accumulators = new Dictionary<string, FolderPairAccumulator>(comparer);

			foreach (var group in groups) {
				if (group.Count < 2)
					continue;

				var byFolder = new Dictionary<string, List<DuplicateItemVM>>(comparer);
				foreach (var item in group) {
					string folder = PikPakItemFolder(item);
					if (folder.Length == 0)
						continue;
					if (!byFolder.TryGetValue(folder, out var list))
						byFolder[folder] = list = new List<DuplicateItemVM>();
					list.Add(item);
					if (!participantCounts.TryGetValue(folder, out var participants))
						participantCounts[folder] = participants = new HashSet<DuplicateItemVM>(ReferenceEqualityComparer<DuplicateItemVM>.Instance);
					participants.Add(item);
				}

				if (byFolder.Count < 2)
					continue;

				var folders = byFolder.Keys.OrderBy(path => path, comparer).ToList();
				for (int i = 0; i < folders.Count - 1; i++) {
					for (int j = i + 1; j < folders.Count; j++) {
						string a = folders[i];
						string b = folders[j];
						string key = a + "\0" + b;
						if (!accumulators.TryGetValue(key, out var acc))
							accumulators[key] = acc = new FolderPairAccumulator(a, b);
						acc.Add(group[0].ItemInfo.GroupId, byFolder[a], byFolder[b]);
					}
				}
			}

			var normalizedStats = new Dictionary<string, FolderMediaStats>(comparer);
			if (folderStats != null)
				foreach (var pair in folderStats)
					normalizedStats[NormalizePikPakPath(pair.Key)] = pair.Value;

			var result = new List<PikPakFolderCoverageOption>(accumulators.Count);
			foreach (var acc in accumulators.Values) {
				int participantsA = participantCounts.TryGetValue(acc.FolderA, out var pa) ? pa.Count : acc.MatchedFilesA.Count;
				int participantsB = participantCounts.TryGetValue(acc.FolderB, out var pb) ? pb.Count : acc.MatchedFilesB.Count;
				FolderMediaStats statsA = normalizedStats.TryGetValue(acc.FolderA, out var sa) ? sa : default;
				FolderMediaStats statsB = normalizedStats.TryGetValue(acc.FolderB, out var sb) ? sb : default;
				int totalA = Math.Max(statsA.FileCount, participantsA);
				int totalB = Math.Max(statsB.FileCount, participantsB);
				int resourcesA = PikPakFolderCoverageOption.EstimateResourceTotal(totalA, acc.MatchedFilesA.Count, acc.Matches.Count);
				int resourcesB = PikPakFolderCoverageOption.EstimateResourceTotal(totalB, acc.MatchedFilesB.Count, acc.Matches.Count);
				double coverageA = PercentForSuggestion(acc.Matches.Count, resourcesA);
				double coverageB = PercentForSuggestion(acc.Matches.Count, resourcesB);

				bool suggestA = Math.Abs(coverageA - coverageB) > 0.0001
					? coverageA < coverageB
					: resourcesA != resourcesB
						? resourcesA > resourcesB
						: totalA != totalB
							? totalA > totalB
							: comparer.Compare(acc.FolderA, acc.FolderB) <= 0;

				result.Add(new PikPakFolderCoverageOption(
					acc.FolderA,
					acc.FolderB,
					acc.Matches,
					totalA,
					totalB,
					statsA.TotalBytes,
					statsB.TotalBytes,
					acc.MatchedFilesA.Count,
					acc.MatchedFilesB.Count,
					suggestA));
			}

			return result
				.OrderByDescending(option => Math.Max(option.CoverageA, option.CoverageB))
				.ThenByDescending(option => option.MatchedGroupCount)
				.ThenByDescending(option => Math.Min(option.CoverageA, option.CoverageB))
				.ThenByDescending(option => Math.Max(option.EstimatedResourcesA, option.EstimatedResourcesB))
				.ThenBy(option => option.FolderA, comparer)
				.ThenBy(option => option.FolderB, comparer)
				.ToList();
		}

		internal int RunPikPakFolderMergeSelection(
			PikPakFolderCoverageOption option,
			bool swapSuggestedDirection,
			PikPakFolderMergeKeepRule keepRule) {
			if (option == null || keepRule == PikPakFolderMergeKeepRule.Manual)
				return 0;

			DuplicateItemVM BestQuality(IReadOnlyList<DuplicateItemVM> members) {
				var (keep, _) = QualityRanker.PickKeeperWithReason(
					members.ToList(), ResolveCriteria(QualityCriteriaOrder), d => d.ItemInfo.IsImage);
				return keep;
			}

			var plan = ComputePikPakFolderMergeSelection(option, swapSuggestedDirection, keepRule, BestQuality);
			if (plan.MatchedGroups == 0 || plan.ToCheck.Count == 0)
				return 0;

			var pairMembers = option.Matches
				.SelectMany(match => match.FolderAItems.Concat(match.FolderBItems))
				.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
				.ToList();

			using var undoBatch = BeginSelectionUndoBatch();
			foreach (var item in pairMembers)
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
			var matchedSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			DuplicateItemVM BestQuality(IReadOnlyList<DuplicateItemVM> members) =>
				QualityRanker.PickKeeper(
					members.ToList(), ResolveCriteria(QualityCriteriaOrder), d => d.ItemInfo.IsImage);

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
				var keeper = BestQuality(candidates);
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

			bool wholeSourceEligible = sourceCoverage >= WholeSourceCoverageThreshold;
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
				UniqueSourceFiles = uniqueSourceFiles,
			};
		}

		/// <summary>
		/// Executes only reversible/non-destructive parts of consolidation: move the BEST
		/// keeper into the target when it currently lives in the source, optionally move
		/// source-only files when source coverage is >= 90%, and mark lower-quality copies.
		/// No loser is deleted here. If a BEST move fails, that group's losers are NOT marked.
		/// </summary>
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
			Func<IReadOnlyList<DuplicateItemVM>, DuplicateItemVM>? bestQualityPicker = null) {
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

				plan.MatchedGroups++;
				if (keepRule == PikPakFolderMergeKeepRule.Manual)
					continue;

				var candidates = target
					.Concat(source)
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
					.ToList();
				DuplicateItemVM keeper = keepRule switch {
					PikPakFolderMergeKeepRule.KeepTarget => target[0],
					PikPakFolderMergeKeepRule.KeepSource => source[0],
					PikPakFolderMergeKeepRule.BestQuality => bestQualityPicker?.Invoke(candidates) ?? target[0],
					PikPakFolderMergeKeepRule.Largest => PickPikPakKeeper(candidates, PikPakKeepRule.Largest),
					PikPakFolderMergeKeepRule.Smallest => PickPikPakKeeper(candidates, PikPakKeepRule.Smallest),
					PikPakFolderMergeKeepRule.Newest => PickPikPakKeeper(candidates, PikPakKeepRule.Newest),
					PikPakFolderMergeKeepRule.Oldest => PickPikPakKeeper(candidates, PikPakKeepRule.Oldest),
					_ => target[0],
				};
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

		static double PercentForSuggestion(int matched, int total) => total <= 0 ? 0d : Math.Min(100d, matched * 100d / total);

		sealed class FolderPairAccumulator {
			readonly HashSet<Guid> groupIds = new();
			public FolderPairAccumulator(string folderA, string folderB) {
				FolderA = folderA;
				FolderB = folderB;
			}
			public string FolderA { get; }
			public string FolderB { get; }
			public List<PikPakFolderCoverageMatch> Matches { get; } = new();
			public HashSet<DuplicateItemVM> MatchedFilesA { get; } = new(ReferenceEqualityComparer<DuplicateItemVM>.Instance);
			public HashSet<DuplicateItemVM> MatchedFilesB { get; } = new(ReferenceEqualityComparer<DuplicateItemVM>.Instance);

			public void Add(Guid groupId, IReadOnlyList<DuplicateItemVM> a, IReadOnlyList<DuplicateItemVM> b) {
				if (!groupIds.Add(groupId))
					return;
				foreach (var item in a) MatchedFilesA.Add(item);
				foreach (var item in b) MatchedFilesB.Add(item);
				Matches.Add(new PikPakFolderCoverageMatch {
					GroupId = groupId,
					FolderA = FolderA,
					FolderB = FolderB,
					FolderAItems = a.ToList(),
					FolderBItems = b.ToList(),
				});
			}
		}
	}
}
