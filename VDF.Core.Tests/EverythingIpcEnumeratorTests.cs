// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.Core.Utils;

namespace VDF.Core.Tests;

public class EverythingIpcEnumeratorTests {
	static string Root => Path.Combine(Path.GetTempPath(), "vdf everything root");

	[Fact]
	public void BuildSearch_UsesFileOnlyPathForRecursiveAndParentForFlat() {
		string recursive = EverythingIpcEnumerator.BuildSearch(Root, true, [".mp4", ".mkv", ".jpg"]);
		string flat = EverythingIpcEnumerator.BuildSearch(Root, false, [".mp4", ".mkv"]);

		Assert.StartsWith("file: path:\"", recursive, StringComparison.Ordinal);
		Assert.Contains("vdf everything root", recursive, StringComparison.Ordinal);
		Assert.EndsWith("ext:mp4;mkv;jpg", recursive, StringComparison.Ordinal);
		Assert.StartsWith("file: parent:\"", flat, StringComparison.Ordinal);
		Assert.EndsWith("ext:mp4;mkv", flat, StringComparison.Ordinal);
	}

	[Fact]
	public void IsPathInScope_RespectsDirectoryBoundaryAndRecursion() {
		string direct = Path.Combine(Root, "movie.mp4");
		string nested = Path.Combine(Root, "Season 1", "episode.mkv");
		string siblingPrefix = Root + "-other" + Path.DirectorySeparatorChar + "movie.mp4";

		Assert.True(EverythingIpcEnumerator.IsPathInScope(Root, direct, recursive: false));
		Assert.False(EverythingIpcEnumerator.IsPathInScope(Root, nested, recursive: false));
		Assert.True(EverythingIpcEnumerator.IsPathInScope(Root, nested, recursive: true));
		Assert.False(EverythingIpcEnumerator.IsPathInScope(Root, siblingPrefix, recursive: true));
	}

	[Fact]
	public void FolderRules_ExcludePlainPathAndNameWildcardAcrossAncestors() {
		string nested = Path.Combine(Root, "Downloads", "temp-cache", "movie.mp4");
		string exactExcluded = Path.Combine(Root, "Downloads");

		Assert.True(EverythingIpcEnumerator.IsExcludedByFolderRules(Root, nested, [exactExcluded]));
		Assert.True(EverythingIpcEnumerator.IsExcludedByFolderRules(Root, nested, ["temp-*"]));
		Assert.False(EverythingIpcEnumerator.IsExcludedByFolderRules(Root, nested, ["keep-*"]));
	}

	[Fact]
	public void FolderRules_DoNotExcludeInitialIncludeItself() {
		string movie = Path.Combine(Root, "movie.mp4");
		Assert.False(EverythingIpcEnumerator.IsExcludedByFolderRules(Root, movie, [Root]));
	}
}
