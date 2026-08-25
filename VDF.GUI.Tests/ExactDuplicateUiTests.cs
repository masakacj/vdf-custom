// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class ExactDuplicateUiTests {
	static DuplicateItemVM Exact(Guid group, string path) => new() {
		IsVisibleInFilter = true,
		ItemInfo = new DuplicateItem {
			GroupId = group,
			Path = path,
			Folder = Path.GetDirectoryName(path) ?? string.Empty,
			SizeLong = 123456,
			Similarity = 100,
			Duration = TimeSpan.Zero,
			Flags = DuplicateFlags.ByteIdentical,
		}
	};

	[Fact]
	public void ResultsBuilder_ExposesByteIdenticalGroupBadge() {
		Guid group = Guid.NewGuid();
		var a = Exact(group, @"D:\A\same.bin");
		var b = Exact(group, @"E:\B\same.bin");

		ResultsBuildResult result = ResultsListBuilder.Build(new ResultsBuildRequest {
			Items = new[] { a, b },
			RecommendBest = members => MainWindowVM.RecommendBest(members),
			IsTombstone = _ => false,
			IsOffline = _ => false,
		});

		ResultsGroupHeader header = Assert.Single(result.Groups);
		Assert.True(header.HasByteIdenticalMatches);
		Assert.False(header.HasGrayscaleMatches);
		Assert.False(header.HasPHashMatches);
	}

	[Fact]
	public void ByteIdenticalBest_IsConfirmedAndPathStable() {
		Guid group = Guid.NewGuid();
		var z = Exact(group, @"Z:\copy.bin");
		var a = Exact(group, @"A:\copy.bin");

		BestRecommendation forward = MainWindowVM.RecommendBest(new[] { z, a });
		BestRecommendation reverse = MainWindowVM.RecommendBest(new[] { a, z });

		Assert.True(forward.IsConfirmed);
		Assert.Same(a, forward.Winner);
		Assert.Same(a, reverse.Winner);
		Assert.Contains("SHA-256", forward.Reason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void SetupAndCommandRoute_ExposeIndependentExactDuplicatePass() {
		string root = RepoRoot();
		string setup = File.ReadAllText(Path.Combine(root, "VDF.GUI", "Views", "SetupView.xaml"));
		string vm = File.ReadAllText(Path.Combine(root, "VDF.GUI", "ViewModels", "MainWindowVM.cs"));

		Assert.Contains("CommandParameter=\"ExactDuplicates\"", setup, StringComparison.Ordinal);
		Assert.Contains("Setup.ExactDuplicatesTip", setup, StringComparison.Ordinal);
		Assert.Contains("case \"ExactDuplicates\"", vm, StringComparison.Ordinal);
		Assert.Contains("Scanner.StartExactDuplicateScan()", vm, StringComparison.Ordinal);
		Assert.Contains("!completedExactDuplicateScan && SettingsFile.Instance.GeneratePreviewThumbnails", vm, StringComparison.Ordinal);
	}

	static string RepoRoot() {
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
			dir = dir.Parent;
		return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
	}
}
