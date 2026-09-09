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
		/// touches files. HashSet lookup is path-based, so this stays O(group size) even when
		/// ScannedFiles.db contains millions of entries. Lookup keys are identity-only and never
		/// stat the destination/duplicate files.
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

				if (!DatabaseUtils.Database.TryGetValue(CreateInteractiveLookupEntry(keeperPath), out _)) {
					error = "BEST file is not present in the active VDF database.";
					return false;
				}

				if (DatabaseUtils.Database.TryGetValue(CreateInteractiveLookupEntry(destination), out FileEntry? occupant) &&
					occupant != null && !known.Contains(NormalizeConsolidationPath(occupant.Path))) {
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
		/// Commits only the affected entries after the verified filesystem operation completed.
		/// A tiny durable sidecar journal records the mutation instead of rewriting a multi-GB
		/// ScannedFiles.db after every consolidation. The base database format is unchanged and
		/// the journal is replayed on startup until a later normal checkpoint absorbs it.
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
					if (!DatabaseUtils.Database.TryGetValue(CreateInteractiveLookupEntry(keeperPath), out keeper) || keeper == null) {
						error = "BEST file disappeared from the active VDF database before consolidation could be committed.";
						return false;
					}

					if (DatabaseUtils.Database.TryGetValue(CreateInteractiveLookupEntry(destination), out FileEntry? occupant) &&
						occupant != null && !ReferenceEquals(occupant, keeper) &&
						!removed.Contains(NormalizeConsolidationPath(occupant.Path))) {
						error = "The final destination is occupied by a database entry that was not removed by this consolidation.";
						return false;
					}

					oldKeeperPath = keeper.Path;
					foreach (string path in removed) {
						if (DatabaseUtils.Database.TryGetValue(CreateInteractiveLookupEntry(path), out FileEntry? entry) &&
							entry != null && !ReferenceEquals(entry, keeper))
							removedEntries.Add(entry);
					}

					DatabaseUtils.Database.Remove(keeper);
					foreach (FileEntry entry in removedEntries)
						DatabaseUtils.Database.Remove(entry);
					keeper.Path = destinationPath;
					DatabaseUtils.Database.Add(keeper);

					var moves = comparer.Equals(keeperPath, destination)
						? Array.Empty<(string OldPath, string NewPath)>()
						: new[] { (OldPath: keeperPath, NewPath: destination) };
					if (!PersistInteractiveDatabaseMutations(moves, removed, out string journalError)) {
						// Durability must win over latency. Journal failure is exceptional; fall back
						// to the legacy full checkpoint so a completed file operation is never lost.
						Logger.Instance.Warn($"Interactive database journal failed; falling back to a full database checkpoint: {journalError}");
						DatabaseUtils.SaveDatabase();
					}
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
