// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using ReactiveUI;
using VDF.Core;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
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

				var confirmedMatches = acc.Matches.Where(match => !match.ReviewOnly).ToList();
				int confirmedGroups = confirmedMatches.Count;
				int confirmedFilesA = confirmedMatches.SelectMany(m => m.FolderAItems)
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance).Count();
				int confirmedFilesB = confirmedMatches.SelectMany(m => m.FolderBItems)
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance).Count();
				int resourcesA = PikPakFolderCoverageOption.EstimateResourceTotal(totalA, confirmedFilesA, confirmedGroups);
				int resourcesB = PikPakFolderCoverageOption.EstimateResourceTotal(totalB, confirmedFilesB, confirmedGroups);
				double coverageA = PercentForSuggestion(confirmedGroups, resourcesA);
				double coverageB = PercentForSuggestion(confirmedGroups, resourcesB);

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
				.ThenByDescending(option => option.ConfirmedMatchedGroupCount)
				.ThenBy(option => option.ReviewOnlyGroupCount)
				.ThenByDescending(option => Math.Min(option.CoverageA, option.CoverageB))
				.ThenByDescending(option => Math.Max(option.EstimatedResourcesA, option.EstimatedResourcesB))
				.ThenBy(option => option.FolderA, comparer)
				.ThenBy(option => option.FolderB, comparer)
				.ToList();
		}

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
				var candidates = a.Concat(b)
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
					.ToList();
				bool relationReviewOnly = IsReviewOnlyResourceGroup(candidates);
				bool autoBestReviewOnly = relationReviewOnly || !TryPickDecisiveQualityWinner(candidates, out _);
				Matches.Add(new PikPakFolderCoverageMatch {
					GroupId = groupId,
					FolderA = FolderA,
					FolderB = FolderB,
					FolderAItems = a.ToList(),
					FolderBItems = b.ToList(),
					ReviewOnly = relationReviewOnly,
					AutoBestReviewOnly = autoBestReviewOnly,
				});
			}
		}
	}
}
