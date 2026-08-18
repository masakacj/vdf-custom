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
}
