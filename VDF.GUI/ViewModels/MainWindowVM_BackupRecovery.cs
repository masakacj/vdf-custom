// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core.Utils;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		/// <summary>
		/// Reconciles the automatic startup backup with the already-loaded database. The DB is
		/// the durable source of truth after interactive journal replay; backup.scanresults may
		/// lag a delete/rename by a fraction of a second because automatic backups are coalesced.
		/// Manual ZIP import does not call this helper.
		/// </summary>
		internal static int ReconcileStartupBackupItems(
			List<DuplicateItemVM> items,
			IReadOnlySet<string> databasePaths,
			IReadOnlyList<(string OldPath, string NewPath)> pendingMoves) {
			StringComparer comparer = PathComparer.ForCurrentPlatform;
			var moveMap = new Dictionary<string, string>(comparer);
			foreach (var move in pendingMoves) {
				if (string.IsNullOrWhiteSpace(move.OldPath) || string.IsNullOrWhiteSpace(move.NewPath))
					continue;
				moveMap[move.OldPath] = move.NewPath;
			}

			foreach (DuplicateItemVM item in items) {
				string original = item.ItemInfo.Path;
				string resolved = original;
				var seen = new HashSet<string>(comparer);
				while (moveMap.TryGetValue(resolved, out string? next) && seen.Add(resolved))
					resolved = next;

				if (!comparer.Equals(original, resolved)) {
					item.ItemInfo.Path = resolved;
					item.ItemInfo.Folder = Path.GetDirectoryName(resolved) ?? string.Empty;
				}
			}

			int removed = items.RemoveAll(item => !databasePaths.Contains(item.ItemInfo.Path));
			if (items.Count == 0)
				return removed;

			var groupCounts = new Dictionary<Guid, int>();
			foreach (DuplicateItemVM item in items) {
				Guid groupId = item.ItemInfo.GroupId;
				groupCounts.TryGetValue(groupId, out int count);
				groupCounts[groupId] = count + 1;
			}

			removed += items.RemoveAll(item => groupCounts[item.ItemInfo.GroupId] < 2);
			return removed;
		}
	}
}
