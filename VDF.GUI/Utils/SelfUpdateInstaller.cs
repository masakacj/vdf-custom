// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Diagnostics;
using System.Threading;

namespace VDF.GUI.Utils {
	/// <summary>
	/// Early-process updater mode. A freshly downloaded VDF.GUI.exe runs this before
	/// Avalonia starts, waits for the old process to exit, overlays release files onto the
	/// existing install folder, then launches the installed copy. User-created files are not
	/// deleted because the copy is additive/overwrite-only, never a mirror/purge.
	/// </summary>
	internal static class SelfUpdateInstaller {
		internal const string ApplyUpdateSwitch = "--apply-update";

		internal static int? TryHandle(string[] args) {
			if (args.Length == 0 || !args[0].Equals(ApplyUpdateSwitch, StringComparison.OrdinalIgnoreCase))
				return null;
			if (!OperatingSystem.IsWindows()) return 20;
			if (args.Length != 5 || !int.TryParse(args[1], out int oldPid) || oldPid <= 0)
				return 21;
			string sourceRoot = args[2];
			string targetRoot = args[3];
			string tempRoot = args[4];
			try {
				WaitForProcessExit(oldPid, TimeSpan.FromMinutes(2));
				CopyPayloadWithRetry(sourceRoot, targetRoot, TimeSpan.FromSeconds(45));
				string installedExe = Path.Combine(targetRoot, "VDF.GUI.exe");
				if (!File.Exists(installedExe)) throw new FileNotFoundException("更新后未找到 VDF.GUI.exe。", installedExe);
				Process.Start(new ProcessStartInfo {
					FileName = installedExe,
					WorkingDirectory = targetRoot,
					UseShellExecute = true,
				});
				ScheduleCleanup(tempRoot);
				return 0;
			}
			catch (Exception ex) {
				try {
					Directory.CreateDirectory(tempRoot);
					File.WriteAllText(Path.Combine(tempRoot, "update-error.txt"), ex.ToString());
				}
				catch { }
				// Re-open the old/current installation if enough of it still exists, so a
				// transient updater failure does not strand the user at the desktop.
				try {
					string installedExe = Path.Combine(targetRoot, "VDF.GUI.exe");
					if (File.Exists(installedExe))
						Process.Start(new ProcessStartInfo { FileName = installedExe, WorkingDirectory = targetRoot, UseShellExecute = true });
				}
				catch { }
				return 22;
			}
		}

		internal static void WaitForProcessExit(int pid, TimeSpan timeout) {
			try {
				using Process process = Process.GetProcessById(pid);
				if (process.HasExited) return;
				if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
					throw new TimeoutException($"等待旧 VDF 进程 {pid} 退出超时。");
			}
			catch (ArgumentException) {
				// Already gone.
			}
		}

		internal static void CopyPayloadWithRetry(string sourceRoot, string targetRoot, TimeSpan timeout) {
			string source = Path.GetFullPath(sourceRoot);
			string target = Path.GetFullPath(targetRoot);
			if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
			Directory.CreateDirectory(target);
			DateTime deadline = DateTime.UtcNow + timeout;
			foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) {
				string relative = Path.GetRelativePath(source, directory);
				Directory.CreateDirectory(Path.Combine(target, relative));
			}
			foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
				string relative = Path.GetRelativePath(source, file);
				string destination = Path.Combine(target, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				while (true) {
					try {
						File.Copy(file, destination, overwrite: true);
						break;
					}
					catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && DateTime.UtcNow < deadline) {
						Thread.Sleep(500);
					}
				}
			}
		}

		static void ScheduleCleanup(string tempRoot) {
			try {
				var psi = new ProcessStartInfo {
					FileName = "cmd.exe",
					UseShellExecute = false,
					CreateNoWindow = true,
				};
				psi.ArgumentList.Add("/d");
				psi.ArgumentList.Add("/c");
				psi.ArgumentList.Add($"ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{tempRoot}\"");
				Process.Start(psi);
			}
			catch { }
		}
	}
}
