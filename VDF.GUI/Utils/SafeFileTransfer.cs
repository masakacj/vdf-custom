// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Security.Cryptography;

namespace VDF.GUI.Utils {
	internal readonly record struct SafeMoveResult(bool Success, string NewPath, string? Error);

	/// <summary>
	/// Conservative move primitives for collection consolidation. Same-volume moves use
	/// filesystem rename. Cross-volume moves copy to a temporary sibling, verify complete
	/// SHA-256, atomically rename the verified copy, and only then delete the source.
	/// </summary>
	internal static class SafeFileTransfer {
		internal static string BuildDestinationPath(string sourcePath, string targetFolder, string? preferredFileName = null) {
			Directory.CreateDirectory(targetFolder);
			string fileName = string.IsNullOrWhiteSpace(preferredFileName)
				? Path.GetFileName(sourcePath)
				: preferredFileName!;
			string candidate = Path.Combine(targetFolder, fileName);
			if (!File.Exists(candidate) && !Directory.Exists(candidate))
				return candidate;

			string stem = Path.GetFileNameWithoutExtension(fileName);
			string ext = Path.GetExtension(fileName);
			for (int i = 1; ; i++) {
				candidate = Path.Combine(targetFolder, $"{stem}_best{i}{ext}");
				if (!File.Exists(candidate) && !Directory.Exists(candidate))
					return candidate;
			}
		}

		internal static SafeMoveResult MoveVerified(string sourcePath, string targetFolder, string? preferredFileName = null) {
			if (!File.Exists(sourcePath))
				return new SafeMoveResult(false, sourcePath, "source file does not exist");

			string destination = BuildDestinationPath(sourcePath, targetFolder, preferredFileName);
			return MoveVerifiedCore(sourcePath, destination, rejectExistingDestination: false);
		}

		/// <summary>
		/// Moves to one exact destination path. Unlike <see cref="MoveVerified"/>, this method
		/// never invents _bestN names and never overwrites an existing path. Resource-series
		/// consolidation uses it so directory metadata and collision semantics stay explicit.
		/// </summary>
		internal static SafeMoveResult MoveVerifiedExact(string sourcePath, string destinationPath) {
			if (!File.Exists(sourcePath))
				return new SafeMoveResult(false, sourcePath, "source file does not exist");
			if (string.IsNullOrWhiteSpace(destinationPath))
				return new SafeMoveResult(false, sourcePath, "destination path is empty");

			string sourceFull = Path.GetFullPath(sourcePath);
			string destinationFull = Path.GetFullPath(destinationPath);
			var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if (sourceFull.Equals(destinationFull, comparison))
				return new SafeMoveResult(true, sourceFull, null);
			if (File.Exists(destinationFull) || Directory.Exists(destinationFull))
				return new SafeMoveResult(false, sourcePath, "destination already exists");

			string? parent = Path.GetDirectoryName(destinationFull);
			if (string.IsNullOrWhiteSpace(parent))
				return new SafeMoveResult(false, sourcePath, "destination has no parent folder");
			Directory.CreateDirectory(parent);
			return MoveVerifiedCore(sourceFull, destinationFull, rejectExistingDestination: true);
		}

		static SafeMoveResult MoveVerifiedCore(string sourcePath, string destination, bool rejectExistingDestination) {
			string? temp = null;
			try {
				if (rejectExistingDestination && (File.Exists(destination) || Directory.Exists(destination)))
					throw new IOException("destination already exists");

				if (SameVolume(sourcePath, destination)) {
					File.Move(sourcePath, destination);
					return new SafeMoveResult(true, destination, null);
				}

				temp = destination + $".vdf-transfer-{Guid.NewGuid():N}.tmp";
				File.Copy(sourcePath, temp, overwrite: false);

				byte[] sourceHash = ComputeSha256(sourcePath);
				byte[] copiedHash = ComputeSha256(temp);
				if (!CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash))
					throw new IOException("full-file SHA-256 verification failed after cross-volume copy");

				if (rejectExistingDestination && (File.Exists(destination) || Directory.Exists(destination)))
					throw new IOException("destination appeared during transfer");
				File.Move(temp, destination);
				temp = null;
				File.Delete(sourcePath);
				return new SafeMoveResult(true, destination, null);
			}
			catch (Exception ex) {
				try { if (temp != null && File.Exists(temp)) File.Delete(temp); } catch { }
				return new SafeMoveResult(false, sourcePath, ex.Message);
			}
		}

		static bool SameVolume(string a, string b) {
			string rootA = Path.GetPathRoot(Path.GetFullPath(a)) ?? string.Empty;
			string rootB = Path.GetPathRoot(Path.GetFullPath(b)) ?? string.Empty;
			return string.Equals(rootA, rootB, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		}

		static byte[] ComputeSha256(string path) {
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
				FileOptions.SequentialScan);
			return SHA256.HashData(stream);
		}
	}
}
