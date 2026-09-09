// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Text;
using VDF.Core.Utils;

namespace VDF.Core {
	public sealed partial class ScanEngine {
		/// <summary>
		/// Snapshot of the currently loaded database identities. This deliberately compares
		/// paths only and never stats the filesystem: offline-drive entries and tombstones are
		/// still legitimate database members during startup recovery.
		/// </summary>
		public static HashSet<string> GetLoadedDatabasePathsSnapshot() {
			lock (DatabaseUtils.Database) {
				return DatabaseUtils.Database
					.Select(entry => entry.Path)
					.ToHashSet(InteractivePathComparer);
			}
		}

		/// <summary>
		/// Returns move records that still need the interactive journal for durability. Startup
		/// backup recovery uses these to translate a slightly stale backup.scanresults path to
		/// its post-rename/post-move path before checking it against the loaded database.
		/// </summary>
		public static IReadOnlyList<(string OldPath, string NewPath)> GetPendingInteractiveDatabaseMovesSnapshot() {
			lock (interactiveDatabaseJournalLock) {
				string journal = InteractiveDatabaseJournalPath;
				if (!File.Exists(journal))
					return Array.Empty<(string OldPath, string NewPath)>();

				try {
					var moves = new List<(string OldPath, string NewPath)>();
					foreach (string raw in File.ReadLines(journal, Encoding.UTF8)) {
						if (TryParseInteractiveJournalLine(raw, out char op, out string oldPath, out string? newPath) &&
							op == 'M' && newPath != null)
							moves.Add((oldPath, newPath));
					}
					return moves;
				}
				catch (Exception ex) {
					Logger.Instance.Warn($"Interactive database move snapshot could not be read: {ex.Message}");
					return Array.Empty<(string OldPath, string NewPath)>();
				}
			}
		}
	}
}
