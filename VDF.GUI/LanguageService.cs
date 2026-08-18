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

using System.Linq;
using System.Text.Json;
using Avalonia.Platform;
using ReactiveUI;

namespace VDF.GUI {
	public class LanguageService : ReactiveObject {
		Dictionary<string, string> _translations = new();
		string _currentLanguage = "zh-Hans";

		IReadOnlyList<string>? _availableLanguages;
		public IReadOnlyList<string> AvailableLanguages => _availableLanguages ??= LoadAvailableLanguages();
		public string CurrentLanguage {
			get => _currentLanguage;
			set {
				if (EqualityComparer<string>.Default.Equals(_currentLanguage, value))
					return;
				this.RaiseAndSetIfChanged(ref _currentLanguage, value);
				LoadLanguage(value);
			}
		}

		public void LoadLanguage(string langCode) {
			try {
				var english = LoadDictionary("en");
				var selected = string.Equals(langCode, "en", StringComparison.OrdinalIgnoreCase)
					? english
					: LoadDictionary(langCode);

				var merged = new Dictionary<string, string>(english, StringComparer.Ordinal);
				foreach (var pair in selected) {
					if (english.TryGetValue(pair.Key, out var fallback) &&
						!CompositeFormatCompatible(fallback, pair.Value)) {
						// A malformed translated format string can otherwise throw while the main
						// window is being constructed, which looks like a silent language-specific
						// startup failure. Fall back per-key instead of taking down the whole UI.
						continue;
					}
					merged[pair.Key] = pair.Value;
				}

				ApplyProductOverrides(langCode, merged);
				_translations = merged;
				this.RaisePropertyChanged("Item[]");
			}
			catch (Exception) {
				if (!string.Equals(langCode, "en", StringComparison.OrdinalIgnoreCase)) {
					_currentLanguage = "en";
					LoadLanguage("en");
				}
				else
					_translations = new();
			}
		}

		static Dictionary<string, string> LoadDictionary(string langCode) {
			var uri = new Uri($"avares://VDF.GUI/Assets/Locales/{langCode}.json");
			using var stream = AssetLoader.Open(uri);
			using var reader = new StreamReader(stream);
			var json = reader.ReadToEnd();
			return JsonSerializer.Deserialize(json, Data.GuiJsonContext.Default.DictionaryStringString) ?? new();
		}

		static void ApplyProductOverrides(string langCode, Dictionary<string, string> values) {
			if (!langCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
				return;

			// Three fixed modes for the custom resource-consolidation build. Keeping the
			// existing locale keys avoids breaking upstream locale parity while making the
			// Chinese-first UX match the actual behavior.
			values["Profile.Exact.Name"] = "精准去重";
			values["Profile.Exact.Desc"] = "优先找完全副本、重命名和轻度重编码版本；适合高置信批量清理。";
			values["Profile.Exact.Time"] = "最快 · 最安全";
			values["Profile.Edited.Name"] = "高质量整合";
			values["Profile.Edited.Desc"] = "找不同分辨率、码率、水印、黑边/白边、翻转等完整资源版本，并用于 BEST Quality 整合。";
			values["Profile.Edited.Time"] = "推荐 · 默认";
			values["Profile.Ai.Name"] = "深度同源";
			values["Profile.Ai.Desc"] = "在高质量整合基础上加入本地 AI，寻找裁剪、缩放、严重调色或重剪的完整同源版本；结果建议人工复核。";
			values["Profile.Ai.Time"] = "AI · 需复核";
		}

		/// <summary>
		/// Validates only .NET composite-format structure. Plain translated text is accepted
		/// unchanged. The index sequence must match English so callers using string.Format
		/// cannot crash because a translation dropped/added/malformed a placeholder.
		/// </summary>
		internal static bool CompositeFormatCompatible(string fallback, string translated) {
			if (!fallback.Contains('{') && !fallback.Contains('}') && !translated.Contains('{') && !translated.Contains('}'))
				return true;
			return TryReadFormatIndexes(fallback, out var a) &&
				TryReadFormatIndexes(translated, out var b) && a.SequenceEqual(b);
		}

		static bool TryReadFormatIndexes(string text, out List<int> indexes) {
			indexes = new List<int>();
			for (int i = 0; i < text.Length; i++) {
				char c = text[i];
				if (c == '{') {
					if (i + 1 < text.Length && text[i + 1] == '{') { i++; continue; }
					int j = i + 1;
					int value = 0;
					int digits = 0;
					while (j < text.Length && char.IsDigit(text[j])) {
						value = checked(value * 10 + (text[j] - '0'));
						digits++;
						j++;
					}
					if (digits == 0)
						return false;
					while (j < text.Length && text[j] != '}') {
						if (text[j] == '{') return false;
						j++;
					}
					if (j >= text.Length) return false;
					indexes.Add(value);
					i = j;
				}
				else if (c == '}') {
					if (i + 1 < text.Length && text[i + 1] == '}') { i++; continue; }
					return false;
				}
			}
			indexes.Sort();
			return true;
		}

		static IReadOnlyList<string> LoadAvailableLanguages() {
			try {
				var localeUri = new Uri("avares://VDF.GUI/Assets/Locales/");
				var assets = AssetLoader.GetAssets(localeUri, null)
					.Select(asset => Path.GetFileNameWithoutExtension(asset.AbsolutePath))
					.Where(name => !string.IsNullOrWhiteSpace(name))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
					.ToList();

				return assets.Count > 0 ? assets : new List<string> { "en" };
			}
			catch (Exception) {
				return new List<string> { "en" };
			}
		}
		public string this[string key] => _translations.TryGetValue(key, out var val) ? val : key;
	}
}
