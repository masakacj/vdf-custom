// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the GNU Affero General Public License as published by
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

using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI {
	public class App : Application {
		public static LanguageService Lang { get; } = new();
		static readonly object startupTraceLock = new();

		static void TraceStartup(string phase, bool reset = false) {
			try {
				string folder = VDF.Core.Utils.CoreUtils.SettingsFolder;
				Directory.CreateDirectory(folder);
				string path = Path.Combine(folder, "startup-trace.txt");
				string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {phase}{Environment.NewLine}";
				lock (startupTraceLock) {
					if (reset)
						File.WriteAllText(path, line);
					else
						File.AppendAllText(path, line);
				}
			}
			catch { /* diagnostics must never interfere with startup */ }
		}

		static string GetDatabaseLoadingText(TimeSpan elapsed) {
			try {
				string folder = VDF.Core.Utils.CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder);
				string path = Path.Combine(folder, "ScannedFiles.db");
				if (File.Exists(path)) {
					double gib = new FileInfo(path).Length / 1024d / 1024d / 1024d;
					return $"正在加载扫描数据库 / Loading database… {gib:F2} GB · {elapsed:hh\\:mm\\:ss}";
				}
			}
			catch { }
			return $"正在加载扫描数据库 / Loading database… {elapsed:hh\\:mm\\:ss}";
		}

		static void StartDatabaseLoadingStatus(MainWindowVM viewModel) {
			var elapsed = Stopwatch.StartNew();
			var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
			timer.Tick += (_, _) => {
				if (!viewModel.IsBusy) {
					timer.Stop();
					return;
				}
				if (viewModel.IsBusyOverlayText.StartsWith("Loading database", StringComparison.OrdinalIgnoreCase) ||
					viewModel.IsBusyOverlayText.StartsWith("正在加载扫描数据库", StringComparison.Ordinal))
					viewModel.IsBusyOverlayText = GetDatabaseLoadingText(elapsed.Elapsed);
				else
					timer.Stop();
			};
			timer.Start();
		}

		public override void Initialize() {
			TraceStartup("App.Initialize begin", reset: true);
			// Product default is Simplified Chinese. A user-selected language can still
			// replace CurrentLanguage later through Settings; fresh installs start here.
			Lang.LoadLanguage(SettingsFile.DefaultLanguageCode);
			TraceStartup("Default language loaded");
			AvaloniaXamlLoader.Load(this);
			TraceStartup("App XAML loaded");
		}

		public override void OnFrameworkInitializationCompleted() {
			TraceStartup("Framework initialization begin");
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
				// Crash logging must be wired BEFORE the window is constructed: an exception
				// escaping the MainWindow/MainWindowVM constructors used to terminate the
				// process without leaving any trace in log.txt (#830).
				AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
				Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
				TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
				TraceStartup("Crash handlers installed");

				TraceStartup("Constructing MainWindow");
				var window = new MainWindow();
				TraceStartup("MainWindow constructed");
				var viewModel = new MainWindowVM();
				TraceStartup("MainWindowVM constructed");

				// Interactive file operations may be journaled instead of forcing a multi-GB
				// ScannedFiles.db checkpoint. Replay that tiny journal at the exact boundary where
				// LoadDatabaseAsync has finished loading/migrating the base DB but before its async
				// continuation can start restoring backup.scanresults. PropertyChanged is synchronous,
				// so this cannot race result restoration.
				bool sawStartupDatabaseBusy = false;
				System.ComponentModel.PropertyChangedEventHandler? replayHandler = null;
				replayHandler = (_, e) => {
					if (e.PropertyName != nameof(MainWindowVM.IsBusy)) return;
					if (viewModel.IsBusy) {
						sawStartupDatabaseBusy = true;
						return;
					}
					if (!sawStartupDatabaseBusy) return;
					viewModel.PropertyChanged -= replayHandler;
					try {
						int replayed = VDF.Core.ScanEngine.ReplayInteractiveDatabaseJournal();
						TraceStartup($"Interactive DB journal replay completed ({replayed:N0} mutation(s))");
					}
					catch (Exception ex) {
						TraceStartup($"Interactive DB journal replay failed: {ex.GetType().Name}: {ex.Message}");
						VDF.Core.Utils.Logger.Instance.Error($"Interactive DB journal replay failed: {ex}");
					}
				};
				viewModel.PropertyChanged += replayHandler;

				window.DataContext = viewModel;
				desktop.MainWindow = window;
				TraceStartup("MainWindow assigned to desktop lifetime");
				window.Opened += (_, _) => TraceStartup("MainWindow Opened");

				// ClassicDesktopLifetime raises Startup before its normal automatic MainWindow
				// show. A multi-GB ScannedFiles.db load therefore used to leave a perfectly alive
				// process with no visible window for many minutes. Show the shell synchronously
				// before base.OnFrameworkInitializationCompleted() can enter that Startup path.
				try {
					viewModel.IsBusy = true;
					viewModel.IsBusyOverlayText = GetDatabaseLoadingText(TimeSpan.Zero);
					window.Show();
					StartDatabaseLoadingStatus(viewModel);
					TraceStartup("MainWindow shown synchronously before Startup database load");
				}
				catch (Exception ex) {
					TraceStartup($"Synchronous MainWindow show failed: {ex.GetType().Name}: {ex.Message}");
				}

				desktop.ShutdownRequested += OnShutdownRequested;
				desktop.Exit += OnExitCleanup; //fallback
				AppDomain.CurrentDomain.ProcessExit += (_, __) => SafeCleanup();
			}

			base.OnFrameworkInitializationCompleted();
			TraceStartup("Framework initialization completed");
		}
		void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e) => SafeCleanup();
		void OnExitCleanup(object? sender, ControlledApplicationLifetimeExitEventArgs e) => SafeCleanup();
		static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) {
			try {
				string detail = e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "<null>";
				TraceStartup($"FATAL unhandled exception (terminating={e.IsTerminating}): {detail}");
				VDF.Core.Utils.Logger.Instance.Error($"FATAL: Unhandled exception (terminating={e.IsTerminating}): {detail}");
			}
			catch { /* never let logging failure mask the original crash */ }
			SafeCleanup();
		}
		static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e) {
			try {
				TraceStartup($"Dispatcher exception: {e.Exception}");
				VDF.Core.Utils.Logger.Instance.Error($"Unhandled dispatcher exception: {e.Exception}");
			}
			catch { /* never let logging failure mask the original error */ }
			// Keep the app alive so the user can save state and the log contains a stack trace.
			e.Handled = true;
		}
		static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) {
			try {
				TraceStartup($"Unobserved task exception: {e.Exception}");
				VDF.Core.Utils.Logger.Instance.Error($"Unobserved task exception: {e.Exception}");
			}
			catch { /* never let logging failure mask the original error */ }
			e.SetObserved();
		}
		static void SafeCleanup() {
			try {
				try { VDF.GUI.Utils.ThumbCacheHelpers.Provider?.Dispose(); }
				catch { /* ignore */ }
				VDF.GUI.Utils.ThumbCacheHelpers.Provider = null;

				try { TempExtractionManager.DisposeAll(); }
				catch { /* ignore */ }

				// This exit is not a native crash — files a scan still had in flight are
				// innocent and must not be quarantined at the next scan (#861).
				try { VDF.Core.Utils.ScanCrashJournal.ClearOnCleanShutdown(); }
				catch { /* ignore */ }
			}
			catch { /* last resort */ }
		}
	}
}
