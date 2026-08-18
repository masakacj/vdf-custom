// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Linq;
using ReactiveUI;
using VDF.Core.Utils;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// The classic VDF file-similarity grouping remains the canonical result model.
	/// ResourceConsolidation is only a second presentation layer over those same groups.
	/// </summary>
	public enum ResultsDisplayMode {
		SimilarityGroups = 0,
		ResourceConsolidation = 1,
	}

	public sealed record ResultsDisplayModeOption(string Name, ResultsDisplayMode Mode);

	/// <summary>
	/// First row of either results view. Keeping the switcher inside the flattened list lets
	/// the feature land without replacing the mature file-row templates or interaction code.
	/// </summary>
	public sealed class ResultsViewSwitcherRow : ReactiveObject {
		readonly Action<ResultsDisplayMode> onChanged;
		ResultsDisplayModeOption _Selected;

		public ResultsViewSwitcherRow(
			IReadOnlyList<ResultsDisplayModeOption> options,
			ResultsDisplayMode selected,
			Action<ResultsDisplayMode> onChanged) {
			Options = options;
			this.onChanged = onChanged;
			_Selected = options.FirstOrDefault(o => o.Mode == selected) ?? options[0];
		}

		public IReadOnlyList<ResultsDisplayModeOption> Options { get; }
		public ResultsDisplayModeOption Selected {
			get => _Selected;
			set {
				if (value == null || value.Mode == _Selected.Mode) return;
				this.RaiseAndSetIfChanged(ref _Selected, value);
				onChanged(value.Mode);
			}
		}
		public string Label => "显示方式";
		public string Hint => Selected.Mode == ResultsDisplayMode.SimilarityGroups
			? "传统 VDF：每个相似文件组独立显示。"
			: "资源整合：按“目标合集 ← 来源目录”关系组织，同一个相似组只显示一次。";
	}

	/// <summary>Top-level header of one resource-oriented folder relationship.</summary>
	public sealed class ResourceRelationHeader {
		internal ResourceRelationHeader(PikPakFolderCoverageOption option, int displayedResourceGroups) {
			Option = option;
			DisplayedResourceGroups = displayedResourceGroups;

			// A nearly-complete side (>=90%) is a subset/source and flows INTO the other side.
			// When neither side is a subset, the side with higher relationship density is the
			// safer collection target. This prevents a huge Misc folder (1/5000 matched) from
			// becoming the target of Series A (1/100 matched) merely because its own coverage
			// is numerically lower. Ties prefer the resource-richer side.
			bool targetIsA = ChooseTargetIsA(option);
			TargetFolder = targetIsA ? option.FolderA : option.FolderB;
			SourceFolder = targetIsA ? option.FolderB : option.FolderA;
			TargetFiles = targetIsA ? option.TotalFilesA : option.TotalFilesB;
			SourceFiles = targetIsA ? option.TotalFilesB : option.TotalFilesA;
			TargetBytes = targetIsA ? option.TotalBytesA : option.TotalBytesB;
			SourceBytes = targetIsA ? option.TotalBytesB : option.TotalBytesA;
			TargetResources = targetIsA ? option.EstimatedResourcesA : option.EstimatedResourcesB;
			SourceResources = targetIsA ? option.EstimatedResourcesB : option.EstimatedResourcesA;
			SourceCoverage = targetIsA ? option.CoverageB : option.CoverageA;
			WholeSourceEligible = MainWindowVM.MayMergeWholeSource(SourceCoverage, option.ReviewOnlyGroupCount);
		}

		internal PikPakFolderCoverageOption Option { get; }
		public int DisplayedResourceGroups { get; }
		public string TargetFolder { get; }
		public string SourceFolder { get; }
		public int TargetFiles { get; }
		public int SourceFiles { get; }
		public long TargetBytes { get; }
		public long SourceBytes { get; }
		public int TargetResources { get; }
		public int SourceResources { get; }
		public double SourceCoverage { get; }
		public bool WholeSourceEligible { get; }
		public int ConfirmedMatches => Option.ConfirmedMatchedGroupCount;
		public int ReviewOnlyMatches => Option.ReviewOnlyGroupCount;

		public string DirectionLine => $"{TargetFolder}  ←  {SourceFolder}";
		public string TargetStats => $"目标：{TargetFiles:N0} 文件 · {TargetBytes.BytesToString()} · 约 {TargetResources:N0} 资源";
		public string SourceStats => $"来源：{SourceFiles:N0} 文件 · {SourceBytes.BytesToString()} · 约 {SourceResources:N0} 资源";
		public string RelationStats =>
			$"本视图归入 {DisplayedResourceGroups:N0} 个资源组 · 关系确认命中 {ConfirmedMatches:N0} · 待复核 {ReviewOnlyMatches:N0} · 来源确认覆盖 {SourceCoverage:0.#}%";
		public string ActionLabel => WholeSourceEligible ? "可整合集合" : "仅处理匹配资源";
		public string ActionHint => WholeSourceEligible
			? "来源确认覆盖率 ≥ 90% 且没有待复核资源，可在“安全整合”中补入来源独有资源。"
			: "不会因为这个关系移动来源目录中的其他未匹配文件。";

		internal static bool ChooseTargetIsA(PikPakFolderCoverageOption option) {
			bool aSubset = option.CoverageA >= MainWindowVM.WholeSourceCoverageThreshold;
			bool bSubset = option.CoverageB >= MainWindowVM.WholeSourceCoverageThreshold;
			if (aSubset != bSubset)
				return !aSubset; // the >=90% side is source, the other side is target

			if (!aSubset && !bSubset && Math.Abs(option.CoverageA - option.CoverageB) > 0.0001)
				return option.CoverageA > option.CoverageB; // stronger relationship density = collection

			if (option.EstimatedResourcesA != option.EstimatedResourcesB)
				return option.EstimatedResourcesA > option.EstimatedResourcesB;
			if (option.TotalFilesA != option.TotalFilesB)
				return option.TotalFilesA > option.TotalFilesB;
			return option.SuggestedTargetIsA;
		}
	}

	/// <summary>
	/// Same-folder-only or otherwise cross-folder-unassigned groups are never hidden in the
	/// resource view. They live under this catch-all header and retain the classic rows.
	/// </summary>
	public sealed class ResourceUnassignedHeader {
		public ResourceUnassignedHeader(IReadOnlyList<ResultsGroupHeader> groups) {
			GroupCount = groups.Count;
			FileCount = groups.Sum(g => g.FileCount);
			TotalBytes = groups.Sum(g => g.TotalBytes);
		}
		public int GroupCount { get; }
		public int FileCount { get; }
		public long TotalBytes { get; }
		public string Title => "其他相似文件组";
		public string Summary => $"{GroupCount:N0} 组 · {FileCount:N0} 文件 · {TotalBytes.BytesToString()} · 未形成可用的跨目录整合方向";
	}

	public sealed class ResourceResultsBuildResult {
		public required List<object> Rows { get; init; }
		public required int RelationCount { get; init; }
		public required int AssignedGroupCount { get; init; }
		public required int UnassignedGroupCount { get; init; }
	}

	/// <summary>
	/// Builds a resource-oriented presentation from the already-built canonical VDF groups.
	/// Each GroupId is assigned to at most ONE relationship (the strongest relationship in
	/// the supplied option order), preventing the same actionable file row from appearing in
	/// several folder-pair buckets. Nothing is deleted, moved or re-matched here.
	/// </summary>
	public static class ResourceResultsBuilder {
		public static ResourceResultsBuildResult Build(
			IReadOnlyList<ResultsGroupHeader> canonicalGroups,
			IReadOnlyList<PikPakFolderCoverageOption> options,
			IReadOnlySet<DuplicateItemVM>? expandedDetails = null) {
			var byId = canonicalGroups.ToDictionary(g => g.GroupId);
			var assigned = new HashSet<Guid>();
			var rows = new List<object>();
			int relations = 0;

			foreach (var option in options) {
				var gids = option.Matches
					.Select(m => m.GroupId)
					.Distinct()
					.Where(gid => byId.ContainsKey(gid) && assigned.Add(gid))
					.ToList();
				if (gids.Count == 0)
					continue;

				relations++;
				rows.Add(new ResourceRelationHeader(option, gids.Count));
				foreach (var gid in gids)
					AppendCanonicalGroup(rows, byId[gid], expandedDetails);
			}

			var unassigned = canonicalGroups.Where(g => !assigned.Contains(g.GroupId)).ToList();
			if (unassigned.Count > 0) {
				rows.Add(new ResourceUnassignedHeader(unassigned));
				foreach (var group in unassigned)
					AppendCanonicalGroup(rows, group, expandedDetails);
			}

			return new ResourceResultsBuildResult {
				Rows = rows,
				RelationCount = relations,
				AssignedGroupCount = assigned.Count,
				UnassignedGroupCount = unassigned.Count,
			};
		}

		static void AppendCanonicalGroup(
			List<object> rows,
			ResultsGroupHeader header,
			IReadOnlySet<DuplicateItemVM>? expandedDetails) {
			rows.Add(header);
			if (header.IsCollapsed)
				return;
			foreach (var row in header.Rows) {
				rows.Add(row);
				if (expandedDetails?.Contains(row.Item) == true)
					rows.Add(new ResultsDetailsRow(row));
			}
		}
	}
}
