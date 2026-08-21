// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Diagnostics;
using System.Reactive;
using System.Threading;
using Avalonia.Threading;
using ReactiveUI;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Utils;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
		/// <summary>Manual GitHub Release updater for the Windows x64 custom build.</summary>
		public ReactiveCommand<Unit, Unit> CheckForUpdatesCommand => ReactiveCommand.CreateFromTask(async () => {
			if (!OperatingSystem.IsWindows()) {
				await MessageBoxService.Show("当前在线更新仅支持 Windows x64 版本。", title: "在线更新");
				return;
			}
			if (IsScanning) {
				await MessageBoxService.Show("扫描进行中，完成或停止扫描后再更新。", title: "在线更新");
				return;
			}
			if (IsBusy) return;
			if (!CoreUtils.IsCurrentFolderWritable) {
				await MessageBoxService.Show(
					$"当前程序目录没有写权限，无法自动覆盖更新：\n{CoreUtils.CurrentFolder}\n\n请把 VDF 放到普通可写目录后再使用在线更新。",
					title: "在线更新");
				return;
			}

			PreparedGitHubUpdate? prepared = null;
			CancellationToken token = BeginCancelableBusyOperation();
			IsBusy = true;
			try {
				IsBusyOverlayText = $"正在检查 GitHub 更新…  当前 v{GitHubUpdateService.FormatVersion(GitHubUpdateService.CurrentVersion)}";
				GitHubReleaseUpdate? release = await GitHubUpdateService.CheckLatestAsync(token);
				if (release == null) {
					IsBusy = false;
					await MessageBoxService.Show(
						$"当前已经是最新版本 v{GitHubUpdateService.FormatVersion(GitHubUpdateService.CurrentVersion)}。",
						title: "在线更新");
					return;
				}

				IsBusy = false;
				MessageBoxButtons? confirm = await MessageBoxService.Show(
					$"发现新版本 v{GitHubUpdateService.FormatVersion(release.Version)}。\n\n" +
					$"将从 GitHub 下载 {release.AssetName}（约 {DownloadUtils.FormatBytes(release.AssetSize)}），" +
					"校验后自动退出、覆盖程序文件并重新启动。\n\n" +
					"Settings.json、扫描数据库、日志和扫描结果不会被删除。\n\n现在更新吗？",
					MessageBoxButtons.Yes | MessageBoxButtons.No,
					title: "在线更新",
					defaultButton: MessageBoxButtons.Yes);
				if (confirm != MessageBoxButtons.Yes) return;

				// Preserve the same close semantics as a normal user exit before downloading.
				// Cancel means "do not update"; Yes/No on the save prompt both allow update.
				if (!await SaveScanResults()) return;

				IsBusy = true;
				IsBusyOverlayText = $"正在下载 v{GitHubUpdateService.FormatVersion(release.Version)}…";
				prepared = await GitHubUpdateService.DownloadAndPrepareAsync(release, (done, total) => {
					double pct = total is > 0 ? (double)done / total.Value * 100d : 0d;
					string progressText = total is > 0
						? $"正在下载 v{GitHubUpdateService.FormatVersion(release.Version)}… {pct:0}%  {DownloadUtils.FormatBytes(done)} / {DownloadUtils.FormatBytes(total)}"
						: $"正在下载 v{GitHubUpdateService.FormatVersion(release.Version)}… {DownloadUtils.FormatBytes(done)}";
					Dispatcher.UIThread.Post(() => IsBusyOverlayText = progressText);
				}, token);

				IsBusyOverlayText = "更新包校验完成，正在启动更新程序…";
				GitHubUpdateService.LaunchInstaller(prepared, CoreUtils.CurrentFolder, Environment.ProcessId);
				prepared = null; // installer owns/cleans the staging folder from here on.
				if (ApplicationHelpers.MainWindow is MainWindow window)
					window.ShutdownForUpdate();
				else
					ApplicationHelpers.CurrentApplicationLifetime.Shutdown();
			}
			catch (OperationCanceledException) {
				// User clicked the busy overlay's Cancel button.
			}
			catch (Exception ex) {
				Logger.Instance.Error($"Online update failed: {ex}");
				IsBusy = false;
				await MessageBoxService.Show($"在线更新失败：\n{ex.Message}", title: "在线更新");
			}
			finally {
				if (prepared != null)
					GitHubUpdateService.DeleteDirectoryBestEffort(prepared.TempRoot);
				IsBusy = false;
				EndCancelableBusyOperation();
			}
		});
	}
}
