// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Text.Json.Serialization;

namespace VDF.GUI.Data {
	/// <summary>
	/// Scan profiles are intentionally named after the user's resource-cleanup goal.
	/// Only the first three are exposed on the Setup screen. DeepClean and Custom are
	/// retained as legacy/internal states so old settings keep loading safely.
	/// </summary>
	public enum ScanProfile {
		/// <summary>Very high-confidence copies/re-encodes. Fastest and safest for bulk cleanup.</summary>
		ExactAndNear,
		/// <summary>Normal resource consolidation: re-encodes, watermarks, borders and quality changes.</summary>
		EditedAndAltered,
		/// <summary>Deep same-source detection using visual AI, but never partial/clip containment.</summary>
		AiScan,
		/// <summary>Legacy profile: AI + audio/visual partial detection. Hidden from the main UI.</summary>
		DeepClean,
		/// <summary>Internal sentinel for expert settings that do not match one of the three fixed profiles.</summary>
		Custom,
	}

	/// <summary>The settings a profile manages. Everything else stays the user's business.</summary>
	public sealed class ScanKnobs {
		[JsonInclude] public float Percent { get; set; }
		[JsonInclude] public bool CompareHorizontallyFlipped { get; set; }
		[JsonInclude] public bool IgnoreBlackPixels { get; set; }
		[JsonInclude] public bool IgnoreWhitePixels { get; set; }
		[JsonInclude] public bool EnablePartialClipDetection { get; set; }
		[JsonInclude] public bool UseAiMatching { get; set; }
		[JsonInclude] public bool EnableAiPartialDetection { get; set; }
	}

	/// <summary>
	/// Maps scan profiles onto the managed settings knobs. The three user-facing profiles
	/// deliberately keep BOTH partial-clip engines disabled: a two-minute excerpt may be
	/// related to a two-hour source, but it is not a lower-quality duplicate and must never
	/// enter BEST-quality deletion/merge logic.
	/// </summary>
	internal static class ScanProfileMapper {

		// 1) Precise dedupe: conservative, high-confidence complete-resource matching.
		internal static readonly ScanKnobs ExactAndNear = new() {
			Percent = 98f,
			CompareHorizontallyFlipped = false,
			IgnoreBlackPixels = false,
			IgnoreWhitePixels = false,
			EnablePartialClipDetection = false,
			UseAiMatching = false,
			EnableAiPartialDetection = false,
		};

		// 2) Quality consolidation (default): catches ordinary alternate releases while
		// staying on the classic visual matcher so false positives remain manageable.
		internal static readonly ScanKnobs EditedAndAltered = new() {
			Percent = 92f,
			CompareHorizontallyFlipped = true,
			IgnoreBlackPixels = true,
			IgnoreWhitePixels = true,
			EnablePartialClipDetection = false,
			UseAiMatching = false,
			EnableAiPartialDetection = false,
		};

		// 3) Deep same-source: add DINO visual embeddings for crops/zoom/heavy edits,
		// but DO NOT enable AI partial detection. Complete-resource review only.
		internal static readonly ScanKnobs AiScan = new() {
			Percent = 92f,
			CompareHorizontallyFlipped = true,
			IgnoreBlackPixels = true,
			IgnoreWhitePixels = true,
			EnablePartialClipDetection = false,
			UseAiMatching = true,
			EnableAiPartialDetection = false,
		};

		// Kept only to preserve old saved settings / advanced experiments. This profile
		// is never shown on the Setup screen and is never selected by the three-mode UI.
		internal static readonly ScanKnobs DeepClean = new() {
			Percent = 92f,
			CompareHorizontallyFlipped = true,
			IgnoreBlackPixels = true,
			IgnoreWhitePixels = true,
			EnablePartialClipDetection = true,
			UseAiMatching = true,
			EnableAiPartialDetection = true,
		};

		internal static ScanKnobs? BundleFor(ScanProfile profile) => profile switch {
			ScanProfile.ExactAndNear => ExactAndNear,
			ScanProfile.EditedAndAltered => EditedAndAltered,
			ScanProfile.AiScan => AiScan,
			ScanProfile.DeepClean => DeepClean,
			_ => null,
		};

		internal static bool IsUserFacing(ScanProfile profile) =>
			profile is ScanProfile.ExactAndNear or ScanProfile.EditedAndAltered or ScanProfile.AiScan;

		internal static ScanKnobs Capture(SettingsFile settings) => new() {
			Percent = settings.Percent,
			CompareHorizontallyFlipped = settings.CompareHorizontallyFlipped,
			IgnoreBlackPixels = settings.IgnoreBlackPixels,
			IgnoreWhitePixels = settings.IgnoreWhitePixels,
			EnablePartialClipDetection = settings.EnablePartialClipDetection,
			UseAiMatching = settings.UseAiMatching,
			EnableAiPartialDetection = settings.EnableAiPartialDetection,
		};

		internal static bool Matches(SettingsFile settings, ScanKnobs knobs) =>
			settings.Percent == knobs.Percent &&
			settings.CompareHorizontallyFlipped == knobs.CompareHorizontallyFlipped &&
			settings.IgnoreBlackPixels == knobs.IgnoreBlackPixels &&
			settings.IgnoreWhitePixels == knobs.IgnoreWhitePixels &&
			settings.EnablePartialClipDetection == knobs.EnablePartialClipDetection &&
			settings.UseAiMatching == knobs.UseAiMatching &&
			settings.EnableAiPartialDetection == knobs.EnableAiPartialDetection;

		/// <summary>The profile the current knob values correspond to; Custom when none match.</summary>
		internal static ScanProfile Detect(SettingsFile settings) =>
			Matches(settings, ExactAndNear) ? ScanProfile.ExactAndNear :
			Matches(settings, AiScan) ? ScanProfile.AiScan :
			Matches(settings, EditedAndAltered) ? ScanProfile.EditedAndAltered :
			Matches(settings, DeepClean) ? ScanProfile.DeepClean :
			ScanProfile.Custom;

		/// <summary>
		/// Applies a profile's bundle. Custom remains an internal restore mechanism for
		/// legacy/expert settings, but is not exposed as a main scan mode.
		/// </summary>
		internal static void Apply(ScanProfile profile, SettingsFile settings) {
			if (profile == ScanProfile.Custom) {
				if (settings.CustomScanKnobs is ScanKnobs backup)
					ApplyKnobs(backup, settings);
				return;
			}
			if (Detect(settings) == ScanProfile.Custom)
				settings.CustomScanKnobs = Capture(settings);
			ApplyKnobs(BundleFor(profile)!, settings);
		}

		static void ApplyKnobs(ScanKnobs knobs, SettingsFile settings) {
			settings.Percent = knobs.Percent;
			settings.CompareHorizontallyFlipped = knobs.CompareHorizontallyFlipped;
			settings.IgnoreBlackPixels = knobs.IgnoreBlackPixels;
			settings.IgnoreWhitePixels = knobs.IgnoreWhitePixels;
			settings.EnablePartialClipDetection = knobs.EnablePartialClipDetection;
			settings.UseAiMatching = knobs.UseAiMatching;
			settings.EnableAiPartialDetection = knobs.EnableAiPartialDetection;
		}
	}
}
