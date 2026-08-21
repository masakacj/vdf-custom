// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Linq;
using ReactiveUI;
using VDF.Core;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// Presentation-level quality recommendation. Every duplicate group gets one winner;
	/// IsConfirmed says whether the same choice is safe enough for unattended actions.
	/// </summary>
	public sealed record BestRecommendation(DuplicateItemVM Winner, bool IsConfirmed, string Reason);

	public partial class MainWindowVM : ReactiveObject {
		internal const double WholeSourceCoverageThreshold = 90d;

		internal static bool MayMergeWholeSource(double confirmedSourceCoverage, int reviewOnlyGroups) =>
			confirmedSourceCoverage >= WholeSourceCoverageThreshold && reviewOnlyGroups == 0;

		/// <summary>
		/// Determines whether the resource identity/edition itself is too ambiguous for
		/// replacement. Quality ties are handled separately by TryPickDecisiveQualityWinner.
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

			// Cross-codec bitrate comparisons are not reliable enough for destructive choices.
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
		/// Conservative BEST gate. A copy is automatic only if it is not worse on any
		/// comparable quality signal and is strictly better than every competing copy on at
		/// least one signal. Ties, missing metadata and quality trade-offs remain manual.
		/// File size is deliberately excluded from unattended quality decisions.
		///
		/// Still images are recommendation-only: resolution/format metadata cannot prove that
		/// a larger image was not upscaled, sharpened or more aggressively recompressed. They
		/// therefore always require a human confirmation before destructive consolidation.
		/// </summary>
		internal static bool TryPickDecisiveQualityWinner(
			IReadOnlyList<DuplicateItemVM> candidates,
			out DuplicateItemVM winner) {
			winner = candidates != null && candidates.Count > 0 ? candidates[0] : null!;
			if (candidates == null || candidates.Count < 2 || IsReviewOnlyResourceGroup(candidates))
				return false;

			if (candidates[0].ItemInfo.IsImage)
				return false;

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

		/// <summary>
		/// UI-facing BEST recommendation using the user's current criterion priority.
		/// The winner follows the configured order immediately; the conservative dominance
		/// gate still controls IsConfirmed so changing priorities cannot silently make an
		/// unsafe destructive action automatic.
		/// </summary>
		internal BestRecommendation RecommendBestUsingCurrentRules(IReadOnlyList<DuplicateItemVM> candidates) =>
			RecommendBest(candidates, QualityCriteriaOrder);

		/// <summary>
		/// Always returns one most-likely BEST. This legacy overload preserves the fixed
		/// weighted recommendation used by tests/callers that do not opt into user rules.
		/// </summary>
		internal static BestRecommendation RecommendBest(IReadOnlyList<DuplicateItemVM> candidates) {
			if (candidates == null || candidates.Count == 0)
				throw new ArgumentException("At least one candidate is required.", nameof(candidates));
			if (candidates.Count == 1)
				return new BestRecommendation(candidates[0], true, "BEST：当前只有一个候选副本。");

			bool confirmed = TryPickDecisiveQualityWinner(candidates, out DuplicateItemVM decisive);
			DuplicateItemVM winner = confirmed ? decisive : PickLikelyQualityWinner(candidates);
			return BuildBestRecommendation(winner, candidates, confirmed, sizeIsWeakTieBreaker: true);
		}

		/// <summary>
		/// User-configurable BEST: quality criteria are lexicographic in the chosen order.
		/// Physical file size is excluded; when the user's winner differs from the strict
		/// dominance winner the recommendation remains review-only.
		/// </summary>
		internal static BestRecommendation RecommendBest(
			IReadOnlyList<DuplicateItemVM> candidates,
			IEnumerable<string> criteriaOrder) {
			if (candidates == null || candidates.Count == 0)
				throw new ArgumentException("At least one candidate is required.", nameof(candidates));
			if (candidates.Count == 1)
				return new BestRecommendation(candidates[0], true, "BEST：当前只有一个候选副本。");

			var criteria = ResolveBestCriteria(criteriaOrder).ToList();
			DuplicateItemVM preferred = VDF.Core.Utils.QualityRanker.PickKeeper(
				candidates.ToList(),
				criteria,
				item => item.ItemInfo.IsImage);
			bool hasDecisive = TryPickDecisiveQualityWinner(candidates, out DuplicateItemVM decisive);
			bool confirmed = hasDecisive && ReferenceEquals(preferred, decisive);
			return BuildBestRecommendation(preferred, candidates, confirmed, sizeIsWeakTieBreaker: false);
		}

		static BestRecommendation BuildBestRecommendation(
			DuplicateItemVM winner,
			IReadOnlyList<DuplicateItemVM> candidates,
			bool confirmed,
			bool sizeIsWeakTieBreaker) {
			string strengths = BuildRecommendationStrengths(winner, candidates);
			if (confirmed)
				return new BestRecommendation(winner, true,
					$"确认 BEST：{strengths}。该副本对同组其他副本不存在已知质量劣势，可用于自动处理。");

			string uncertainty = BuildRecommendationUncertainty(candidates);
			string sizeNote = sizeIsWeakTieBreaker
				? "文件大小只作为最后的弱参考，不会优先选择更小文件。"
				: "文件大小不参与 BEST 判断。";
			return new BestRecommendation(winner, false,
				$"推荐 BEST：{strengths}。需人工复核：{uncertainty}。{sizeNote}");
		}

		/// <summary>Legacy tuple seam retained for callers/tests; now every non-empty group receives a BEST.</summary>
		internal static (DuplicateItemVM? Best, string? Tooltip) PickDecisiveBestForResults(
			IReadOnlyList<DuplicateItemVM> candidates) {
			if (candidates == null || candidates.Count == 0)
				return (null, null);
			BestRecommendation recommendation = RecommendBest(candidates);
			return (recommendation.Winner, recommendation.Reason);
		}

		static DuplicateItemVM PickLikelyQualityWinner(IReadOnlyList<DuplicateItemVM> candidates) {
			var scored = candidates
				.Select((candidate, index) => (Candidate: candidate, Score: RecommendationScore(candidate, candidates), Index: index))
				.OrderByDescending(item => item.Score)
				.ThenBy(item => LightweightQualityDiagnostics.Penalty(item.Candidate))
				.ThenByDescending(item => item.Candidate.ItemInfo.FrameSizeInt)
				.ThenByDescending(item => item.Candidate.ItemInfo.HdrFormatRank)
				.ThenByDescending(item => item.Candidate.ItemInfo.BitRateKbs)
				// Size is deliberately a very late tie-breaker, and larger is weakly preferred
				// because the goal is most-likely source quality rather than smallest storage.
				.ThenByDescending(item => Math.Max(0, item.Candidate.ItemInfo.SizeLong))
				.ThenBy(item => item.Index)
				.ToList();
			return scored[0].Candidate;
		}

		static double RecommendationScore(DuplicateItemVM candidate, IReadOnlyList<DuplicateItemVM> group) {
			double score = 0;
			foreach (DuplicateItemVM other in group) {
				if (ReferenceEquals(candidate, other)) continue;
				score += CompareLowerWeighted(
					LightweightQualityDiagnostics.Penalty(candidate), LightweightQualityDiagnostics.Penalty(other), 8d);
				score += CompareHigherWeighted(candidate.ItemInfo.FrameSizeInt, other.ItemInfo.FrameSizeInt, 6d, requirePositive: true);
				score += CompareHigherWeighted(candidate.ItemInfo.HdrFormatRank, other.ItemInfo.HdrFormatRank, 2d, requirePositive: false);
				score += CompareHigherWeighted((double)candidate.ItemInfo.BitRateKbs, (double)other.ItemInfo.BitRateKbs, 4d, requirePositive: true, nearTieRatio: 0.05d);
				score += CompareHigherWeighted(candidate.ItemInfo.Fps, other.ItemInfo.Fps, 1.5d, requirePositive: true, absoluteTie: 0.5d);

				decimal candidateBpp = BitsPerPixel(candidate.ItemInfo);
				decimal otherBpp = BitsPerPixel(other.ItemInfo);
				score += CompareHigherWeighted((double)candidateBpp, (double)otherBpp, 2.5d, requirePositive: true, nearTieRatio: 0.05d);
				score += CompareHigherWeighted((double)candidate.ItemInfo.AudioBitRateKbs, (double)other.ItemInfo.AudioBitRateKbs, 1d, requirePositive: true, nearTieRatio: 0.05d);
				score += CompareHigherWeighted(candidate.ItemInfo.AudioSampleRate, other.ItemInfo.AudioSampleRate, 0.75d, requirePositive: true);
			// Physical size is intentionally tiny compared with every actual quality signal.
				score += CompareHigherWeighted(Math.Max(0, candidate.ItemInfo.SizeLong), Math.Max(0, other.ItemInfo.SizeLong), 0.15d, requirePositive: true, nearTieRatio: 0.03d);
			}
			return score;
		}

		static double CompareLowerWeighted(double candidate, double other, double weight) {
			if (Math.Abs(candidate - other) < 0.0001d) return 0d;
			return candidate < other ? weight : -weight;
		}

		static double CompareHigherWeighted(
			double candidate, double other, double weight, bool requirePositive,
			double nearTieRatio = 0d, double absoluteTie = 0d) {
			if (requirePositive && (candidate <= 0 || other <= 0)) return 0d;
			double tolerance = Math.Max(absoluteTie, Math.Max(Math.Abs(candidate), Math.Abs(other)) * nearTieRatio);
			if (Math.Abs(candidate - other) <= tolerance) return 0d;
			return candidate > other ? weight : -weight;
		}

		static string BuildRecommendationStrengths(DuplicateItemVM winner, IReadOnlyList<DuplicateItemVM> group) {
			var strengths = new List<string>();
			int minPenalty = group.Min(LightweightQualityDiagnostics.Penalty);
			int winnerPenalty = LightweightQualityDiagnostics.Penalty(winner);
			if (winnerPenalty == minPenalty && group.Any(item => LightweightQualityDiagnostics.Penalty(item) > minPenalty))
				strengths.Add("轻量画质诊断更干净");

			if (winner.ItemInfo.FrameSizeInt > 0 &&
				winner.ItemInfo.FrameSizeInt == group.Max(item => item.ItemInfo.FrameSizeInt) &&
				group.Any(item => item.ItemInfo.FrameSizeInt > 0 && item.ItemInfo.FrameSizeInt < winner.ItemInfo.FrameSizeInt))
				strengths.Add("分辨率更高");

			if (winner.ItemInfo.HdrFormatRank == group.Max(item => item.ItemInfo.HdrFormatRank) &&
				group.Any(item => item.ItemInfo.HdrFormatRank < winner.ItemInfo.HdrFormatRank))
				strengths.Add("动态范围规格更高");

			if (winner.ItemInfo.BitRateKbs > 0 && winner.ItemInfo.BitRateKbs == group.Max(item => item.ItemInfo.BitRateKbs) &&
				group.Any(item => item.ItemInfo.BitRateKbs > 0 && winner.ItemInfo.BitRateKbs > item.ItemInfo.BitRateKbs * 1.05m))
				strengths.Add("视频码率更高");

			decimal winnerBpp = BitsPerPixel(winner.ItemInfo);
			var knownBpp = group.Select(item => BitsPerPixel(item.ItemInfo)).Where(value => value > 0).ToList();
			if (winnerBpp > 0 && knownBpp.Count > 1 && winnerBpp == knownBpp.Max() &&
				knownBpp.Any(value => winnerBpp > value * 1.05m))
				strengths.Add("单位像素码率更高");

			if (winner.ItemInfo.Fps > 0 && winner.ItemInfo.Fps == group.Max(item => item.ItemInfo.Fps) &&
				group.Any(item => item.ItemInfo.Fps > 0 && winner.ItemInfo.Fps > item.ItemInfo.Fps + 0.5f))
				strengths.Add("帧率更高");

			if (winner.ItemInfo.AudioBitRateKbs > 0 && winner.ItemInfo.AudioBitRateKbs == group.Max(item => item.ItemInfo.AudioBitRateKbs) &&
				group.Any(item => item.ItemInfo.AudioBitRateKbs > 0 && winner.ItemInfo.AudioBitRateKbs > item.ItemInfo.AudioBitRateKbs * 1.05m))
				strengths.Add("音频码率更高");

			if (strengths.Count == 0) {
				long maxSize = group.Max(item => Math.Max(0, item.ItemInfo.SizeLong));
				if (maxSize > 0 && Math.Max(0, winner.ItemInfo.SizeLong) == maxSize &&
					group.Any(item => Math.Max(0, item.ItemInfo.SizeLong) < maxSize))
					strengths.Add("主要质量指标接近，较大的文件体积仅作为弱参考");
				else
					strengths.Add("可比质量指标基本打平，按稳定顺序给出最可能候选");
			}
			return string.Join("、", strengths.Take(4));
		}

		static string BuildRecommendationUncertainty(IReadOnlyList<DuplicateItemVM> candidates) {
			if (candidates.Any(item => item.ItemInfo.Flags.HasFlag(DuplicateFlags.PartialClip)))
				return "存在局部片段匹配，不能确认是同一完整版本";
			if (candidates.Any(item => item.ItemInfo.Flags.HasFlag(DuplicateFlags.AiMatched)))
				return "包含 AI 匹配结果，资源身份需要人工确认";
			if (candidates.Any(item => item.ItemInfo.Flags.HasFlag(DuplicateFlags.Flipped)))
				return "包含水平翻转版本，需要人工确认版本关系";

			bool anyImage = candidates.Any(item => item.ItemInfo.IsImage);
			bool anyVideo = candidates.Any(item => !item.ItemInfo.IsImage);
			if (anyImage && anyVideo)
				return "组内同时包含图片和视频";
			if (anyImage) {
				if (candidates.Any(item => item.ItemInfo.FrameSizeInt <= 0))
					return "部分图片缺少有效分辨率";
				if (candidates.Select(item => (item.ItemInfo.Format ?? string.Empty).Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
					return "图片格式不同，不能仅按分辨率自动确认";
				return "图片仅凭分辨率和格式无法排除放大、锐化或重压缩差异，因此必须人工确认";
			}

			var durations = candidates.Select(item => item.ItemInfo.Duration.TotalSeconds).ToList();
			if (durations.Any(seconds => seconds <= 0))
				return "部分视频缺少有效时长";
			if (durations.Max() - durations.Min() > Math.Max(1d, durations.Max() * 0.01d))
				return "视频时长存在明显差异，可能不是同一完整版本";
			if (candidates.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.Format) ? "<unknown>" : item.ItemInfo.Format.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
				return "视频编码格式不同，跨编码码率不能直接等价比较";
			if (candidates.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.HdrFormat) ? "SDR" : item.ItemInfo.HdrFormat.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
				return "HDR/SDR 或 HDR 格式不同";
			if (candidates.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.AudioChannel) ? "<none>" : item.ItemInfo.AudioChannel.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
				return "音频声道布局不同";
			if (candidates.Any(item => item.ItemInfo.FrameSizeInt <= 0 || item.ItemInfo.BitRateKbs <= 0))
				return "部分关键质量元数据缺失";
			return "质量指标存在互有胜负或接近打平，无法达到无人值守处理门槛";
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
	}
}
