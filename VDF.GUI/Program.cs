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
			// The downloaded updater executable must run before Avalonia/System.CommandLine:
			// it waits for the old GUI to exit, overlays the new release and restarts it.
			if (SelfUpdateInstaller.TryHandle(args) is int updateExitCode)
				return updateExitCode;

			Option<FileInfo> settingsOption = new("--settings", new[] { "-s" }) {
				Description = "Path to a settings file to load and save."
			};
			Option<bool> softwareRenderingOption = new("--software-rendering") {
				Description = "Force software rendering (for graphics-related startup crashes)."
			};
			Option<bool> hardwareRenderingOption = new("--hardware-rendering") {
				Description = "Temporarily bypass the saved software-rendering recovery mode."
			};
			RootCommand rootCommand = new("VideoDuplicateFinder settings options");
			rootCommand.Options.Add(settingsOption);
			rootCommand.Options.Add(softwareRenderingOption);
			rootCommand.Options.Add(hardwareRenderingOption);

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

				// A second Windows launch must not start another 3+ GB database load or race
				// state writes. It signals the already-running GUI to show/activate and exits.
				using var singleInstance = SingleInstanceCoordinator.TryAcquirePrimary();
				if (singleInstance == null)
					return;

				try {
					bool forceSoftware = parseResult.GetValue(softwareRenderingOption);
					bool forceHardware = parseResult.GetValue(hardwareRenderingOption);
					if (forceSoftware && forceHardware)
						throw new ArgumentException("Only one of --software-rendering and --hardware-rendering may be selected.");
					bool useSoftware = WindowsStartupRenderRecovery.SelectRenderingMode(forceSoftware, forceHardware, out string reason);
					App.StartupRenderingMode = reason;
					BuildAvaloniaApp(useSoftware).StartWithClassicDesktopLifetime(args);
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

		internal static readonly string[] WindowsCjkFallbackFamilies = [
			"Microsoft YaHei UI",
			"Microsoft YaHei",
			"DengXian",
			"SimSun",
			"Segoe UI Symbol",
			"Segoe UI Emoji",
		];

		internal static readonly string[] MacCjkFallbackFamilies = [
			"PingFang SC",
			"Hiragino Sans GB",
			"Hiragino Sans",
			"Noto Sans CJK SC",
		];

		public static AppBuilder BuildAvaloniaApp(bool softwareRendering = false) {
			App.SafeWindowsFontsEnabled = OperatingSystem.IsWindows() && softwareRendering;
			var builder = AppBuilder.Configure<App>()
				.UsePlatformDetect()
				.With(new X11PlatformOptions { UseDBusFilePicker = false });

			// Avalonia/Skia does not always follow DirectWrite's normal Win32 fallback path,
			// especially when a control explicitly requests a Latin-only monospace face such
			// as Consolas/Cascadia Mono. Chinese paths/status text could therefore render as
			// tofu squares even though Windows has CJK fonts installed. Keep the fallback
			// chains platform-specific so Windows never probes macOS-only faces (an older
			// cross-platform chain was associated with machine-specific startup failures).
			if (OperatingSystem.IsWindows()) {
				// Native SkiaSharp/ANGLE crashes on some Win10 remote/virtual display setups.
				// Only force the conservative redirection + CPU rendering path after a
				// pre-window startup crash, or when the user explicitly requests it.
				// Do not slow down unaffected machines by default.
				if (softwareRendering)
					builder = builder.With(new Win32PlatformOptions {
						RenderingMode = [Win32RenderingMode.Software],
						CompositionMode = [Win32CompositionMode.RedirectionSurface],
					});
				// The crash at libSkiaSharp+0x2a0934 is a null DirectWrite
				// font-file stream in SkDWriteFontFileStream::read, not a GPU
				// rendering failure. CPU rendering alone does NOT avoid it.
				// In recovery mode source fonts from local file-backed memory
				// streams instead of Skia's DirectWrite font handles.
				if (softwareRendering)
					builder = builder.With(WindowsSafeFonts.CreateOptions());
				else
					builder = builder.With(new FontManagerOptions {
						FontFallbacks = WindowsCjkFallbackFamilies
							.Select(name => new FontFallback { FontFamily = new FontFamily(name) })
							.ToArray(),
					});
			}
			else if (OperatingSystem.IsMacOS()) {
				builder = builder.With(new FontManagerOptions {
					FontFallbacks = MacCjkFallbackFamilies
						.Select(name => new FontFallback { FontFamily = new FontFamily(name) })
						.ToArray(),
				});
			}

			return builder
				.UseReactiveUI(_ => { })
				.RegisterReactiveUIViewsFromEntryAssembly();
		}
	}
}
