// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.GUI.Utils;

namespace VDF.GUI.Tests;

public class SafeFileTransferTests : IDisposable {
	readonly string root = Directory.CreateTempSubdirectory("vdf-safe-transfer-").FullName;

	public void Dispose() {
		try { Directory.Delete(root, recursive: true); } catch { }
	}

	[Fact]
	public void BuildDestinationPath_NeverOverwritesExistingFile() {
		string source = Path.Combine(root, "source.mkv");
		string target = Path.Combine(root, "target");
		Directory.CreateDirectory(target);
		File.WriteAllText(source, "better");
		File.WriteAllText(Path.Combine(target, "episode.mkv"), "older");

		string path = SafeFileTransfer.BuildDestinationPath(source, target, "episode.mkv");

		Assert.Equal(Path.Combine(target, "episode_best1.mkv"), path);
	}

	[Fact]
	public void MoveVerified_SameVolumeMovesWithoutDeletingOnFailurePath() {
		string source = Path.Combine(root, "keeper.bin");
		string target = Path.Combine(root, "collection");
		byte[] payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
		File.WriteAllBytes(source, payload);

		var result = SafeFileTransfer.MoveVerified(source, target);

		Assert.True(result.Success, result.Error);
		Assert.False(File.Exists(source));
		Assert.True(File.Exists(result.NewPath));
		Assert.Equal(payload, File.ReadAllBytes(result.NewPath));
	}

	[Fact]
	public void MoveVerified_MissingSourceFailsAndLeavesNoDestination() {
		string source = Path.Combine(root, "missing.bin");
		string target = Path.Combine(root, "collection");

		var result = SafeFileTransfer.MoveVerified(source, target);

		Assert.False(result.Success);
		Assert.False(Directory.Exists(target));
	}

	[Fact]
	public void ReplaceVerifiedExact_ReplacesAnchorWithBestAndOnlyThenRemovesSource() {
		string source = Path.Combine(root, "misc-best.mkv");
		string targetDir = Path.Combine(root, "Series", "2026-02-14", "Theme");
		Directory.CreateDirectory(targetDir);
		string destination = Path.Combine(targetDir, "003.mkv");
		byte[] best = Enumerable.Range(0, 16384).Select(i => (byte)(i % 239)).ToArray();
		File.WriteAllBytes(source, best);
		File.WriteAllText(destination, "old-low-quality-copy");

		var result = SafeFileTransfer.ReplaceVerifiedExact(source, destination);

		Assert.True(result.Success, result.Error);
		Assert.False(File.Exists(source));
		Assert.Equal(best, File.ReadAllBytes(destination));
		Assert.Empty(Directory.EnumerateFiles(targetDir, "*.vdf-replaced-*.bak"));
		Assert.Empty(Directory.EnumerateFiles(targetDir, "*.vdf-replace-*.tmp"));
	}

	[Fact]
	public void ReplaceVerifiedExact_MissingSourceDoesNotTouchExistingAnchor() {
		string source = Path.Combine(root, "missing-best.mkv");
		string destination = Path.Combine(root, "003.mkv");
		File.WriteAllText(destination, "keep-me");

		var result = SafeFileTransfer.ReplaceVerifiedExact(source, destination);

		Assert.False(result.Success);
		Assert.Equal("keep-me", File.ReadAllText(destination));
	}
}
