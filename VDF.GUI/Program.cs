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

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Threading.Tasks;
using System.CommandLine;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using ReactiveUI.Avalonia;
using VDF.GUI.Utils;

namespace VDF.GUI {
	class Program {
		[STAThread]
		public static int Main(string[] args) {
			Option<FileInfo> settingsOption = new("--settings", new[] { "-s" }) {
				Description = "Path to a settings file to load and save."
			};
			RootCommand rootCommand = new("VideoDuplicateFinder settings options");
			rootCommand.Options.Add(settingsOption);

			rootCommand.SetAction(parseResult => {
				if (parseResult.GetValue(settingsOption) is FileInfo parsedFile) {
					if (parsedFile.Exists) {
						Data.SettingsFile.SetSettingsPath(parsedFile.FullName);
						Console.Out.WriteLine($"Using custom settings file: '{parsedFile.FullName}'");
					}
					else {
						ConsoleAttach.EnsureConsole();
						Console.Error.WriteLine($"Settings file not found: '{parsedFile.FullName}'. Using default settings file.");
					}
				}

				try {
					BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
				}
				catch (Exception ex) {
					// WinExe has no visible console on normal launch. Always leave an early-startup
					// breadcrumb beside the settings/state location instead of silently vanishing.
					try {
						string folder = VDF.Core.Utils.CoreUtils.SettingsFolder;
						Directory.CreateDirectory(folder);
						File.WriteAllText(Path.Combine(folder, "startup-crash.txt"), ex.ToString());
					}
					catch { }
					throw;
				}
			});
			var parseResult = rootCommand.Parse(args);
			if (parseResult.Errors.Count > 0 || args.Contains("-h") || args.Contains("--help") || args.Contains("-?")) {
				ConsoleAttach.EnsureConsole();
			}
			return rootCommand.Parse(args).Invoke();
		}

		public static AppBuilder BuildAvaloniaApp() {
			var builder = AppBuilder.Configure<App>()
				.UsePlatformDetect()
				.With(new X11PlatformOptions { UseDBusFilePicker = false });

			// The explicit list was added for macOS CoreText CJK fallback. Applying that
			// cross-platform forced Windows through unavailable/non-native font families and
			// is a plausible trigger for machine-specific silent exits when zh-Hans is active.
			// Windows already has DirectWrite font fallback; let it use the installed system
			// Chinese fonts. Only macOS keeps the explicit fallback chain it actually needs.
			if (OperatingSystem.IsMacOS()) {
				builder = builder.With(new FontManagerOptions {
					FontFallbacks = new[] {
						new FontFallback { FontFamily = new FontFamily("PingFang SC") },
						new FontFallback { FontFamily = new FontFamily("Hiragino Sans") },
						new FontFallback { FontFamily = new FontFamily("Noto Sans CJK SC") },
					},
				});
			}

			return builder
				.UseReactiveUI(_ => { })
				.RegisterReactiveUIViewsFromEntryAssembly();
		}
	}
}
