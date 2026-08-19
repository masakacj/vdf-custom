// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Linq;
using VDF.Core.Utils;

namespace VDF.Core {
	public sealed partial class ScanEngine {
		/// <summary>
		/// Validates the database side of a single-resource consolidation before the GUI
		/// touches files. A destination may already be occupied only when that entry is one
		/// of the known duplicate copies that the operation is explicitly replacing/removing.
		/// </summary>
		public static bool ValidateConsolidationDatabaseChange(
			string keeperOriginalPath,
			string destinationPath,
			IEnumerable<string> knownDuplicatePaths,
			out string error) {
			try {
				var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
				string keeperPath = NormalizeConsolidationPath(keeperOriginalPath);
				string destination = NormalizeConsolidationPath(destinationPath);
				var known = new HashSet<string>(knownDuplicatePaths.Select(NormalizeConsolidationPath), comparer);
				known.Add(keeperPath);

				FileEntry[] snapshot = DatabaseUtils.Database.ToArray();
				if (!snapshot.Any(entry => comparer.Equals(NormalizeConsolidationPath(entry.Path), keeperPath))) {
					error = "BEST file is not present in the active VDF database.";
					return false;
				}

				FileEntry? occupant = snapshot.FirstOrDefault(entry =>
					comparer.Equals(NormalizeConsolidationPath(entry.Path), destination));
				if (occupant != null && !known.Contains(NormalizeConsolidationPath(occupant.Path))) {
					error = "The destination is occupied by a VDF database entry outside this duplicate group.";
					return false;
				}

				error = string.Empty;
				return true;
			}
			catch (Exception ex) {
				error = ex.Message;
				return false;
			}
		}

		/// <summary>
		/// Commits the metadata switch after the verified filesystem operation completed.
		/// The BEST entry keeps its fingerprints/media metadata and is moved to the final
		/// destination; only duplicate entries whose physical copies are confirmed gone are
		/// removed. HashSet membership is updated before mutating FileEntry.Path because Path
		/// participates in FileEntry equality/hash semantics.
		/// </summary>
		public static bool CommitConsolidationDatabaseChange(
			string keeperOriginalPath,
			string destinationPath,
			IEnumerable<string> removedDuplicatePaths,
			out string error) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			string keeperPath;
			string destination;
			HashSet<string> removed;
			try {
				keeperPath = NormalizeConsolidationPath(keeperOriginalPath);
				destination = NormalizeConsolidationPath(destinationPath);
				removed = new HashSet<string>(removedDuplicatePaths.Select(NormalizeConsolidationPath), comparer);
				removed.Remove(keeperPath);
			}
			catch (Exception ex) {
				error = ex.Message;
				return false;
			}

			FileEntry? keeper = null;
			var removedEntries = new List<FileEntry>();
			string oldKeeperPath = string.Empty;
			lock (DatabaseUtils.Database) {
				try {
					FileEntry[] snapshot = DatabaseUtils.Database.ToArray();
					keeper = snapshot.FirstOrDefault(entry =>
						comparer.Equals(NormalizeConsolidationPath(entry.Path), keeperPath));
					if (keeper == null) {
						error = "BEST file disappeared from the active VDF database before consolidation could be committed.";
						return false;
					}

					FileEntry? occupant = snapshot.FirstOrDefault(entry =>
						!ReferenceEquals(entry, keeper) &&
						comparer.Equals(NormalizeConsolidationPath(entry.Path), destination));
					if (occupant != null && !removed.Contains(NormalizeConsolidationPath(occupant.Path))) {
						error = "The final destination is occupied by a database entry that was not removed by this consolidation.";
						return false;
					}

					oldKeeperPath = keeper.Path;
					foreach (FileEntry entry in snapshot) {
						if (ReferenceEquals(entry, keeper)) continue;
						if (removed.Contains(NormalizeConsolidationPath(entry.Path)))
							removedEntries.Add(entry);
					}

					DatabaseUtils.Database.Remove(keeper);
					foreach (FileEntry entry in removedEntries)
						DatabaseUtils.Database.Remove(entry);
					keeper.Path = destinationPath;
					DatabaseUtils.Database.Add(keeper);
					DatabaseUtils.SaveDatabase();
					error = string.Empty;
					return true;
				}
				catch (Exception ex) {
					try {
						if (keeper != null) {
							DatabaseUtils.Database.Remove(keeper);
							if (!string.IsNullOrEmpty(oldKeeperPath))
								keeper.Path = oldKeeperPath;
							DatabaseUtils.Database.Add(keeper);
						}
						foreach (FileEntry entry in removedEntries)
							DatabaseUtils.Database.Add(entry);
						DatabaseUtils.SaveDatabase();
					}
					catch (Exception rollbackEx) {
						Logger.Instance.Error($"Consolidation DB rollback failed: {rollbackEx}");
					}
					error = ex.Message;
					return false;
				}
			}
		}

		static string NormalizeConsolidationPath(string path) {
			if (string.IsNullOrWhiteSpace(path))
				throw new ArgumentException("Path is empty.", nameof(path));
			return Path.GetFullPath(path);
		}
	}
}
