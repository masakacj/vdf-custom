// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.IO.Compression;
using System.Text.Json;
using Avalonia.Threading;
using VDF.Core.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		sealed record AutomaticBackupWaiter(long Version, TaskCompletionSource<bool> Completion);

		readonly object automaticScanResultsBackupLock = new();
		readonly List<AutomaticBackupWaiter> automaticScanResultsBackupWaiters = new();
		long automaticScanResultsBackupRequestedVersion;
		long automaticScanResultsBackupCompletedVersion;
		bool automaticScanResultsBackupWorkerRunning;

		/// <summary>
		/// Existing one-string calls all target backup.scanresults. Make that path a coalesced,
		/// non-blocking-background export while still returning a Task for callers that explicitly
		/// await durability (notably SaveScanResults during shutdown). The expensive ZIP/pack write
		/// never owns the busy overlay, so ordinary delete/mark interactions remain usable.
		/// </summary>
		Task ExportScanResults(string path) {
			StringComparison comparison = CoreUtils.IsWindows
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			if (!string.Equals(path, BackupScanResultsFile, comparison))
				return ExportScanResults(path, includeThumbnails: true, thumbMaxEdge: 160, envelopeTypeInfo: null);

			return RequestAutomaticScanResultsBackupAsync();
		}

		Task RequestAutomaticScanResultsBackupAsync() {
			TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
			lock (automaticScanResultsBackupLock) {
				long version = ++automaticScanResultsBackupRequestedVersion;
				automaticScanResultsBackupWaiters.Add(new AutomaticBackupWaiter(version, completion));
				if (!automaticScanResultsBackupWorkerRunning) {
					automaticScanResultsBackupWorkerRunning = true;
					_ = RunAutomaticScanResultsBackupWorkerAsync();
				}
			}
			return completion.Task;
		}

		async Task RunAutomaticScanResultsBackupWorkerAsync() {
			try {
				while (true) {
					// Absorb a burst of list mutations before starting a 1+ GB write. Requests
					// arriving during the write collapse into exactly one follow-up snapshot.
					await Task.Delay(750).ConfigureAwait(false);

					long targetVersion;
					lock (automaticScanResultsBackupLock)
						targetVersion = automaticScanResultsBackupRequestedVersion;

					try {
						await WriteAutomaticScanResultsBackupAsync().ConfigureAwait(false);
					}
					catch (Exception ex) {
						// Preserve the old export contract: a secondary backup failure is reported,
						// but does not tear down the app. The primary database/journal has its own
						// durability path and remains authoritative.
						Logger.Instance.Warn($"Automatic scan-results backup failed; the primary database/journal remains intact: {ex.Message}");
					}

					List<TaskCompletionSource<bool>> completed = new();
					bool done;
					lock (automaticScanResultsBackupLock) {
						automaticScanResultsBackupCompletedVersion = Math.Max(
							automaticScanResultsBackupCompletedVersion, targetVersion);
						for (int i = automaticScanResultsBackupWaiters.Count - 1; i >= 0; i--) {
							if (automaticScanResultsBackupWaiters[i].Version > automaticScanResultsBackupCompletedVersion)
								continue;
							completed.Add(automaticScanResultsBackupWaiters[i].Completion);
							automaticScanResultsBackupWaiters.RemoveAt(i);
						}
						done = automaticScanResultsBackupRequestedVersion <= automaticScanResultsBackupCompletedVersion;
						if (done)
							automaticScanResultsBackupWorkerRunning = false;
					}
					foreach (TaskCompletionSource<bool> waiter in completed)
						waiter.TrySetResult(true);

					if (done)
						return;
				}
			}
			catch (Exception ex) {
				List<TaskCompletionSource<bool>> waiters;
				lock (automaticScanResultsBackupLock) {
					automaticScanResultsBackupWorkerRunning = false;
					waiters = automaticScanResultsBackupWaiters.Select(w => w.Completion).ToList();
					automaticScanResultsBackupWaiters.Clear();
				}
				Logger.Instance.Warn($"Automatic scan-results backup worker stopped unexpectedly: {ex.Message}");
				foreach (TaskCompletionSource<bool> waiter in waiters)
					waiter.TrySetResult(false);
			}
		}

		async Task WriteAutomaticScanResultsBackupAsync() {
			// AvaloniaList is UI-owned. Copy the references on its dispatcher, then perform the
			// expensive JSON/ZIP/thumbnail-pack write entirely on a worker. This backup is a
			// secondary recovery bundle; the primary ScannedFiles.db + interactive journal is
			// persisted independently and never depends on this task completing.
			List<DuplicateItemVM> snapshot = await Dispatcher.UIThread.InvokeAsync(() => Duplicates.ToList());
			var envelope = new ScanResultsEnvelope {
				Version = ScanResultsEnvelope.CurrentVersion,
				Items = snapshot,
			};
			var pack = Utils.ThumbCacheHelpers.Provider;
			string path = BackupScanResultsFile;
			string dir = Path.GetDirectoryName(path)!;
			string tmp = Path.Combine(dir, Path.GetFileName(path) + ".auto.tmp");

			await scanResultsExportGate.WaitAsync().ConfigureAwait(false);
			try {
				await Task.Run(() => {
					Directory.CreateDirectory(dir);
					using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
					using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false)) {
						var jsonEntry = zip.CreateEntry("scan.json", CompressionLevel.NoCompression);
						using (var es = jsonEntry.Open())
							JsonSerializer.Serialize(es, envelope, GuiJsonFieldsContext.Default.ScanResultsEnvelope);

						if (pack != null) {
							var (packLength, indexJson) = pack.SnapshotForExport();
							var packEntry = zip.CreateEntry("thumbs.pack", CompressionLevel.NoCompression);
							using (var es = packEntry.Open())
								pack.CopyPackTo(es, packLength);
							var idxEntry = zip.CreateEntry("thumbs.idx", CompressionLevel.NoCompression);
							using (var es = idxEntry.Open())
								es.Write(indexJson, 0, indexJson.Length);
						}
					}
					File.Move(tmp, path, overwrite: true);
				}).ConfigureAwait(false);
			}
			finally {
				try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
				scanResultsExportGate.Release();
			}
		}
	}
}
