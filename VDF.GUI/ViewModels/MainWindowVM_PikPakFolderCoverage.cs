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
using VDF.GUI.Data;
using VDF.GUI.Utils;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// One folder-pair bucket built FROM ordinary VDF file-duplicate groups. File matching
	/// remains the source of truth; the folder relationship is only an organization layer.
	/// Confirmed coverage excludes groups that must be manually reviewed because the resource
	/// identity itself is uncertain. BEST-quality uncertainty is tracked separately so a valid
	/// folder relation can remain visible even when the user must choose the keeper manually.
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
			ConfirmedMatchedGroupCount = Matches.Count(m => !m.ReviewOnly);
			ReviewOnlyGroupCount = Matches.Count - ConfirmedMatchedGroupCount;
			AutoBestReviewOnlyGroupCount = Matches.Count(m => m.AutoBestReviewOnly);
			ConfirmedMatchedFilesA = Matches
				.Where(m => !m.ReviewOnly)
				.SelectMany(m => m.FolderAItems)
				.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
				.Count();
			ConfirmedMatchedFilesB = Matches
				.Where(m => !m.ReviewOnly)
				.SelectMany(m => m.FolderBItems)
				.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
				.Count();
			EstimatedResourcesA = EstimateResourceTotal(TotalFilesA, ConfirmedMatchedFilesA, ConfirmedMatchedGroupCount);
			EstimatedResourcesB = EstimateResourceTotal(TotalFilesB, ConfirmedMatchedFilesB, ConfirmedMatchedGroupCount);
			CoverageA = Percent(ConfirmedMatchedGroupCount, EstimatedResourcesA);
			CoverageB = Percent(ConfirmedMatchedGroupCount, EstimatedResourcesB);
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
		public int ConfirmedMatchedGroupCount { get; }
		public int ReviewOnlyGroupCount { get; }
		public int AutoBestReviewOnlyGroupCount { get; }
		public int ConfirmedMatchedFilesA { get; }
		public int ConfirmedMatchedFilesB { get; }
		public int EstimatedResourcesA { get; }
		public int EstimatedResourcesB { get; }
		public double CoverageA { get; }
		public double CoverageB { get; }
		/// <summary>
		/// Conservative folder match score: overlap relative to the larger side. Using the
		/// lower directional coverage prevents a tiny 100%-contained folder from pretending
		/// that it is a 100% match for a huge miscellaneous directory.
		/// </summary>
		public double FolderMatchPercent => Math.Min(CoverageA, CoverageB);
		public bool SuggestedTargetIsA { get; }
		public string SuggestedTargetFolder => SuggestedTargetIsA ? FolderA : FolderB;
		public string SuggestedSourceFolder => SuggestedTargetIsA ? FolderB : FolderA;
		public double SuggestedTargetCoverage => SuggestedTargetIsA ? CoverageA : CoverageB;
		public double SuggestedSourceCoverage => SuggestedTargetIsA ? CoverageB : CoverageA;
		public int SuggestedTargetTotalFiles => SuggestedTargetIsA ? TotalFilesA : TotalFilesB;
		public int SuggestedSourceTotalFiles => SuggestedTargetIsA ? TotalFilesB : TotalFilesA;

		public string DisplayText => string.Format(
			CultureInfo.CurrentCulture,
			"目标 {0}（{1:N0} 文件 / {2} / 约 {3:N0} 资源）  ←  {4}（{5:N0} 文件 / {6} / 约 {7:N0} 资源）  ·  文件夹匹配 {8:0.#}%  ·  确认命中 {9:N0} / 关系待复核 {10:N0} / BEST待复核 {11:N0} 资源  ·  确认覆盖 {12:0.#}% / {13:0.#}%",
			SuggestedTargetFolder,
			SuggestedTargetIsA ? TotalFilesA : TotalFilesB,
			(SuggestedTargetIsA ? TotalBytesA : TotalBytesB).BytesToString(),
			SuggestedTargetIsA ? EstimatedResourcesA : EstimatedResourcesB,
			SuggestedSourceFolder,
			SuggestedTargetIsA ? TotalFilesB : TotalFilesA,
			(SuggestedTargetIsA ? TotalBytesB : TotalBytesA).BytesToString(),
			SuggestedTargetIsA ? EstimatedResourcesB : EstimatedResourcesA,
			FolderMatchPercent,
			ConfirmedMatchedGroupCount,
			ReviewOnlyGroupCount,
			AutoBestReviewOnlyGroupCount,
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
		/// <summary>Resource identity/edition is uncertain; do not use it for confirmed coverage.</summary>
		public required bool ReviewOnly { get; init; }
		/// <summary>Resource relation is valid, but no single copy has decisive quality dominance.</summary>
		public required bool AutoBestReviewOnly { get; init; }
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

	internal readonly record struct FolderBestSelectionResult(int Selected, int ReviewOnlyGroups);

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
		public required IReadOnlyList<Guid> ManualReviewGroupIds { get; init; }
		public required IReadOnlyList<FolderMediaFile> UniqueSourceFiles { get; init; }
		public int MatchedGroups => Groups.Count;
		public int ManualReviewGroups => ManualReviewGroupIds.Count;
		public int TotalRelatedGroups => MatchedGroups + ManualReviewGroups;
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

		internal static bool MayMergeWholeSource(double confirmedSourceCoverage, int reviewOnlyGroups) =>
			confirmedSourceCoverage >= WholeSourceCoverageThreshold && reviewOnlyGroups == 0;

		/// <summary>
		/// Determines whether the matching evidence itself is too ambiguous for replacement.
		/// Quality ties are handled separately by TryPickDecisiveQualityWinner so they still
		/// count as valid folder-overlap evidence.
		/// </summary>
		internal static bool IsReviewOnlyResourceGroup(IReadOnlyList<DuplicateItemVM> candidates) {
			if (candidates == null || candidates.Count < 2)
				return true;

			if (candidates.Any(item =>
				item.ItemInfo.Flags.HasFlag(DuplicateFlags.PartialClip) ||
				item.ItemInfo.Flags.HasFlag(DuplicateFlags.AiMatched) ||
				item.ItemInfo.Flags.HasFlag(DuplicateFlags.Flipped)))
				return true;

			bool anyImage = candidates.Any(item => item.ItemInfo.IsImage);
			bool anyVideo = candidates.Any(item => !item.ItemInfo.IsImage);
			if (anyImage && anyVideo)
				return true;

			if (anyImage) {
				if (candidates.Any(item => item.ItemInfo.FrameSizeInt <= 0))
					return true;
				// Different image encodings may change alpha/loss/detail semantics; do not rank
				// JPEG/PNG/HEIC against each other automatically even when resolution differs.
				var formats = candidates
					.Select(item => (item.ItemInfo.Format ?? string.Empty).Trim())
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();
				return formats.Count > 1;
			}

			var durations = candidates.Select(item => item.ItemInfo.Duration.TotalSeconds).ToList();
			if (durations.Any(seconds => seconds <= 0))
				return true;
			double longest = durations.Max();
			double shortest = durations.Min();
			if (longest - shortest > Math.Max(1d, longest * 0.01d))
				return true;

			var hdrKinds = candidates
				.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.HdrFormat) ? "SDR" : item.ItemInfo.HdrFormat.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (hdrKinds.Count > 1)
				return true;

			var channelLayouts = candidates
				.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.AudioChannel) ? "<none>" : item.ItemInfo.AudioChannel.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (channelLayouts.Count > 1)
				return true;

			// Bitrates across codecs are not directly comparable. A smaller HEVC encode may be
			// cleaner than a larger H.264 encode, so codec changes are manual-review editions.
			var videoFormats = candidates
				.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.Format) ? "<unknown>" : item.ItemInfo.Format.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (videoFormats.Count > 1)
				return true;

			var audioFormats = candidates
				.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.AudioFormat) ? "<none>" : item.ItemInfo.AudioFormat.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (audioFormats.Count > 1)
				return true;

			var knownFps = candidates.Select(item => item.ItemInfo.Fps).Where(fps => fps > 0).ToList();
			if (knownFps.Count > 1 && knownFps.Max() - knownFps.Min() > 0.5f)
				return true;

			return false;
		}

		/// <summary>
		/// Conservative BEST gate. A candidate is automatic only when it is not worse on ANY
		/// comparable quality signal and is strictly better than every competing copy on at
		/// least one signal. Exact/near ties, missing essential metadata, and trade-offs such
		/// as "higher resolution but lower bits-per-pixel" are left for manual review.
		/// File size is intentionally not a quality signal here.
		/// </summary>
		internal static bool TryPickDecisiveQualityWinner(
			IReadOnlyList<DuplicateItemVM> candidates,
			out DuplicateItemVM winner) {
			winner = candidates != null && candidates.Count > 0 ? candidates[0] : null!;
			if (candidates == null || candidates.Count < 2 || IsReviewOnlyResourceGroup(candidates))
				return false;

			if (candidates[0].ItemInfo.IsImage) {
				int maxResolution = candidates.Max(item => item.ItemInfo.FrameSizeInt);
				var top = candidates.Where(item => item.ItemInfo.FrameSizeInt == maxResolution).ToList();
				if (top.Count != 1)
					return false;
				winner = top[0];
				return candidates.All(item => ReferenceEquals(item, winner) || winner.ItemInfo.FrameSizeInt > item.ItemInfo.FrameSizeInt);
			}

			// Resolution and video bitrate are the minimum evidence required to call a video
			// winner automatic. Partial metadata = evidence insufficient, so manual review.
			if (candidates.Any(item => item.ItemInfo.FrameSizeInt <= 0 || item.ItemInfo.BitRateKbs <= 0))
				return false;

			bool compareFps = candidates.Any(item => item.ItemInfo.Fps > 0);
			if (compareFps && candidates.Any(item => item.ItemInfo.Fps <= 0))
				return false;
			bool compareAudioBitrate = candidates.Any(item => item.ItemInfo.AudioBitRateKbs > 0);
			if (compareAudioBitrate && candidates.Any(item => item.ItemInfo.AudioBitRateKbs <= 0))
				return false;
			bool compareAudioSampleRate = candidates.Any(item => item.ItemInfo.AudioSampleRate > 0);
			if (compareAudioSampleRate && candidates.Any(item => item.ItemInfo.AudioSampleRate <= 0))
				return false;

			var dominant = candidates
				.Where(candidate => candidates.All(other =>
					ReferenceEquals(candidate, other) || QualityDominates(
						candidate, other, compareFps, compareAudioBitrate, compareAudioSampleRate)))
				.ToList();
			if (dominant.Count != 1)
				return false;

			winner = dominant[0];
			return true;
		}

		static bool QualityDominates(
			DuplicateItemVM candidate,
			DuplicateItemVM other,
			bool compareFps,
			bool compareAudioBitrate,
			bool compareAudioSampleRate) {
			bool strictlyBetter = false;

			int candidatePenalty = LightweightQualityDiagnostics.Penalty(candidate);
			int otherPenalty = LightweightQualityDiagnostics.Penalty(other);
			if (candidatePenalty > otherPenalty) return false;
			if (candidatePenalty < otherPenalty) strictlyBetter = true;

			if (candidate.ItemInfo.FrameSizeInt < other.ItemInfo.FrameSizeInt) return false;
			if (candidate.ItemInfo.FrameSizeInt > other.ItemInfo.FrameSizeInt) strictlyBetter = true;

			if (!CompareHigherNearTie(candidate.ItemInfo.BitRateKbs, other.ItemInfo.BitRateKbs, 0.05m, ref strictlyBetter))
				return false;

			if (compareFps) {
				float candidateFps = candidate.ItemInfo.Fps;
				float otherFps = other.ItemInfo.Fps;
				if (candidateFps + 0.5f < otherFps) return false;
				if (candidateFps > otherFps + 0.5f) strictlyBetter = true;

				decimal candidateBpp = BitsPerPixel(candidate.ItemInfo);
				decimal otherBpp = BitsPerPixel(other.ItemInfo);
				if (candidateBpp <= 0 || otherBpp <= 0) return false;
				if (!CompareHigherNearTie(candidateBpp, otherBpp, 0.05m, ref strictlyBetter))
					return false;
			}

			if (compareAudioBitrate &&
				!CompareHigherNearTie(candidate.ItemInfo.AudioBitRateKbs, other.ItemInfo.AudioBitRateKbs, 0.05m, ref strictlyBetter))
				return false;

			if (compareAudioSampleRate) {
				if (candidate.ItemInfo.AudioSampleRate < other.ItemInfo.AudioSampleRate) return false;
				if (candidate.ItemInfo.AudioSampleRate > other.ItemInfo.AudioSampleRate) strictlyBetter = true;
			}

			return strictlyBetter;
		}

		static bool CompareHigherNearTie(decimal candidate, decimal other, decimal toleranceRatio, ref bool strictlyBetter) {
			decimal tolerance = Math.Max(candidate, other) * toleranceRatio;
			if (candidate + tolerance < other)
				return false;
			if (candidate > other + tolerance)
				strictlyBetter = true;
			return true;
		}

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

		internal int RunPikPakFolderMergeSelection(
			PikPakFolderCoverageOption option,
			bool swapSuggestedDirection,
			PikPakFolderMergeKeepRule keepRule) {
			if (option == null || keepRule == PikPakFolderMergeKeepRule.Manual)
				return 0;

			DuplicateItemVM? BestQuality(IReadOnlyList<DuplicateItemVM> members) =>
				TryPickDecisiveQualityWinner(members, out DuplicateItemVM keep) ? keep : null;

			var plan = ComputePikPakFolderMergeSelection(option, swapSuggestedDirection, keepRule, BestQuality);
			return ApplyPikPakFolderSelectionPlan(option, plan);
		}

		internal FolderBestSelectionResult RunPikPakFolderBestSelection(
			PikPakFolderCoverageOption option,
			bool swapSuggestedDirection) {
			DuplicateItemVM? BestQuality(IReadOnlyList<DuplicateItemVM> members) =>
				TryPickDecisiveQualityWinner(members, out DuplicateItemVM keep) ? keep : null;
			var plan = ComputePikPakFolderMergeSelection(
				option, swapSuggestedDirection, PikPakFolderMergeKeepRule.BestQuality, BestQuality);
			int selected = ApplyPikPakFolderSelectionPlan(option, plan);
			return new FolderBestSelectionResult(selected, plan.ReviewOnlyGroups);
		}

		int ApplyPikPakFolderSelectionPlan(PikPakFolderCoverageOption option, PikPakSelectionPlan plan) {
			if (plan.MatchedGroups == 0 || plan.ToCheck.Count == 0)
				return 0;

			// Touch only groups the plan actually decided. Manual-review groups keep the user's
			// current check state intact instead of being silently cleared by a BEST preview.
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
