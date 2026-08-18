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
	/// Conservative move primitive for collection consolidation. Same-volume moves use
	/// the filesystem rename operation. Cross-volume moves copy to a temporary sibling,
	/// verify the complete SHA-256 of source and copy, atomically rename the verified copy
	/// to its final name, and only then delete the source. Any failure leaves the source.
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
			string? temp = null;
			try {
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
