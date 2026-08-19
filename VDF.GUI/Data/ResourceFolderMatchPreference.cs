// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Globalization;
using VDF.Core.Utils;

namespace VDF.GUI.Data {
	/// <summary>
	/// Independent resource-view preference. 0% preserves the historical behavior (show every
	/// cross-folder relation); raising it filters folder relationships by bilateral overlap.
	/// Stored separately from Settings.json so this small presentation preference cannot affect
	/// scan-profile serialization or startup compatibility.
	/// </summary>
	internal static class ResourceFolderMatchPreference {
		static readonly string PreferencePath = Path.Combine(CoreUtils.SettingsFolder, "ResourceFolderMatchThreshold.setting");
		static double minimumPercent = Load();

		internal static double MinimumPercent {
			get => minimumPercent;
			set {
				double clamped = Math.Clamp(value, 0d, 100d);
				if (Math.Abs(clamped - minimumPercent) < 0.0001d) return;
				minimumPercent = clamped;
				Save(clamped);
			}
		}

		static double Load() {
			try {
				if (!File.Exists(PreferencePath)) return 0d;
				string text = File.ReadAllText(PreferencePath).Trim();
				return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
					? Math.Clamp(value, 0d, 100d)
					: 0d;
			}
			catch {
				return 0d;
			}
		}

		static void Save(double value) {
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
				string temp = PreferencePath + ".tmp";
				File.WriteAllText(temp, value.ToString("0.###", CultureInfo.InvariantCulture));
				File.Move(temp, PreferencePath, overwrite: true);
			}
			catch (Exception ex) {
				Logger.Instance.Warn($"Could not save resource folder-match preference: {ex.Message}");
			}
		}
	}
}
