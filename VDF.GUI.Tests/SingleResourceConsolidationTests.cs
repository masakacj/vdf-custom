// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.GUI.Views;

namespace VDF.GUI.Tests;

public class SingleResourceConsolidationTests {
	[Fact]
	public void AnchoredDestination_PreservesSeriesPathAndAnchorStem() {
		string anchor = Path.Combine("D:" + Path.DirectorySeparatorChar, "Series A", "2026-02-14", "情人节", "003.mp4");
		string best = Path.Combine("E:" + Path.DirectorySeparatorChar, "Misc", "random-name.mp4");

		string destination = SingleResourceConsolidationDialog.BuildAnchoredDestination(anchor, best);

		Assert.Equal(Path.Combine("D:" + Path.DirectorySeparatorChar, "Series A", "2026-02-14", "情人节", "003.mp4"), destination);
	}

	[Fact]
	public void AnchoredDestination_UsesBestRealExtensionInsteadOfAnchorExtension() {
		string anchor = Path.Combine("D:" + Path.DirectorySeparatorChar, "Series A", "Theme", "episode.mp4");
		string best = Path.Combine("E:" + Path.DirectorySeparatorChar, "Misc", "higher-quality.mkv");

		string destination = SingleResourceConsolidationDialog.BuildAnchoredDestination(anchor, best);

		Assert.Equal(Path.Combine("D:" + Path.DirectorySeparatorChar, "Series A", "Theme", "episode.mkv"), destination);
	}
}
