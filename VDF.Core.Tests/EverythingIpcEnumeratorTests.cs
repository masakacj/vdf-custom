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

	[Fact]
	public void NetworkFolderIndex_VerifiesMappedSubfolderWhenMonitoredAndUnfiltered() {
		const string ini = """
			[Everything]
			exclude_list_enabled=1
			exclude_hidden_files_and_folders=0
			exclude_system_files_and_folders=0
			include_only_files=
			exclude_files=
			exclude_folders=
			folders="W:\\","X:\\","Y:\\","Z:\\"
			folder_monitor_changes=1,1,1,1
			""";

		bool ok = EverythingFolderIndexCoverageDetector.TryVerifyFromIni(
			@"Y:\pipa", ini, @"C:\Users\test\AppData\Roaming\Everything\Everything.ini",
			out EverythingFolderIndexCoverage? coverage, out string? reason);

		Assert.True(ok, reason);
		Assert.NotNull(coverage);
		Assert.Equal(@"Y:\", coverage!.IndexedRoot, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void NetworkFolderIndex_RejectsUnmonitoredOrFilteredIndex() {
		const string unmonitored = """
			[Everything]
			folders="Y:\\"
			folder_monitor_changes=0
			""";
		Assert.False(EverythingFolderIndexCoverageDetector.TryVerifyFromIni(
			@"Y:\pipa", unmonitored, "Everything.ini", out _, out string? monitorReason));
		Assert.Contains("not monitoring", monitorReason, StringComparison.OrdinalIgnoreCase);

		const string filtered = """
			[Everything]
			exclude_list_enabled=1
			exclude_folders="Y:\\private"
			folders="Y:\\"
			folder_monitor_changes=1
			""";
		Assert.False(EverythingFolderIndexCoverageDetector.TryVerifyFromIni(
			@"Y:\pipa", filtered, "Everything.ini", out _, out string? filterReason));
		Assert.Contains("exclude_folders", filterReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void IniListParser_UnescapesQuotedEverythingPaths() {
		List<string> values = EverythingFolderIndexCoverageDetector.ParseIniList(""""Y:\\","\\\\server\\share"""");
		Assert.Equal(2, values.Count);
		Assert.Equal(@"Y:\", values[0]);
		Assert.Equal(@"\\server\share", values[1]);
	}
}
