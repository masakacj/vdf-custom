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
			: "文件夹整合：以目标系列文件夹为一级组，把多个候选副本目录放在一起；待复核资源保持人工处理。";
	}

	/// <summary>
	/// One folder-pair option after resolving its safer direction. Keeping this directional
	/// object separate lets the presentation merge A←B and A←C into one series-folder group
	/// without ever turning the graph into an undirected connected component (which could let
	/// a generic Misc/Download folder bridge two unrelated series).
	/// </summary>
	internal sealed class ResourceDirectedRelation {
		internal ResourceDirectedRelation(PikPakFolderCoverageOption option) {
			Option = option;
			TargetIsA = ResourceRelationHeader.ChooseTargetIsA(option);
		}

		internal PikPakFolderCoverageOption Option { get; }
		internal bool TargetIsA { get; }
		internal string TargetFolder => TargetIsA ? Option.FolderA : Option.FolderB;
		internal string SourceFolder => TargetIsA ? Option.FolderB : Option.FolderA;
		internal int TargetFiles => TargetIsA ? Option.TotalFilesA : Option.TotalFilesB;
		internal int SourceFiles => TargetIsA ? Option.TotalFilesB : Option.TotalFilesA;
		internal long TargetBytes => TargetIsA ? Option.TotalBytesA : Option.TotalBytesB;
		internal long SourceBytes => TargetIsA ? Option.TotalBytesB : Option.TotalBytesA;
		internal int TargetResources => TargetIsA ? Option.EstimatedResourcesA : Option.EstimatedResourcesB;
		internal int SourceResources => TargetIsA ? Option.EstimatedResourcesB : Option.EstimatedResourcesA;
		internal double SourceCoverage => TargetIsA ? Option.CoverageB : Option.CoverageA;
		internal bool WholeSourceEligible => MainWindowVM.MayMergeWholeSource(SourceCoverage, Option.ReviewOnlyGroupCount);
	}

	/// <summary>
	/// Top-level header for one target series folder. Multiple source/copy folders that all
	/// resolve toward the same target are deliberately combined here so the user can review
	/// one series at a time instead of bouncing between A↔B, A↔C pair buckets.
	/// </summary>
	public sealed class ResourceRelationHeader {
		readonly IReadOnlyList<ResourceDirectedRelation> sourceRelations;

		internal ResourceRelationHeader(
			IReadOnlyList<ResourceDirectedRelation> relations,
			IReadOnlyCollection<Guid> displayedGroupIds) {
			if (relations == null || relations.Count == 0)
				throw new ArgumentException("At least one folder relation is required.", nameof(relations));

			var displaySet = new HashSet<Guid>(displayedGroupIds);
			var targetFolder = relations[0].TargetFolder;
			if (relations.Any(r => !r.TargetFolder.Equals(targetFolder, StringComparison.OrdinalIgnoreCase)))
				throw new ArgumentException("All relations in a resource header must share the same target folder.", nameof(relations));

			// Defensive de-duplication: ComputePikPakFolderCoverageOptions normally emits one
			// pair per source folder, but if that ever changes the UI still shows a source once.
			sourceRelations = relations
				.GroupBy(r => r.SourceFolder, StringComparer.OrdinalIgnoreCase)
				.Select(g => g.OrderByDescending(r => r.SourceCoverage).ThenByDescending(r => r.Option.ConfirmedMatchedGroupCount).First())
				.ToList();

			TargetFolder = targetFolder;
			SourceFolders = sourceRelations.Select(r => r.SourceFolder).ToList();
			TargetFiles = relations.Max(r => r.TargetFiles);
			TargetBytes = relations.Max(r => r.TargetBytes);
			TargetResources = relations.Max(r => r.TargetResources);
			SourceFiles = sourceRelations.Sum(r => r.SourceFiles);
			SourceBytes = sourceRelations.Sum(r => r.SourceBytes);
			SourceResources = sourceRelations.Sum(r => r.SourceResources);
			SourceCoverage = sourceRelations.Count == 0 ? 0d : sourceRelations.Min(r => r.SourceCoverage);
			WholeSourceEligible = sourceRelations.Count > 0 && sourceRelations.All(r => r.WholeSourceEligible);
			DisplayedResourceGroups = displaySet.Count;

			// A group is manual-review if ANY contributing relation says so. This intentionally
			// biases toward retaining both copies rather than allowing a second, looser relation
			// to turn an ambiguous item back into an automatic BEST decision.
			var reviewByGroup = new Dictionary<Guid, bool>();
			foreach (var relation in relations) {
				foreach (var match in relation.Option.Matches) {
					if (!displaySet.Contains(match.GroupId)) continue;
					if (reviewByGroup.TryGetValue(match.GroupId, out bool existing))
						reviewByGroup[match.GroupId] = existing || match.ReviewOnly;
					else
						reviewByGroup[match.GroupId] = match.ReviewOnly;
				}
			}
			ReviewOnlyMatches = reviewByGroup.Count(pair => pair.Value);
			ConfirmedMatches = reviewByGroup.Count - ReviewOnlyMatches;
		}

		internal PikPakFolderCoverageOption Option => sourceRelations[0].Option;
		public int DisplayedResourceGroups { get; }
		public string TargetFolder { get; }
		/// <summary>Compatibility accessor: one path for one source, joined paths for a multi-source series group.</summary>
		public string SourceFolder => string.Join("；", SourceFolders);
		public IReadOnlyList<string> SourceFolders { get; }
		public int TargetFiles { get; }
		public int SourceFiles { get; }
		public long TargetBytes { get; }
		public long SourceBytes { get; }
		public int TargetResources { get; }
		public int SourceResources { get; }
		public double SourceCoverage { get; }
		public bool WholeSourceEligible { get; }
		public int ConfirmedMatches { get; }
		public int ReviewOnlyMatches { get; }
		public int SourceFolderCount => SourceFolders.Count;

		public string DirectionLine => SourceFolderCount == 1
			? $"{TargetFolder}  ←  {SourceFolders[0]}"
			: $"系列文件夹：{TargetFolder}  ←  {SourceFolderCount:N0} 个候选副本目录";
		public string TargetStats => $"目标：{TargetFiles:N0} 文件 · {TargetBytes.BytesToString()} · 约 {TargetResources:N0} 资源";
		public string SourceStats => SourceFolderCount == 1
			? $"来源：{sourceRelations[0].SourceFolder} · {sourceRelations[0].SourceFiles:N0} 文件 · {sourceRelations[0].SourceBytes.BytesToString()} · 约 {sourceRelations[0].SourceResources:N0} 资源"
			: "来源副本：" + string.Join("；", sourceRelations.Select(r =>
				$"{r.SourceFolder}（{r.SourceFiles:N0} 文件 / {r.SourceBytes.BytesToString()} / 覆盖 {r.SourceCoverage:0.#}%）"));
		public string RelationStats =>
			$"本系列归入 {DisplayedResourceGroups:N0} 个相似资源组 · 可按 BEST 规则处理 {ConfirmedMatches:N0} · 人工复核 {ReviewOnlyMatches:N0}";
		public string ActionLabel => ReviewOnlyMatches > 0
			? "含人工复核"
			: WholeSourceEligible ? "可整合集合" : "可处理匹配资源";
		public string ActionHint => ReviewOnlyMatches > 0
			? "只对安全门槛已确认的资源按 BEST 质量规则保留一份；AI/片段/版本差异等不确定组不会自动处理，留给你手动决定。"
			: WholeSourceEligible
				? "所有来源目录确认覆盖率 ≥ 90% 且没有待复核资源，可逐个来源执行安全整合并保留 BEST。"
				: "仅处理已经匹配且通过安全门槛的资源；不会因为文件夹属于同一系列就移动其他未匹配文件。";

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
		public string Summary => $"{GroupCount:N0} 组 · {FileCount:N0} 文件 · {TotalBytes.BytesToString()} · 未形成可用的跨目录系列文件夹关系";
	}

	public sealed class ResourceResultsBuildResult {
		public required List<object> Rows { get; init; }
		/// <summary>Number of directed source-folder relationships represented by the rendered target groups.</summary>
		public required int RelationCount { get; init; }
		public required int AssignedGroupCount { get; init; }
		public required int UnassignedGroupCount { get; init; }
	}

	/// <summary>
	/// Builds a target-folder-oriented presentation from the already-built canonical VDF groups.
	/// Folder pairs are directed first, then ONLY relations with the same target folder are merged.
	/// This is intentionally not a generic connected-component graph: a common Misc/Download
	/// folder may be a source for several series without merging those independent series together.
	/// Each GroupId is still rendered at most once. Nothing is deleted, moved or re-matched here.
	/// </summary>
	public static class ResourceResultsBuilder {
		public static ResourceResultsBuildResult Build(
			IReadOnlyList<ResultsGroupHeader> canonicalGroups,
			IReadOnlyList<PikPakFolderCoverageOption> options,
			IReadOnlySet<DuplicateItemVM>? expandedDetails = null) {
			var byId = canonicalGroups.ToDictionary(g => g.GroupId);
			var assigned = new HashSet<Guid>();
			var rows = new List<object>();
			int representedRelations = 0;

			var directed = options
				.Select((option, index) => new { Relation = new ResourceDirectedRelation(option), Index = index })
				.ToList();
			var targetGroups = directed
				.GroupBy(x => x.Relation.TargetFolder, StringComparer.OrdinalIgnoreCase)
				.Select(group => new {
					Relations = group.OrderBy(x => x.Index).Select(x => x.Relation).ToList(),
					FirstIndex = group.Min(x => x.Index),
				})
				.OrderBy(group => group.FirstIndex)
				.ToList();

			foreach (var targetGroup in targetGroups) {
				var gids = new List<Guid>();
				var localSeen = new HashSet<Guid>();
				foreach (var relation in targetGroup.Relations) {
					foreach (var match in relation.Option.Matches) {
						Guid gid = match.GroupId;
						if (!byId.ContainsKey(gid) || assigned.Contains(gid) || !localSeen.Add(gid))
							continue;
						gids.Add(gid);
					}
				}
				if (gids.Count == 0)
					continue;

				// Show only source relations that actually contributed at least one group to this
				// target bucket after global de-duplication.
				var gidSet = gids.ToHashSet();
				var usedRelations = targetGroup.Relations
					.Where(relation => relation.Option.Matches.Any(match => gidSet.Contains(match.GroupId)))
					.ToList();
				if (usedRelations.Count == 0)
					continue;

				assigned.UnionWith(gids);
				representedRelations += usedRelations.Count;
				rows.Add(new ResourceRelationHeader(usedRelations, gids));

				// Retain the canonical result sort inside the series folder group so BEST-first,
				// size sorting, checked-first etc. continue to behave exactly like traditional VDF.
				foreach (var gid in gids.OrderBy(gid => byId[gid].GroupNumber))
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
				RelationCount = representedRelations,
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
