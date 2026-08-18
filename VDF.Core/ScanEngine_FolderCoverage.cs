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
	/// <summary>
	/// Cached media statistics for one direct parent folder. The folder-coverage planner
	/// consumes the scan database instead of walking the disk again, so building merge
	/// groups is effectively metadata-only even for HDD/NAS libraries.
	/// </summary>
	public readonly record struct FolderMediaStats(int FileCount, long TotalBytes);

	/// <summary>Non-owning snapshot used by the GUI merge planner; no media bytes are read.</summary>
	public readonly record struct FolderMediaFile(string Path, long SizeBytes);

	public sealed partial class ScanEngine {
		/// <summary>
		/// Returns direct-parent folder counts from the already loaded VDF database.
		/// This method performs no file reads and no directory enumeration. Requested
		/// folders that are not present in the database are returned with zero counts so
		/// the GUI can fall back to the currently visible duplicate participants.
		/// </summary>
		public IReadOnlyDictionary<string, FolderMediaStats> GetDirectFolderMediaStats(IEnumerable<string> folders) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var requested = new HashSet<string>(comparer);
			foreach (string folder in folders) {
				string normalized = NormalizeCoverageFolder(folder);
				if (normalized.Length > 0)
					requested.Add(normalized);
			}

			var mutable = requested.ToDictionary(
				folder => folder,
				_ => (Count: 0, Bytes: 0L),
				comparer);

			if (mutable.Count == 0)
				return new Dictionary<string, FolderMediaStats>(comparer);

			foreach (FileEntry entry in DatabaseUtils.Database.ToArray()) {
				string folder = NormalizeCoverageFolder(entry.Folder);
				if (!mutable.TryGetValue(folder, out var value))
					continue;
				value.Count++;
				value.Bytes += Math.Max(0, entry.FileSize);
				mutable[folder] = value;
			}

			var result = new Dictionary<string, FolderMediaStats>(mutable.Count, comparer);
			foreach (var pair in mutable)
				result[pair.Key] = new FolderMediaStats(pair.Value.Count, pair.Value.Bytes);
			return result;
		}

		/// <summary>
		/// Returns cached direct-folder file paths/sizes for an explicit consolidation plan.
		/// It still does no directory walk and no media read; callers may later touch only
		/// the files the user explicitly chooses to move/verify.
		/// </summary>
		public IReadOnlyList<FolderMediaFile> GetDirectFolderMediaFiles(string folder) {
			string requested = NormalizeCoverageFolder(folder);
			if (requested.Length == 0)
				return Array.Empty<FolderMediaFile>();
			var comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			return DatabaseUtils.Database
				.ToArray()
				.Where(entry => string.Equals(NormalizeCoverageFolder(entry.Folder), requested, comparison))
				.Select(entry => new FolderMediaFile(entry.Path, Math.Max(0, entry.FileSize)))
				.ToList();
		}

		static string NormalizeCoverageFolder(string? folder) {
			string value = (folder ?? string.Empty).Trim();
			while (value.Length > 1 && (value.EndsWith("\\", StringComparison.Ordinal) || value.EndsWith("/", StringComparison.Ordinal)))
				value = value[..^1];
			return value;
		}
	}
}
