// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Text;
using VDF.Core.Utils;

namespace VDF.Core {
	/// <summary>
	/// Durable sidecar for tiny interactive database mutations (rename/move/delete/consolidate).
	///
	/// ScannedFiles.db deliberately remains unchanged: interactive actions append a few bytes
	/// here instead of rewriting a multi-GB database after every click. On startup the journal
	/// is replayed on top of the loaded base database. Entries are idempotent, so a later normal
	/// full database checkpoint can absorb them without requiring a schema migration.
	/// </summary>
	public sealed partial class ScanEngine {
		static readonly object interactiveDatabaseJournalLock = new();
		const string InteractiveDatabaseJournalFileName = "ScannedFiles.interactive.log";

		static string InteractiveDatabaseJournalPath =>
			Path.Combine(DatabaseUtils.GetDatabaseFolderPath(), InteractiveDatabaseJournalFileName);

		public static bool PersistInteractiveDatabaseMove(string oldPath, string newPath, out string error) =>
			PersistInteractiveDatabaseMutations(new[] { (OldPath: oldPath, NewPath: newPath) }, Array.Empty<string>(), out error);

		public static bool PersistInteractiveDatabaseMoves(
			IEnumerable<(string OldPath, string NewPath)> moves,
			out string error) =>
			PersistInteractiveDatabaseMutations(moves, Array.Empty<string>(), out error);

		public static bool PersistInteractiveDatabaseDeletes(IEnumerable<string> deletedPaths, out string error) =>
			PersistInteractiveDatabaseMutations(Array.Empty<(string OldPath, string NewPath)>(), deletedPaths, out error);

		public static bool PersistInteractiveDatabaseMutations(
			IEnumerable<(string OldPath, string NewPath)> moves,
			IEnumerable<string> deletedPaths,
			out string error) {
			try {
				var lines = new List<string>();
				foreach (var move in moves) {
					if (string.IsNullOrWhiteSpace(move.OldPath) || string.IsNullOrWhiteSpace(move.NewPath))
						continue;
					string oldPath = NormalizeInteractivePath(move.OldPath);
					string newPath = NormalizeInteractivePath(move.NewPath);
					if (InteractivePathComparer.Equals(oldPath, newPath))
						continue;
					lines.Add($"M\t{EncodeInteractivePath(oldPath)}\t{EncodeInteractivePath(newPath)}");
				}
				foreach (string path in deletedPaths) {
					if (string.IsNullOrWhiteSpace(path)) continue;
					lines.Add($"D\t{EncodeInteractivePath(NormalizeInteractivePath(path))}");
				}
				if (lines.Count == 0) {
					error = string.Empty;
					return true;
				}

				lock (interactiveDatabaseJournalLock) {
					string journal = InteractiveDatabaseJournalPath;
					Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
					using var stream = new FileStream(
						journal, FileMode.Append, FileAccess.Write, FileShare.Read,
						64 * 1024, FileOptions.WriteThrough);
					foreach (string line in lines) {
						byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
						stream.Write(bytes);
					}
					stream.Flush(flushToDisk: true);
				}
				error = string.Empty;
				return true;
			}
			catch (Exception ex) {
				error = ex.Message;
				Logger.Instance.Error($"Interactive database journal append failed: {ex}");
				return false;
			}
		}

		/// <summary>
		/// Replays the durable sidecar after ScannedFiles.db is loaded. Records that are already
		/// reflected in the base database are dropped; records still required relative to the base
		/// are retained so another restart remains safe until a normal full checkpoint absorbs them.
		/// </summary>
		public static int ReplayInteractiveDatabaseJournal() {
			lock (interactiveDatabaseJournalLock) {
				string journal = InteractiveDatabaseJournalPath;
				if (!File.Exists(journal)) return 0;

				string[] lines;
				try { lines = File.ReadAllLines(journal, Encoding.UTF8); }
				catch (Exception ex) {
					Logger.Instance.Warn($"Interactive database journal could not be read: {ex.Message}");
					return 0;
				}

				var required = new List<string>(lines.Length);
				int applied = 0;
				lock (DatabaseUtils.Database) {
					foreach (string raw in lines) {
						if (!TryParseInteractiveJournalLine(raw, out char op, out string a, out string? b))
							continue; // including a possible torn final line

						if (op == 'M') {
							bool oldPresent = TryGetInteractiveDatabaseEntry(a, out FileEntry? entry);
							bool newPresent = TryGetInteractiveDatabaseEntry(b!, out _);
							if (oldPresent && entry != null) {
								DatabaseUtils.Database.Remove(entry);
								entry.Path = b!;
								DatabaseUtils.Database.Add(entry);
								required.Add(raw);
								applied++;
							}
							else if (!newPresent) {
								// Neither side exists in the base DB (typically a later delete already
								// checkpointed). The move no longer needs replaying.
							}
							// old absent + new present => already checkpointed; drop the record.
						}
						else if (op == 'D') {
							if (TryGetInteractiveDatabaseEntry(a, out FileEntry? entry) && entry != null) {
								DatabaseUtils.Database.Remove(entry);
								required.Add(raw);
								applied++;
							}
							// Missing already means the base checkpoint absorbed the delete.
						}
					}
				}

				try {
					if (required.Count == 0)
						File.Delete(journal);
					else
						File.WriteAllLines(journal, required, Encoding.UTF8);
				}
				catch (Exception ex) {
					Logger.Instance.Warn($"Interactive database journal compaction failed: {ex.Message}");
				}

				if (applied > 0)
					Logger.Instance.Info($"Replayed {applied:N0} interactive database mutation(s) without rewriting ScannedFiles.db.");
				return applied;
			}
		}

		static bool TryGetInteractiveDatabaseEntry(string path, out FileEntry? entry) =>
			DatabaseUtils.Database.TryGetValue(new FileEntry(path), out entry);

		static readonly StringComparer InteractivePathComparer =
			CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

		static string NormalizeInteractivePath(string path) => Path.GetFullPath(path);
		static string EncodeInteractivePath(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
		static string DecodeInteractivePath(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

		internal static bool TryParseInteractiveJournalLine(string raw, out char op, out string a, out string? b) {
			op = '\0'; a = string.Empty; b = null;
			if (string.IsNullOrWhiteSpace(raw)) return false;
			string[] parts = raw.Split('\t');
			try {
				if (parts.Length == 2 && parts[0] == "D") {
					op = 'D';
					a = DecodeInteractivePath(parts[1]);
					return true;
				}
				if (parts.Length == 3 && parts[0] == "M") {
					op = 'M';
					a = DecodeInteractivePath(parts[1]);
					b = DecodeInteractivePath(parts[2]);
					return true;
				}
			}
			catch { }
			return false;
		}
	}
}
