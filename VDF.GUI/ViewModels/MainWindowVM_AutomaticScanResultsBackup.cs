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
		readonly object automaticScanResultsBackupLock = new();
		bool automaticScanResultsBackupRequested;
		bool automaticScanResultsBackupWorkerRunning;

		/// <summary>
		/// One-string calls are the automatic backup.scanresults path. Queue/coalesce the work
		/// and return immediately so a 1+ GB recovery bundle never extends a delete/mark action.
		/// Explicit exit/update saves call the overload with includeThumbnails:true directly and
		/// therefore retain the original synchronous saving overlay + error reporting contract.
		/// </summary>
		Task ExportScanResults(string path) {
			StringComparison comparison = CoreUtils.IsWindows
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			if (!string.Equals(path, BackupScanResultsFile, comparison))
				return ExportScanResults(path, includeThumbnails: true, thumbMaxEdge: 160, envelopeTypeInfo: null);

			QueueAutomaticScanResultsBackup();
			return Task.CompletedTask;
		}

		void QueueAutomaticScanResultsBackup() {
			lock (automaticScanResultsBackupLock) {
				automaticScanResultsBackupRequested = true;
				if (automaticScanResultsBackupWorkerRunning)
					return;
				automaticScanResultsBackupWorkerRunning = true;
			}
			_ = RunAutomaticScanResultsBackupWorkerAsync();
		}

		async Task RunAutomaticScanResultsBackupWorkerAsync() {
			try {
				while (true) {
					// Absorb a burst of list mutations before starting a 1+ GB write. Requests
					// arriving during the write collapse into exactly one follow-up snapshot.
					await Task.Delay(750).ConfigureAwait(false);

					lock (automaticScanResultsBackupLock) {
						if (!automaticScanResultsBackupRequested) {
							automaticScanResultsBackupWorkerRunning = false;
							return;
						}
						automaticScanResultsBackupRequested = false;
					}

					try {
						await WriteAutomaticScanResultsBackupAsync().ConfigureAwait(false);
					}
					catch (Exception ex) {
						// This is the secondary crash-recovery bundle. Primary DB changes are already
						// durable through ScannedFiles.db or the write-through interactive journal.
						Logger.Instance.Warn($"Automatic scan-results backup failed; the primary database/journal remains intact: {ex.Message}");
					}

					lock (automaticScanResultsBackupLock) {
						if (!automaticScanResultsBackupRequested) {
							automaticScanResultsBackupWorkerRunning = false;
							return;
						}
					}
			}
			catch (Exception ex) {
				lock (automaticScanResultsBackupLock)
					automaticScanResultsBackupWorkerRunning = false;
				Logger.Instance.Warn($"Automatic scan-results backup worker stopped unexpectedly: {ex.Message}");
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
