// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Globalization;
using VDF.Core;
using VDF.Core.Utils;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// One folder-pair bucket built from ordinary VDF duplicate groups. Resource-relation
	/// confidence and automatic-BEST confidence are intentionally tracked separately.
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
		/// Bilateral folder match score. Taking the lower directional coverage prevents a tiny
		/// fully-contained folder from claiming a 100% match with a huge miscellaneous folder.
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
		/// <summary>Resource identity/edition is uncertain; excluded from confirmed coverage.</summary>
		public required bool ReviewOnly { get; init; }
		/// <summary>Relation is valid but no copy has decisive quality dominance.</summary>
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
}
