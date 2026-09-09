// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.IO.Compression;
using System.Runtime.CompilerServices;
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
		/// One-string backup calls are the automatic "list changed" path in the existing VM,
		/// except SaveScanResults which is the user's explicit exit-save. Keep the explicit save
		/// synchronous/durable, while automatic calls become coalesced background work.
		///
		/// This overload is intentionally more specific than the legacy optional-argument export
		/// method, so existing call sites do not need to be duplicated across the large main VM.
		/// </summary>
		Task ExportScanResults(string path, [CallerMemberName] string caller = "") {
			StringComparison comparison = CoreUtils.IsWindows
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			if (!string.Equals(path, BackupScanResultsFile, comparison))
				return ExportScanResults(path, includeThumbnails: true, thumbMaxEdge: 160, envelopeTypeInfo: null);

			// Closing/updating explicitly asked to save the current result state. Never turn that
			// contract into fire-and-forget: the process must not exit before this export completes.
			if (caller == nameof(SaveScanResults))
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
					// Short debounce absorbs a delete/mark burst before starting a 1+ GB write.
					await Task.Delay(1500).ConfigureAwait(false);

					lock (automaticScanResultsBackupLock) {
						if (!automaticScanResultsBackupRequested) {
							automaticScanResultsBackupWorkerRunning = false;
							return;
						}
						automaticScanResultsBackupRequested = false;
					}

					await WriteAutomaticScanResultsBackupAsync().ConfigureAwait(false);

					lock (automaticScanResultsBackupLock) {
						if (!automaticScanResultsBackupRequested) {
							automaticScanResultsBackupWorkerRunning = false;
							return;
						}
						// A request arrived while the export was running. Loop once more; any
						// number of requests during the next write collapse into one newest copy.
					}
				}
			}
			catch (Exception ex) {
				lock (automaticScanResultsBackupLock)
					automaticScanResultsBackupWorkerRunning = false;
				Logger.Instance.Warn($"Automatic scan-results backup failed; the primary database/journal remains intact: {ex.Message}");
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
