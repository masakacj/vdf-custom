// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using ReactiveUI;
using VDF.Core;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
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
		/// File size is deliberately excluded as a quality signal.
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
				// A unique maximum is necessarily strictly above every other candidate; keeping
				// it in a local variable also avoids capturing the out parameter in a LINQ lambda.
				var imageWinner = top[0];
				winner = imageWinner;
				return true;
			}

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
	}
}
