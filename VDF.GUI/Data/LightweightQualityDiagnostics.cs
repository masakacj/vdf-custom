// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Runtime.CompilerServices;
using VDF.Core.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Data {
	[Flags]
	internal enum LightweightQualityWarning {
		None = 0,
		SuspectedTranscodeOrUpscale = 1,
		SuspectedWatermark = 2,
	}

	/// <summary>
	/// Pure analyzer input. Frames are the already cached VDF grayscale samples — the
	/// diagnostic stage never opens, seeks or decodes a media file.
	/// </summary>
	internal sealed record CachedVideoQualitySample(
		string Key,
		int Width,
		int Height,
		long BitRate,
		long FileSize,
		IReadOnlyList<byte[]> GrayFrames);

	internal sealed record LightweightQualityFinding(
		string Key,
		LightweightQualityWarning Warning,
		int Confidence,
		string Reason) {
		public int Penalty =>
			(Warning.HasFlag(LightweightQualityWarning.SuspectedWatermark) ? 2 : 0) +
			(Warning.HasFlag(LightweightQualityWarning.SuspectedTranscodeOrUpscale) ? 1 : 0);
	}

	/// <summary>
	/// Small independent preference so the feature stays outside the three fixed scan-profile
	/// knobs. Default ON; changing it only writes a tiny settings marker, never media storage.
	/// </summary>
	internal static class LightweightQualityDiagnosticsPreference {
		static readonly string PreferencePath = Path.Combine(CoreUtils.SettingsFolder, "LightweightQualityDiagnostics.setting");
		static bool enabled = Load();

		internal static bool Enabled {
			get => enabled;
			set {
				if (enabled == value) return;
				enabled = value;
				try {
					Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
					string temp = PreferencePath + ".tmp";
					File.WriteAllText(temp, value ? "1" : "0");
					File.Move(temp, PreferencePath, overwrite: true);
				}
				catch (Exception ex) {
					Logger.Instance.Warn($"Could not save lightweight quality-diagnostics preference: {ex.Message}");
				}
			}
		}

		static bool Load() {
			try {
				if (!File.Exists(PreferencePath)) return true;
				return File.ReadAllText(PreferencePath).Trim() != "0";
			}
			catch {
				return true;
			}
		}
	}

	/// <summary>
	/// Cache-only, conservative quality diagnostics. It intentionally does NOT try to be a
	/// full no-reference VQA model. It only emits a warning when the cached evidence is strong:
	///   1) a larger/higher-bitrate encode has materially LESS sampled detail, or
	///   2) one corner carries a persistent localized high-frequency overlay across frames.
	/// Warnings are advisory and feed BEST as a first-pass penalty; deletion remains explicit.
	/// </summary>
	internal static class LightweightQualityDiagnostics {
		internal const string QualityCriterionName = "Cached quality diagnostics";
		const int MaxFramesPerFile = 12;
		const int MinimumFramesForWatermark = 5;
		const double PersistenceRatio = 0.70;

		static readonly ConditionalWeakTable<DuplicateItemVM, LightweightQualityFinding> findings = new();

		internal static LightweightQualityFinding? GetFinding(DuplicateItemVM item) =>
			findings.TryGetValue(item, out LightweightQualityFinding? finding) ? finding : null;

		internal static int Penalty(DuplicateItemVM item) => GetFinding(item)?.Penalty ?? 0;

		internal static string WarningText(DuplicateItemVM item) => GetFinding(item) is { } finding
			? $"⚠ {finding.Reason}（置信度 {finding.Confidence}%）"
			: string.Empty;

		internal static void Clear(IEnumerable<DuplicateItemVM> items) {
			foreach (var item in items)
				findings.Remove(item);
		}

		internal static void Apply(DuplicateItemVM item, LightweightQualityFinding finding) {
			findings.Remove(item);
			findings.Add(item, finding);
		}

		/// <summary>
		/// Returns the cached grayscale arrays for requested video paths. No FileInfo, File.Exists,
		/// FFmpeg or media stream is touched here — this is deliberately safe for sleeping HDDs.
		/// </summary>
		internal static IReadOnlyDictionary<string, IReadOnlyList<byte[]>> SnapshotCachedGrayFrames(IEnumerable<string> paths) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var wanted = new HashSet<string>(paths.Where(p => !string.IsNullOrWhiteSpace(p)), comparer);
			var result = new Dictionary<string, IReadOnlyList<byte[]>>(comparer);
			if (wanted.Count == 0) return result;

			// The caller runs after matching has completed, when the DB is structurally stable.
			// Iterate the in-memory set directly: ToArray() would allocate a huge second list on
			// multi-million-entry libraries, while constructing FileEntry(path) would touch disk.
			foreach (var entry in DatabaseUtils.Database) {
				if (!wanted.Contains(entry.Path) || entry.IsImage)
					continue;
				var frames = entry.grayBytes
					.OrderBy(pair => pair.Key)
					.Select(pair => pair.Value)
					.Where(frame => frame != null && IsUsableSquareFrame(frame.Length))
					.Take(MaxFramesPerFile)
					.Cast<byte[]>()
					.ToList();
				if (frames.Count > 0)
					result[entry.Path] = frames;
				wanted.Remove(entry.Path);
				if (wanted.Count == 0) break;
			}
			return result;
		}

		internal static IReadOnlyList<LightweightQualityFinding> AnalyzeGroup(IReadOnlyList<CachedVideoQualitySample> samples) {
			if (samples == null || samples.Count < 2)
				return Array.Empty<LightweightQualityFinding>();

			var valid = samples
				.Where(s => s.Width > 0 && s.Height > 0 && s.GrayFrames.Count > 0)
				.ToList();
			if (valid.Count < 2)
				return Array.Empty<LightweightQualityFinding>();

			var detail = valid.ToDictionary(s => s.Key, DetailScore, StringComparer.OrdinalIgnoreCase);
			var transcodeEvidence = valid.ToDictionary(s => s.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
			var watermarkEvidence = valid.ToDictionary(s => s.Key, _ => 0, StringComparer.OrdinalIgnoreCase);

			for (int i = 0; i < valid.Count - 1; i++) {
				for (int j = i + 1; j < valid.Count; j++) {
					var a = valid[i];
					var b = valid[j];

					if (LooksLikeWastefulSecondEncode(a, b, detail[a.Key], detail[b.Key]))
						transcodeEvidence[a.Key]++;
					if (LooksLikeWastefulSecondEncode(b, a, detail[b.Key], detail[a.Key]))
						transcodeEvidence[b.Key]++;

					bool aOverlay = LooksLikeLocalizedPersistentOverlay(a.GrayFrames, b.GrayFrames);
					bool bOverlay = LooksLikeLocalizedPersistentOverlay(b.GrayFrames, a.GrayFrames);
					// Only assign a culprit when the signal is directional. Symmetric differences
					// are more likely crop/alignment/content differences and are intentionally ignored.
					if (aOverlay && !bOverlay) watermarkEvidence[a.Key]++;
					if (bOverlay && !aOverlay) watermarkEvidence[b.Key]++;
				}
			}

			int peers = valid.Count - 1;
			int requiredEvidence = valid.Count <= 2 ? 1 : Math.Max(2, (peers + 1) / 2);
			var output = new List<LightweightQualityFinding>();
			foreach (var sample in valid) {
				bool transcode = transcodeEvidence[sample.Key] >= requiredEvidence;
				bool watermark = watermarkEvidence[sample.Key] >= requiredEvidence;
				if (!transcode && !watermark) continue;

				LightweightQualityWarning warning = LightweightQualityWarning.None;
				if (transcode) warning |= LightweightQualityWarning.SuspectedTranscodeOrUpscale;
				if (watermark) warning |= LightweightQualityWarning.SuspectedWatermark;
				int evidence = Math.Max(transcodeEvidence[sample.Key], watermarkEvidence[sample.Key]);
				int confidence = Math.Clamp(72 + (evidence - requiredEvidence) * 8 + (transcode && watermark ? 8 : 0), 72, 96);
				string reason = (transcode, watermark) switch {
					(true, true) => "疑似二次转码/放大并带固定水印",
					(true, false) => "疑似二次转码/放大：参数更高但缓存采样细节明显更低",
					(false, true) => "疑似固定水印/角标：同一角部的额外高频覆盖持续出现在多帧",
					_ => string.Empty,
				};
				output.Add(new LightweightQualityFinding(sample.Key, warning, confidence, reason));
			}
			return output;
		}

		static bool LooksLikeWastefulSecondEncode(
			CachedVideoQualitySample candidate,
			CachedVideoQualitySample peer,
			double candidateDetail,
			double peerDetail) {
			if (candidateDetail <= 0 || peerDetail <= 0) return false;
			long candidatePixels = (long)candidate.Width * candidate.Height;
			long peerPixels = (long)peer.Width * peer.Height;
			if (candidatePixels <= 0 || peerPixels <= 0) return false;

			double pixelRatio = (double)candidatePixels / peerPixels;
			double detailRatio = candidateDetail / peerDetail;
			double bitrateRatio = candidate.BitRate > 0 && peer.BitRate > 0
				? (double)candidate.BitRate / peer.BitRate : 0d;
			double sizeRatio = candidate.FileSize > 0 && peer.FileSize > 0
				? (double)candidate.FileSize / peer.FileSize : 0d;

			// Fake-resolution / upscale pattern: substantially more pixels and no bitrate
			// saving, yet the already-normalized VDF sample carries clearly LESS detail.
			if (pixelRatio >= 1.75 && bitrateRatio >= 1.05 && detailRatio <= 0.90)
				return true;

			// Same-resolution or modest resize second encode: substantially more bits/bytes
			// were spent but the sampled structural detail dropped sharply.
			return bitrateRatio >= 1.50 && sizeRatio >= 1.25 && detailRatio <= 0.84;
		}

		static double DetailScore(CachedVideoQualitySample sample) {
			var values = sample.GrayFrames
				.Select(FrameDetail)
				.Where(v => v > 0)
				.OrderBy(v => v)
				.ToArray();
			if (values.Length == 0) return 0;
			int mid = values.Length / 2;
			return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) * 0.5;
		}

		static double FrameDetail(byte[] frame) {
			int n = SquareSize(frame.Length);
			if (n <= 1) return 0;
			long sum = 0;
			long edges = 0;
			for (int y = 0; y < n; y++) {
				int row = y * n;
				for (int x = 0; x < n; x++) {
					int p = frame[row + x];
					if (x + 1 < n) { sum += Math.Abs(p - frame[row + x + 1]); edges++; }
					if (y + 1 < n) { sum += Math.Abs(p - frame[row + n + x]); edges++; }
				}
			}
			return edges == 0 ? 0 : (double)sum / edges;
		}

		static bool LooksLikeLocalizedPersistentOverlay(IReadOnlyList<byte[]> candidate, IReadOnlyList<byte[]> peer) {
			int frameCount = Math.Min(candidate.Count, peer.Count);
			if (frameCount < MinimumFramesForWatermark) return false;
			int n = SquareSize(candidate[0].Length);
			if (n < 16 || SquareSize(peer[0].Length) != n) return false;
			for (int i = 0; i < frameCount; i++)
				if (candidate[i].Length != n * n || peer[i].Length != n * n) return false;

			int cornerWidth = Math.Max(4, n / 3);
			int cornerHeight = Math.Max(4, n / 4);
			int persistentNeeded = (int)Math.Ceiling(frameCount * PersistenceRatio);
			var cornerScores = new List<(int PersistentCells, double MeanAdvantage)>(4);

			for (int corner = 0; corner < 4; corner++) {
				int x0 = (corner % 2 == 0) ? 0 : n - cornerWidth;
				int y0 = (corner < 2) ? 0 : n - cornerHeight;
				int persistentCells = 0;
				double advantageSum = 0;
				for (int y = y0; y < y0 + cornerHeight; y++) {
					for (int x = x0; x < x0 + cornerWidth; x++) {
						int wins = 0;
						long positiveAdvantage = 0;
						for (int f = 0; f < frameCount; f++) {
							int advantage = LocalGradient(candidate[f], n, x, y) - LocalGradient(peer[f], n, x, y);
							if (advantage >= 32) { wins++; positiveAdvantage += advantage; }
						}
						if (wins < persistentNeeded) continue;
						persistentCells++;
						advantageSum += wins == 0 ? 0 : (double)positiveAdvantage / wins;
					}
				}
				cornerScores.Add((persistentCells,
					persistentCells == 0 ? 0 : advantageSum / persistentCells));
			}

			var ordered = cornerScores.OrderByDescending(c => c.PersistentCells).ToArray();
			int cornerCells = cornerWidth * cornerHeight;
			int minimumLocalizedCells = Math.Max(5, cornerCells / 12);
			if (ordered[0].PersistentCells < minimumLocalizedCells || ordered[0].MeanAdvantage < 45)
				return false;

			// A watermark/logo is expected to stay localized. A generally sharper source tends
			// to win in several corners, so reject non-localized edge-energy advantages.
			int second = ordered[1].PersistentCells;
			return second <= Math.Max(3, (int)Math.Floor(ordered[0].PersistentCells * 0.45));
		}

		static int LocalGradient(byte[] frame, int n, int x, int y) {
			int center = frame[y * n + x];
			int sum = 0;
			if (x > 0) sum += Math.Abs(center - frame[y * n + x - 1]);
			if (x + 1 < n) sum += Math.Abs(center - frame[y * n + x + 1]);
			if (y > 0) sum += Math.Abs(center - frame[(y - 1) * n + x]);
			if (y + 1 < n) sum += Math.Abs(center - frame[(y + 1) * n + x]);
			return sum;
		}

		static bool IsUsableSquareFrame(int length) => SquareSize(length) >= 16;

		static int SquareSize(int length) {
			if (length <= 0) return 0;
			int n = (int)Math.Sqrt(length);
			return n * n == length ? n : 0;
		}
	}
}
