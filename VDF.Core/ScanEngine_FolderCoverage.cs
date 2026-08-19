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
	/// <summary>Cached media statistics for one folder tree.</summary>
	public readonly record struct FolderMediaStats(int FileCount, long TotalBytes);

	/// <summary>Non-owning snapshot used by the GUI consolidation planner; no media bytes are read.</summary>
	public readonly record struct FolderMediaFile(string Path, long SizeBytes);

	public sealed partial class ScanEngine {
		/// <summary>
		/// Returns counts for the requested direct folders and, for resource-consolidation
		/// planning, useful ancestors below the configured scan roots. All counts come from
		/// the already-loaded VDF database: no directory enumeration and no media reads.
		/// Existing callers still receive the exact requested keys; ancestor keys are additive.
		/// </summary>
		public IReadOnlyDictionary<string, FolderMediaStats> GetDirectFolderMediaStats(IEnumerable<string> folders) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var requested = new HashSet<string>(comparer);
			foreach (string folder in folders) {
				string normalized = NormalizeCoverageFolder(folder);
				if (normalized.Length > 0)
					requested.Add(normalized);
			}
			if (requested.Count == 0)
				return new Dictionary<string, FolderMediaStats>(comparer);

			var candidates = new HashSet<string>(requested, comparer);
			foreach (string folder in requested)
				foreach (string ancestor in EnumerateSeriesAncestors(folder))
					candidates.Add(ancestor);

			return GetRecursiveFolderMediaStats(candidates);
		}

		/// <summary>
		/// Returns recursive media counts for explicit folder roots from the VDF database.
		/// One database pass is used and each entry walks only its ancestor chain.
		/// </summary>
		public IReadOnlyDictionary<string, FolderMediaStats> GetRecursiveFolderMediaStats(IEnumerable<string> folders) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var requested = new HashSet<string>(comparer);
			foreach (string folder in folders) {
				string normalized = NormalizeCoverageFolder(folder);
				if (normalized.Length > 0)
					requested.Add(normalized);
			}
			var mutable = requested.ToDictionary(folder => folder, _ => (Count: 0, Bytes: 0L), comparer);
			if (mutable.Count == 0)
				return new Dictionary<string, FolderMediaStats>(comparer);

			foreach (FileEntry entry in DatabaseUtils.Database.ToArray()) {
				string folder = NormalizeCoverageFolder(entry.Folder);
				while (folder.Length > 0) {
					if (mutable.TryGetValue(folder, out var value)) {
						value.Count++;
						value.Bytes = SaturatingAdd(value.Bytes, Math.Max(0, entry.FileSize));
						mutable[folder] = value;
					}
					string parent = CoverageParent(folder);
					if (parent.Length == 0 || parent.Equals(folder, StringComparison.Ordinal))
						break;
					folder = parent;
				}
			}

			var result = new Dictionary<string, FolderMediaStats>(mutable.Count, comparer);
			foreach (var pair in mutable)
				result[pair.Key] = new FolderMediaStats(pair.Value.Count, pair.Value.Bytes);
			return result;
		}

		/// <summary>
		/// Returns cached direct-folder file paths/sizes for legacy pair consolidation.
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

		/// <summary>
		/// Returns every cached media file under a series root, preserving its original path.
		/// Used to keep date/theme subfolders intact during resource consolidation.
		/// </summary>
		public IReadOnlyList<FolderMediaFile> GetRecursiveFolderMediaFiles(string folder) {
			string requested = NormalizeCoverageFolder(folder);
			if (requested.Length == 0)
				return Array.Empty<FolderMediaFile>();
			var comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			return DatabaseUtils.Database
				.ToArray()
				.Where(entry => CoveragePathIsInScope(NormalizeCoverageFolder(entry.Folder), requested, comparison))
				.Select(entry => new FolderMediaFile(entry.Path, Math.Max(0, entry.FileSize)))
				.ToList();
		}

		IEnumerable<string> EnumerateSeriesAncestors(string folder) {
			string current = NormalizeCoverageFolder(folder);
			if (current.Length == 0) yield break;

			var comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			string? boundary = Settings.IncludeList
				.Select(NormalizeCoverageFolder)
				.Where(root => root.Length > 0 && CoveragePathIsInScope(current, root, comparison))
				.OrderByDescending(root => root.Length)
				.FirstOrDefault();

			// The direct folder is already requested. Promote at most four levels when no
			// configured scan root can bound us; never expose a drive/filesystem root.
			int promoted = 0;
			while (promoted < 4) {
				string parent = CoverageParent(current);
				if (parent.Length == 0 || IsCoverageVolumeRoot(parent))
					yield break;
				if (boundary != null && parent.Equals(boundary, comparison)) {
					// A scan root is often a generic library folder. Do not promote to it when
					// there is already a more specific child; this prevents unrelated series
					// under one library root from becoming a mega-series.
					yield break;
				}
				yield return parent;
				current = parent;
				promoted++;
			}
		}

		static bool CoveragePathIsInScope(string path, string root, StringComparison comparison) =>
			path.Equals(root, comparison) ||
			(path.Length > root.Length && path.StartsWith(root, comparison) && path[root.Length] == '/');

		static string CoverageParent(string path) {
			string value = NormalizeCoverageFolder(path);
			int slash = value.LastIndexOf('/');
			if (slash <= 0) return string.Empty;
			// Preserve a Unix root as "/" only as a stopping sentinel; Windows drive roots
			// normalize as "C:" and are also treated as sentinels.
			return slash == 0 ? "/" : value[..slash];
		}

		static bool IsCoverageVolumeRoot(string path) =>
			path == "/" || (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':');

		static long SaturatingAdd(long a, long b) => long.MaxValue - a < b ? long.MaxValue : a + b;

		static string NormalizeCoverageFolder(string? folder) {
			string value = (folder ?? string.Empty).Trim().Replace('\\', '/');
			while (value.Length > 1 && value.EndsWith("/", StringComparison.Ordinal))
				value = value[..^1];
			return value;
		}
	}
}
