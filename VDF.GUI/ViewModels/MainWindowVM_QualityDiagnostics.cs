// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Diagnostics;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
		/// <summary>
		/// Optional post-match quality diagnostic. It is intentionally independent from the
		/// three scan profiles: switching precise/quality/AI modes never changes this preference.
		/// </summary>
		public bool EnableLightweightQualityDiagnostics {
			get => LightweightQualityDiagnosticsPreference.Enabled;
			set {
				if (value == LightweightQualityDiagnosticsPreference.Enabled) return;
				LightweightQualityDiagnosticsPreference.Enabled = value;
				this.RaisePropertyChanged(nameof(EnableLightweightQualityDiagnostics));
				if (Duplicates.Count > 0) {
					RunLightweightQualityDiagnostics();
					RebuildResultsList();
				}
			}
		}

		/// <summary>
		/// Runs only after a duplicate comparison already exists. Media files are NEVER opened:
		/// it reuses the small gray-byte samples already present in ScannedFiles.db memory plus
		/// DuplicateItem metadata. The normal HDD analysis/read pipeline is therefore unchanged.
		/// </summary>
		internal void RunLightweightQualityDiagnostics() {
			LightweightQualityDiagnostics.Clear(Duplicates);
			if (!EnableLightweightQualityDiagnostics || Duplicates.Count < 2)
				return;

			var candidateGroups = Duplicates
				.Where(item => !item.ItemInfo.IsImage)
				.GroupBy(item => item.ItemInfo.GroupId)
				.Select(group => group.ToList())
				.Where(IsEligibleDiagnosticGroup)
				.ToList();
			if (candidateGroups.Count == 0)
				return;

			var requestedPaths = candidateGroups
				.SelectMany(group => group)
				.Select(item => item.ItemInfo.Path)
				.Distinct(CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
				.ToList();
			var cachedFrames = LightweightQualityDiagnostics.SnapshotCachedGrayFrames(requestedPaths);

			int warningFiles = 0;
			int warningGroups = 0;
			var sw = Stopwatch.StartNew();
			foreach (var group in candidateGroups) {
				var samples = new List<CachedVideoQualitySample>(group.Count);
				foreach (var item in group) {
					if (!cachedFrames.TryGetValue(item.ItemInfo.Path, out IReadOnlyList<byte[]>? frames))
						continue;
					if (!TryParseFrameSize(item.ItemInfo.FrameSize, out int width, out int height))
						continue;
					samples.Add(new CachedVideoQualitySample(
						item.ItemInfo.Path,
						width,
						height,
						item.ItemInfo.BitRateKbs > 0 ? (long)(item.ItemInfo.BitRateKbs * 1000m) : 0,
						Math.Max(0, item.ItemInfo.SizeLong),
						frames));
				}

				var groupFindings = LightweightQualityDiagnostics.AnalyzeGroup(samples);
				if (groupFindings.Count == 0) continue;
				warningGroups++;
				var byPath = groupFindings.ToDictionary(
					finding => finding.Key,
					CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
				foreach (var item in group) {
					if (!byPath.TryGetValue(item.ItemInfo.Path, out LightweightQualityFinding? finding))
						continue;
					LightweightQualityDiagnostics.Apply(item, finding);
					warningFiles++;
				}
			}
			sw.Stop();
			Logger.Instance.Info(
				$"Lightweight quality diagnostics: {warningFiles:N0} suspicious file(s) in {warningGroups:N0} duplicate group(s), " +
				$"{sw.ElapsedMilliseconds:N0} ms CPU/cache pass; no media files were read.");
		}

		static bool IsEligibleDiagnosticGroup(List<DuplicateItemVM> group) {
			if (group.Count < 2) return false;
			// Flipped/AI-only/partial matches are intentionally excluded: cached frame positions
			// are not guaranteed to be spatially aligned enough for the conservative overlay test.
			const DuplicateFlags unsupported = DuplicateFlags.Flipped | DuplicateFlags.AiMatched | DuplicateFlags.PartialClip;
			if (group.Any(item => (item.ItemInfo.Flags & unsupported) != 0))
				return false;

			var durations = group.Select(item => item.ItemInfo.Duration.TotalSeconds).ToList();
			if (durations.Any(seconds => seconds <= 0)) return false;
			double longest = durations.Max();
			double shortest = durations.Min();
			return longest - shortest <= Math.Max(1d, longest * 0.01d);
		}

		static bool TryParseFrameSize(string? frameSize, out int width, out int height) {
			width = height = 0;
			if (string.IsNullOrWhiteSpace(frameSize)) return false;
			int split = frameSize.IndexOf('x');
			if (split < 0) split = frameSize.IndexOf('×');
			return split > 0 &&
				int.TryParse(frameSize.AsSpan(0, split), out width) &&
				int.TryParse(frameSize.AsSpan(split + 1), out height) &&
				width > 0 && height > 0;
		}

		internal static string BuildLightweightQualityGroupSummary(ResultsGroupHeader group) {
			int transcode = 0;
			int watermark = 0;
			foreach (var row in group.Rows) {
				var finding = LightweightQualityDiagnostics.GetFinding(row.Item);
				if (finding == null) continue;
				if (finding.Warning.HasFlag(LightweightQualityWarning.SuspectedTranscodeOrUpscale)) transcode++;
				if (finding.Warning.HasFlag(LightweightQualityWarning.SuspectedWatermark)) watermark++;
			}
			if (transcode == 0 && watermark == 0) return string.Empty;
			var parts = new List<string>();
			if (transcode > 0) parts.Add($"{transcode:N0} 疑似二压/放大");
			if (watermark > 0) parts.Add($"{watermark:N0} 疑似水印");
			return "⚠ 轻量画质诊断：" + string.Join(" · ", parts) + "（仅建议，不自动删除）";
		}
	}
}
