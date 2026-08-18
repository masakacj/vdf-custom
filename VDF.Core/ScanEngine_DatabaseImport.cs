// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Buffers.Binary;
using System.Linq;
using MemoryPack;
using VDF.Core.Utils;

namespace VDF.Core {
	/// <summary>Read-only summary shown before importing another VDF ScannedFiles.db.</summary>
	public sealed record DatabaseImportPreview {
		public required string SourcePath { get; init; }
		public required string SourceFormat { get; init; }
		public required int SourceVersion { get; init; }
		public required long SourceDatabaseBytes { get; init; }
		public required int SourceEntryCount { get; init; }
		public required long SourceMediaBytes { get; init; }
		public required int CurrentEntryCount { get; init; }
		public required long CurrentMediaBytes { get; init; }
		public required long CurrentDatabaseBytes { get; init; }
		public required int NewEntryCount { get; init; }
		public required long NewMediaBytes { get; init; }
		public required int ExistingPathCount { get; init; }
		/// <summary>Imported still-image hashes from an older image pipeline are deliberately discarded.</summary>
		public required int LegacyImageEntriesNeedingRehash { get; init; }
	}

	/// <summary>Result of an additive database import. Existing paths are never overwritten.</summary>
	public sealed record DatabaseImportResult {
		public required DatabaseImportPreview Preview { get; init; }
		public required int ImportedEntryCount { get; init; }
		public required long ImportedMediaBytes { get; init; }
		public required int FinalEntryCount { get; init; }
		public required long FinalMediaBytes { get; init; }
		public string? BackupPath { get; init; }
	}

	public sealed partial class ScanEngine {
		/// <summary>
		/// Validates and summarizes an original/custom VDF native database without changing the
		/// active database. Supports VDFDB002, VDFDB001 and legacy protobuf databases.
		/// </summary>
		public static DatabaseImportPreview PreviewNativeDatabaseImport(string databasePath) =>
			NativeDatabaseImporter.Preview(databasePath);

		/// <summary>
		/// Safely merges a native VDF database into the active one. Import is additive: an entry
		/// whose path already exists is skipped, so the active/custom cache always wins. Before
		/// writing, the current database is backed up beside ScannedFiles.db. If saving fails,
		/// added entries are rolled back and the on-disk backup is restored.
		/// </summary>
		public static DatabaseImportResult ImportNativeDatabase(string databasePath) =>
			NativeDatabaseImporter.Import(databasePath);
	}
}

namespace VDF.Core.Utils {
	/// <summary>
	/// Native database reader/importer kept separate from DatabaseUtils' normal startup loader.
	/// VDFDB002 is processed entry-by-entry so importing a multi-million-file database does not
	/// require holding a second complete database object graph in memory.
	/// </summary>
	static class NativeDatabaseImporter {
		static ReadOnlySpan<byte> FormatMagic => "VDFDB001"u8;
		static ReadOnlySpan<byte> StreamingMagic => "VDFDB002"u8;
		const int MaxSaneEntryBytes = 256 * 1024 * 1024;

		readonly record struct ScanSummary(
			string Format,
			int Version,
			int ImageHashPipeline,
			int EntryCount,
			long MediaBytes,
			int LegacyImagesRehashed);

		internal static DatabaseImportPreview Preview(string databasePath) {
			string sourcePath = ValidateSourcePath(databasePath);
			var existing = CurrentPathSet();
			int newCount = 0;
			long newBytes = 0;
			int existingCount = 0;
			ScanSummary source = ScanFile(sourcePath, entry => {
				if (existing.Contains(entry.Path)) {
					existingCount++;
					return;
				}
				newCount++;
				newBytes += Math.Max(0, entry.FileSize);
			});

			string currentPath = CurrentDatabasePath();
			long currentDbBytes = File.Exists(currentPath) ? new FileInfo(currentPath).Length : 0;
			return new DatabaseImportPreview {
				SourcePath = sourcePath,
				SourceFormat = source.Format,
				SourceVersion = source.Version,
				SourceDatabaseBytes = new FileInfo(sourcePath).Length,
				SourceEntryCount = source.EntryCount,
				SourceMediaBytes = source.MediaBytes,
				CurrentEntryCount = DatabaseUtils.Database.Count,
				CurrentMediaBytes = SumMediaBytes(DatabaseUtils.Database),
				CurrentDatabaseBytes = currentDbBytes,
				NewEntryCount = newCount,
				NewMediaBytes = newBytes,
				ExistingPathCount = existingCount,
				LegacyImageEntriesNeedingRehash = source.LegacyImagesRehashed,
			};
		}

		internal static DatabaseImportResult Import(string databasePath) {
			DatabaseImportPreview preview = Preview(databasePath);
			if (preview.NewEntryCount == 0) {
				return new DatabaseImportResult {
					Preview = preview,
					ImportedEntryCount = 0,
					ImportedMediaBytes = 0,
					FinalEntryCount = DatabaseUtils.Database.Count,
					FinalMediaBytes = SumMediaBytes(DatabaseUtils.Database),
				};
			}

			// Persist the exact live state first, then make a byte-for-byte rollback point.
			DatabaseUtils.SaveDatabase();
			string backupPath = CreateBackup();
			var existing = CurrentPathSet();
			var added = new List<FileEntry>(Math.Min(preview.NewEntryCount, 4096));
			long importedBytes = 0;

			try {
				ScanFile(preview.SourcePath, entry => {
					if (!existing.Add(entry.Path))
						return; // current/custom entry always wins by path
					DatabaseUtils.Database.Add(entry);
					added.Add(entry);
					importedBytes += Math.Max(0, entry.FileSize);
				});
				DatabaseUtils.SaveDatabase();
			}
			catch {
				foreach (FileEntry entry in added)
					DatabaseUtils.Database.Remove(entry);
				try {
					File.Copy(backupPath, CurrentDatabasePath(), overwrite: true);
				}
				catch (Exception restoreError) {
					Logger.Instance.Error($"Database import rollback could not restore '{backupPath}': {restoreError}");
				}
				throw;
			}

			Logger.Instance.Info(
				$"Imported {added.Count:N0} cached VDF entries ({importedBytes:N0} indexed bytes); " +
				$"skipped {preview.ExistingPathCount:N0} paths already present. Backup: {backupPath}");
			return new DatabaseImportResult {
				Preview = preview,
				ImportedEntryCount = added.Count,
				ImportedMediaBytes = importedBytes,
				FinalEntryCount = DatabaseUtils.Database.Count,
				FinalMediaBytes = SumMediaBytes(DatabaseUtils.Database),
				BackupPath = backupPath,
			};
		}

		static string ValidateSourcePath(string databasePath) {
			if (string.IsNullOrWhiteSpace(databasePath))
				throw new ArgumentException("Database path is empty.", nameof(databasePath));
			string full = Path.GetFullPath(databasePath);
			if (!File.Exists(full))
				throw new FileNotFoundException("VDF database file was not found.", full);
			if (new FileInfo(full).Length == 0)
				throw new InvalidDataException("VDF database file is empty.");
			StringComparison comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if (string.Equals(full, Path.GetFullPath(CurrentDatabasePath()), comparison))
				throw new InvalidOperationException("The selected file is already the active VDF database.");
			return full;
		}

		static HashSet<string> CurrentPathSet() => new(
			DatabaseUtils.Database.Select(e => e.Path),
			CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

		static long SumMediaBytes(IEnumerable<FileEntry> entries) {
			long total = 0;
			foreach (FileEntry entry in entries) {
				long size = Math.Max(0, entry.FileSize);
				if (long.MaxValue - total < size)
					return long.MaxValue;
				total += size;
			}
			return total;
		}

		static string CurrentDatabasePath() =>
			FileUtils.SafePathCombine(DatabaseUtils.GetDatabaseFolderPath(), "ScannedFiles.db");

		static string CreateBackup() {
			string current = CurrentDatabasePath();
			string folder = Path.GetDirectoryName(current)!;
			string backup = Path.Combine(folder, $"ScannedFiles.before-import-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
			File.Copy(current, backup, overwrite: false);
			return backup;
		}

		static ScanSummary ScanFile(string path, Action<FileEntry> onEntry) {
			MemoryPackRegistration.Register();
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
			Span<byte> magic = stackalloc byte[8];
			stream.ReadExactly(magic);

			if (magic.SequenceEqual(StreamingMagic))
				return ScanStreaming(stream, onEntry);

			stream.Position = 0;
			DatabaseWrapper wrapper;
			string format;
			if (magic.SequenceEqual(FormatMagic)) {
				stream.Position = 8;
				wrapper = MemoryPackSerializer.DeserializeAsync<DatabaseWrapper>(stream)
					.AsTask().GetAwaiter().GetResult()
					?? throw new InvalidDataException("Database payload deserialized to null.");
				format = "VDFDB001";
			}
			else {
				byte[] raw = new byte[stream.Length];
				stream.ReadExactly(raw);
				wrapper = LegacyDatabaseReader.Read(raw);
				format = "Legacy protobuf";
			}

			int count = 0;
			long bytes = 0;
			int migrated = 0;
			bool legacyImages = wrapper.ImageHashPipeline < DatabaseUtils.CurrentImageHashPipeline;
			foreach (FileEntry entry in wrapper.Entries) {
				if (legacyImages && PrepareLegacyImage(entry)) migrated++;
				count++;
				bytes = AddSaturated(bytes, Math.Max(0, entry.FileSize));
				onEntry(entry);
			}
			return new ScanSummary(format, wrapper.Version, wrapper.ImageHashPipeline, count, bytes, migrated);
		}

		static ScanSummary ScanStreaming(Stream stream, Action<FileEntry> onEntry) {
			Span<byte> intBuf = stackalloc byte[sizeof(int)];
			stream.ReadExactly(intBuf);
			int version = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
			stream.ReadExactly(intBuf);
			int imagePipeline = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
			bool legacyImages = imagePipeline < DatabaseUtils.CurrentImageHashPipeline;
			byte[] buffer = new byte[1 << 16];
			int count = 0;
			long bytes = 0;
			int migrated = 0;

			while (true) {
				stream.ReadExactly(intBuf);
				int length = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
				if (length == -1) break;
				if (length < 0 || length > MaxSaneEntryBytes)
					throw new InvalidDataException($"Corrupt database entry length: {length}.");
				if (length > stream.Length - stream.Position)
					throw new InvalidDataException("Database is truncated inside an entry.");
				if (length > buffer.Length)
					buffer = new byte[length];
				stream.ReadExactly(buffer.AsSpan(0, length));
				FileEntry entry = MemoryPackSerializer.Deserialize<FileEntry>(buffer.AsSpan(0, length))
					?? throw new InvalidDataException("Database entry deserialized to null.");
				if (legacyImages && PrepareLegacyImage(entry)) migrated++;
				count++;
				bytes = AddSaturated(bytes, Math.Max(0, entry.FileSize));
				onEntry(entry);
			}
			return new ScanSummary("VDFDB002", version, imagePipeline, count, bytes, migrated);
		}

		static bool PrepareLegacyImage(FileEntry entry) {
			if (!entry.IsImage)
				return false;
			bool hadLegacyData = entry.grayBytes.Count > 0 || entry.PHashes.Count > 0 || entry.Flags.Has(EntryFlags.TooDark);
			entry.grayBytes.Clear();
			entry.PHashes.Clear();
			entry.Flags.Set(EntryFlags.TooDark, false);
			return hadLegacyData;
		}

		static long AddSaturated(long a, long b) => long.MaxValue - a < b ? long.MaxValue : a + b;
	}
}
