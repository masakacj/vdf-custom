// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Diagnostics;

namespace VDF.Core.Utils {
	/// <summary>
	/// A network/mapped path is only safe to enumerate from Everything when the active
	/// Everything configuration explicitly indexes an ancestor folder, monitors it for
	/// changes and has no global exclusions that could make the result incomplete.
	/// </summary>
	internal sealed record EverythingFolderIndexCoverage(string IndexedRoot, string SettingsPath);

	internal static class EverythingFolderIndexCoverageDetector {
		internal static bool TryVerify(
			string scanRoot,
			nint everythingWindow,
			out EverythingFolderIndexCoverage? coverage,
			out string? reason) {
			coverage = null;
			reason = null;
			if (!TryResolveActiveSettingsPath(everythingWindow, out string? settingsPath, out reason))
				return false;
			try {
				string text = File.ReadAllText(settingsPath);
				return TryVerifyFromIni(scanRoot, text, settingsPath, out coverage, out reason);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				reason = $"Everything Folder Index settings could not be read ({ex.Message})";
				return false;
			}
		}

		/// <summary>Pure verification seam used by tests.</summary>
		internal static bool TryVerifyFromIni(
			string scanRoot,
			string iniText,
			string settingsPath,
			out EverythingFolderIndexCoverage? coverage,
			out string? reason) {
			coverage = null;
			reason = null;
			Dictionary<string, string> ini = ParseIni(iniText);
			if (!ini.TryGetValue("folders", out string? rawFolders) || string.IsNullOrWhiteSpace(rawFolders)) {
				reason = "Everything has no configured Folder Index covering network paths";
				return false;
			}

			List<string> folders = ParseIniList(rawFolders);
			if (folders.Count == 0) {
				reason = "Everything Folder Index list is empty";
				return false;
			}

			string normalizedScanRoot = NormalizeDirectory(scanRoot);
			int matchedIndex = -1;
			string? matchedRoot = null;
			for (int i = 0; i < folders.Count; i++) {
				string candidate = NormalizeDirectory(folders[i]);
				if (!IsSameOrChild(normalizedScanRoot, candidate)) continue;
				if (matchedRoot == null || candidate.Length > matchedRoot.Length) {
					matchedRoot = candidate;
					matchedIndex = i;
				}
			}
			if (matchedRoot == null) {
				reason = $"Everything Folder Index does not cover '{scanRoot}'";
				return false;
			}

			// A configured network folder can become stale when monitoring is disabled.
			// VDF only trusts a Folder Index for scan enumeration when Everything is actively
			// monitoring that exact configured root. Scheduled-only indexes keep the native path.
			if (!ini.TryGetValue("folder_monitor_changes", out string? rawMonitor)) {
				reason = $"Everything Folder Index '{matchedRoot}' has no monitor-state metadata";
				return false;
			}
			List<string> monitorValues = ParseSimpleList(rawMonitor);
			if (matchedIndex >= monitorValues.Count || monitorValues[matchedIndex] != "1") {
				reason = $"Everything Folder Index '{matchedRoot}' is not monitoring changes";
				return false;
			}

			// Everything's own exclude rules happen before the IPC query. If any are active,
			// the index is not a complete source of truth for VDF's scan semantics, so fall
			// back rather than silently miss media.
			if (Enabled(ini, "exclude_list_enabled")) {
				if (Enabled(ini, "exclude_hidden_files_and_folders") || Enabled(ini, "exclude_system_files_and_folders")) {
					reason = "Everything global hidden/system exclusions are enabled";
					return false;
				}
				foreach (string key in new[] { "exclude_folders", "include_only_files", "exclude_files" }) {
					if (ini.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)) {
						reason = $"Everything global index filter '{key}' is active";
						return false;
					}
				}
			}

			coverage = new EverythingFolderIndexCoverage(matchedRoot, settingsPath);
			return true;
		}

		internal static List<string> ParseIniList(string value) {
			var result = new List<string>();
			var current = new System.Text.StringBuilder();
			bool quoted = false;
			for (int i = 0; i < value.Length; i++) {
				char c = value[i];
				if (c == '"') {
					quoted = !quoted;
					continue;
				}
				if (quoted && c == '\\' && i + 1 < value.Length) {
					// Everything.ini uses backslash escaping inside quoted list values.
					current.Append(value[++i]);
					continue;
				}
				if (!quoted && c == ',') {
					AddListValue(result, current);
					continue;
				}
				current.Append(c);
			}
			AddListValue(result, current);
			return result;
		}

		static void AddListValue(List<string> result, System.Text.StringBuilder current) {
			string item = current.ToString().Trim();
			current.Clear();
			if (item.Length > 0) result.Add(item);
		}

		static List<string> ParseSimpleList(string value) => value
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();

		static Dictionary<string, string> ParseIni(string text) {
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			bool inEverything = false;
			foreach (string raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)) {
				string line = raw.Trim().TrimStart('\uFEFF');
				if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
				if (line.StartsWith('[') && line.EndsWith(']')) {
					inEverything = line.Equals("[Everything]", StringComparison.OrdinalIgnoreCase);
					continue;
				}
				if (!inEverything) continue;
				int equals = line.IndexOf('=');
				if (equals <= 0) continue;
				result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
			}
			return result;
		}

		static bool Enabled(IReadOnlyDictionary<string, string> ini, string key) =>
			ini.TryGetValue(key, out string? value) && value.Trim() == "1";

		static bool TryResolveActiveSettingsPath(nint everythingWindow, out string path, out string? reason) {
			path = string.Empty;
			reason = null;
			string? exePath = TryGetEverythingExecutablePath(everythingWindow);
			string? exeIni = string.IsNullOrWhiteSpace(exePath)
				? null
				: Path.Combine(Path.GetDirectoryName(exePath) ?? string.Empty, "Everything.ini");

			// app_data is intentionally stored in the executable-directory INI even when
			// the rest of the settings live under %APPDATA%\Everything.
			if (!string.IsNullOrEmpty(exeIni) && File.Exists(exeIni)) {
				try {
					Dictionary<string, string> bootstrap = ParseIni(File.ReadAllText(exeIni));
					if (bootstrap.TryGetValue("app_data", out string? appData) && appData.Trim() == "1") {
						string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything", "Everything.ini");
						if (File.Exists(roaming)) {
							path = roaming;
							return true;
						}
						reason = "Everything is configured for %APPDATA% settings, but Everything.ini was not found there";
						return false;
					}
					path = exeIni;
					return true;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
					reason = $"Everything bootstrap settings could not be read ({ex.Message})";
					return false;
				}
			}

			// Fallback for portable/managed installs whose process path is unavailable.
			string[] candidates = {
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything", "Everything.ini"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.ini"),
			};
			foreach (string candidate in candidates) {
				if (!File.Exists(candidate)) continue;
				path = candidate;
				return true;
			}
			reason = "active Everything.ini could not be located";
			return false;
		}

		static string? TryGetEverythingExecutablePath(nint everythingWindow) {
			try {
				Native.GetWindowThreadProcessId(everythingWindow, out uint processId);
				if (processId == 0) return null;
				using Process process = Process.GetProcessById(checked((int)processId));
				return process.MainModule?.FileName;
			}
			catch {
				return null;
			}
		}

		static string NormalizeDirectory(string path) {
			try {
				string full = Path.GetFullPath(path);
				string? root = Path.GetPathRoot(full);
				if (!string.IsNullOrEmpty(root) && full.Equals(root, StringComparison.OrdinalIgnoreCase))
					return root;
				return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			}
			catch {
				return path.Trim().TrimEnd('\\', '/');
			}
		}

		static bool IsSameOrChild(string path, string root) {
			if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
			if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
			if (root.EndsWith('\\') || root.EndsWith('/')) return true;
			if (path.Length <= root.Length) return false;
			char separator = path[root.Length];
			return separator is '\\' or '/';
		}

		static class Native {
			[System.Runtime.InteropServices.DllImport("user32.dll")]
			internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
		}
	}
}
