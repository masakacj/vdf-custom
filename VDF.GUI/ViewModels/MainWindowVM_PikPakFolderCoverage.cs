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

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// One folder-pair bucket built FROM ordinary VDF file-duplicate groups. The folder
	/// relationship never replaces file matching: it only re-groups already matched files
	/// so users can review/merge related directories in batches.
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
			CoverageA = Percent(matchedFilesA, TotalFilesA);
			CoverageB = Percent(matchedFilesB, TotalFilesB);
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
		public double CoverageA { get; }
		public double CoverageB { get; }
		public bool SuggestedTargetIsA { get; }
		public string SuggestedTargetFolder => SuggestedTargetIsA ? FolderA : FolderB;
		public string SuggestedSourceFolder => SuggestedTargetIsA ? FolderB : FolderA;
		public double SuggestedTargetCoverage => SuggestedTargetIsA ? CoverageA : CoverageB;
		public double SuggestedSourceCoverage => SuggestedTargetIsA ? CoverageB : CoverageA;
		public int SuggestedTargetTotalFiles => SuggestedTargetIsA ? TotalFilesA : TotalFilesB;
		public int SuggestedSourceTotalFiles => SuggestedTargetIsA ? TotalFilesB : TotalFilesA;

		/// <summary>
		/// Full paths are intentionally kept in the row: this is a merge planner, so hiding
		/// the actual destination/source behind a basename is too risky.
		/// </summary>
		public string DisplayText => string.Format(
			CultureInfo.CurrentCulture,
			"建议目标 {0}  ←  {1}  ·  {2} 个相似组  ·  目标被覆盖 {3:0.#}% / 来源被覆盖 {4:0.#}%",
			SuggestedTargetFolder,
			SuggestedSourceFolder,
			MatchedGroupCount,
			SuggestedTargetCoverage,
			SuggestedSourceCoverage);

		internal IReadOnlyList<PikPakFolderCoverageMatch> Matches { get; }

		internal (string Target, string Source) ResolveDirection(bool swapSuggestedDirection) =>
			swapSuggestedDirection
				? (SuggestedSourceFolder, SuggestedTargetFolder)
				: (SuggestedTargetFolder, SuggestedSourceFolder);

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

	public partial class MainWindowVM : ReactiveObject {
		/// <summary>
		/// Builds folder-pair merge buckets from the CURRENT visible file-duplicate groups.
		/// Folder totals come from the cached scan DB; there is no second disk walk.
		/// </summary>
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
				double coverageA = PercentForSuggestion(acc.MatchedFilesA.Count, totalA);
				double coverageB = PercentForSuggestion(acc.MatchedFilesB.Count, totalB);

				// Typical collection merge: the subset folder is ~100% covered while the
				// resource-rich folder has a lower own coverage. Therefore the lower-coverage
				// side is the suggested destination. Equal coverage falls back to the larger
				// folder. It is only a suggestion; the UI always offers one-click direction swap.
				bool suggestA = Math.Abs(coverageA - coverageB) > 0.0001
					? coverageA < coverageB
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

			// Coverage first surfaces "small folder is completely represented by collection A"
			// even when only one stray file crosses into a large/flat miscellaneous folder.
			return result
				.OrderByDescending(option => Math.Max(option.CoverageA, option.CoverageB))
				.ThenByDescending(option => option.MatchedGroupCount)
				.ThenByDescending(option => Math.Min(option.CoverageA, option.CoverageB))
				.ThenByDescending(option => Math.Max(option.TotalFilesA, option.TotalFilesB))
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
