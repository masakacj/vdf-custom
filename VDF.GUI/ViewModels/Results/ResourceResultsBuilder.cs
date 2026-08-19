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
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public enum ResultsDisplayMode {
		SimilarityGroups = 0,
		ResourceConsolidation = 1,
	}

	public sealed record ResultsDisplayModeOption(string Name, ResultsDisplayMode Mode);

	/// <summary>
	/// First row of either results view. Resource mode also exposes the persisted minimum
	/// bilateral folder-overlap threshold so the user can tune series-folder grouping live.
	/// </summary>
	public sealed class ResultsViewSwitcherRow : ReactiveObject {
		readonly Action<ResultsDisplayMode> onChanged;
		readonly Action<double>? onFolderMatchThresholdChanged;
		ResultsDisplayModeOption _Selected;
		double _FolderMatchThresholdPercent;

		public ResultsViewSwitcherRow(
			IReadOnlyList<ResultsDisplayModeOption> options,
			ResultsDisplayMode selected,
			Action<ResultsDisplayMode> onChanged,
			double? folderMatchThresholdPercent = null,
			Action<double>? onFolderMatchThresholdChanged = null) {
			Options = options;
			this.onChanged = onChanged;
			this.onFolderMatchThresholdChanged = onFolderMatchThresholdChanged;
			_Selected = options.FirstOrDefault(o => o.Mode == selected) ?? options[0];
			_FolderMatchThresholdPercent = Math.Clamp(
				folderMatchThresholdPercent ?? ResourceFolderMatchPreference.MinimumPercent, 0d, 100d);
		}

		public IReadOnlyList<ResultsDisplayModeOption> Options { get; }
		public ResultsDisplayModeOption Selected {
			get => _Selected;
			set {
				if (value == null || value.Mode == _Selected.Mode) return;
				this.RaiseAndSetIfChanged(ref _Selected, value);
				this.RaisePropertyChanged(nameof(ShowFolderMatchThreshold));
				this.RaisePropertyChanged(nameof(Hint));
				onChanged(value.Mode);
			}
		}

		public double FolderMatchThresholdPercent {
			get => _FolderMatchThresholdPercent;
			set {
				double clamped = Math.Clamp(value, 0d, 100d);
				if (Math.Abs(clamped - _FolderMatchThresholdPercent) < 0.0001d) return;
				this.RaiseAndSetIfChanged(ref _FolderMatchThresholdPercent, clamped);
				if (onFolderMatchThresholdChanged != null) {
					onFolderMatchThresholdChanged(clamped);
				}
				else {
					ResourceFolderMatchPreference.MinimumPercent = clamped;
					ApplicationHelpers.MainWindowDataContext.RefreshResultsView();
				}
			}
		}

		public bool ShowFolderMatchThreshold => Selected.Mode == ResultsDisplayMode.ResourceConsolidation;
		public string Label => "显示方式";
		public string FolderMatchLabel => "文件夹匹配 ≥";
		public string FolderMatchTip => "文件夹匹配度 = min(A目录确认覆盖率, B目录确认覆盖率)。0% 表示不额外过滤；提高阈值可排除只有少量共同资源的目录关系。";
		public string Hint => Selected.Mode == ResultsDisplayMode.SimilarityGroups
			? "传统 VDF：每个相似文件组独立显示。"
			: "文件夹整合：按同系列目录组织；只有质量信号存在唯一、无冲突的明显胜者才自动 BEST，否则保留人工复核。";
	}

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
		internal double MatchPercent => Option.FolderMatchPercent;
		internal bool WholeSourceEligible => MainWindowVM.MayMergeWholeSource(SourceCoverage, Option.AutoBestReviewOnlyGroupCount);
		internal HashSet<Guid> GroupIds => Option.Matches.Select(match => match.GroupId).ToHashSet();
	}

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

			sourceRelations = relations
				.GroupBy(r => r.SourceFolder, StringComparer.OrdinalIgnoreCase)
				.Select(g => g.OrderByDescending(r => r.MatchPercent)
					.ThenByDescending(r => r.SourceCoverage)
					.ThenByDescending(r => r.Option.ConfirmedMatchedGroupCount)
					.First())
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
			MinimumFolderMatchPercent = sourceRelations.Count == 0 ? 0d : sourceRelations.Min(r => r.MatchPercent);
			DisplayedResourceGroups = displaySet.Count;

			var reviewByGroup = new Dictionary<Guid, bool>();
			foreach (var relation in relations) {
				foreach (var match in relation.Option.Matches) {
					if (!displaySet.Contains(match.GroupId)) continue;
					if (reviewByGroup.TryGetValue(match.GroupId, out bool existing))
						reviewByGroup[match.GroupId] = existing || match.AutoBestReviewOnly;
					else
						reviewByGroup[match.GroupId] = match.AutoBestReviewOnly;
				}
			}
			ReviewOnlyMatches = reviewByGroup.Count(pair => pair.Value);
			ConfirmedMatches = reviewByGroup.Count - ReviewOnlyMatches;
			WholeSourceEligible = ReviewOnlyMatches == 0 && sourceRelations.Count > 0 && sourceRelations.All(r => r.WholeSourceEligible);
		}

		internal PikPakFolderCoverageOption Option => sourceRelations[0].Option;
		public int DisplayedResourceGroups { get; }
		public string TargetFolder { get; }
		public string SourceFolder => string.Join("；", SourceFolders);
		public IReadOnlyList<string> SourceFolders { get; }
		public int TargetFiles { get; }
		public int SourceFiles { get; }
		public long TargetBytes { get; }
		public long SourceBytes { get; }
		public int TargetResources { get; }
		public int SourceResources { get; }
		public double SourceCoverage { get; }
		public double MinimumFolderMatchPercent { get; }
		public bool WholeSourceEligible { get; }
		public int ConfirmedMatches { get; }
		public int ReviewOnlyMatches { get; }
		public int SourceFolderCount => SourceFolders.Count;

		public string DirectionLine => SourceFolderCount == 1
			? $"{TargetFolder}  ←  {SourceFolders[0]}"
			: $"系列文件夹：{TargetFolder}  ←  {SourceFolderCount:N0} 个候选副本目录";
		public string TargetStats => $"目标：{TargetFiles:N0} 文件 · {TargetBytes.BytesToString()} · 约 {TargetResources:N0} 资源";
		public string SourceStats => SourceFolderCount == 1
			? $"来源：{sourceRelations[0].SourceFolder} · {sourceRelations[0].SourceFiles:N0} 文件 · {sourceRelations[0].SourceBytes.BytesToString()} · 约 {sourceRelations[0].SourceResources:N0} 资源 · 文件夹匹配 {sourceRelations[0].MatchPercent:0.#}% · 来源覆盖 {sourceRelations[0].SourceCoverage:0.#}%"
			: "来源副本：" + string.Join("；", sourceRelations.Select(r =>
				$"{r.SourceFolder}（{r.SourceFiles:N0} 文件 / {r.SourceBytes.BytesToString()} / 匹配 {r.MatchPercent:0.#}% / 来源覆盖 {r.SourceCoverage:0.#}%）"));
		public string RelationStats =>
			$"本系列归入 {DisplayedResourceGroups:N0} 个相似资源组 · 最低文件夹匹配 {MinimumFolderMatchPercent:0.#}% · 明确 BEST {ConfirmedMatches:N0} · 人工复核 {ReviewOnlyMatches:N0}";
		public string ActionLabel => ReviewOnlyMatches > 0
			? "含人工复核"
			: WholeSourceEligible ? "可整合集合" : "可处理匹配资源";
		public string ActionHint => ReviewOnlyMatches > 0
			? "质量指标打平、互有胜负或关键元数据不足时不会自动选 BEST；这些资源保留原样供手动决定。"
			: WholeSourceEligible
				? "所有来源目录确认覆盖率 ≥ 90%、且每个资源都有无冲突的唯一质量胜者，可逐个来源执行安全整合。"
				: "仅处理已经匹配且存在明确质量胜者的资源；不会因为文件夹属于同一系列就移动其他未匹配文件。";

		internal static bool ChooseTargetIsA(PikPakFolderCoverageOption option) {
			bool aSubset = option.CoverageA >= MainWindowVM.WholeSourceCoverageThreshold;
			bool bSubset = option.CoverageB >= MainWindowVM.WholeSourceCoverageThreshold;
			if (aSubset != bSubset)
				return !aSubset;
			if (!aSubset && !bSubset && Math.Abs(option.CoverageA - option.CoverageB) > 0.0001)
				return option.CoverageA > option.CoverageB;
			if (option.EstimatedResourcesA != option.EstimatedResourcesB)
				return option.EstimatedResourcesA > option.EstimatedResourcesB;
			if (option.TotalFilesA != option.TotalFilesB)
				return option.TotalFilesA > option.TotalFilesB;
			return option.SuggestedTargetIsA;
		}
	}

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
		public string Summary => $"{GroupCount:N0} 组 · {FileCount:N0} 文件 · {TotalBytes.BytesToString()} · 未达到当前文件夹匹配阈值，或未形成可用的跨目录系列关系";
	}

	public sealed class ResourceResultsBuildResult {
		public required List<object> Rows { get; init; }
		public required int RelationCount { get; init; }
		public required int AssignedGroupCount { get; init; }
		public required int UnassignedGroupCount { get; init; }
	}

	public static class ResourceResultsBuilder {
		public static ResourceResultsBuildResult Build(
			IReadOnlyList<ResultsGroupHeader> canonicalGroups,
			IReadOnlyList<PikPakFolderCoverageOption> options,
			IReadOnlySet<DuplicateItemVM>? expandedDetails = null,
			double minimumFolderMatchPercent = double.NaN) {
			var byId = canonicalGroups.ToDictionary(g => g.GroupId);
			var assigned = new HashSet<Guid>();
			var rows = new List<object>();
			int representedRelations = 0;
			double threshold = double.IsNaN(minimumFolderMatchPercent)
				? ResourceFolderMatchPreference.MinimumPercent
				: Math.Clamp(minimumFolderMatchPercent, 0d, 100d);

			var directed = options
				.Where(option => option.FolderMatchPercent + 0.0001d >= threshold)
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
				foreach (var coherentRelations in SplitBySharedResourceEvidence(targetGroup.Relations)) {
					var gids = new List<Guid>();
					var localSeen = new HashSet<Guid>();
					foreach (var relation in coherentRelations) {
						foreach (var match in relation.Option.Matches) {
							Guid gid = match.GroupId;
							if (!byId.ContainsKey(gid) || assigned.Contains(gid) || !localSeen.Add(gid))
								continue;
							gids.Add(gid);
						}
					}
					if (gids.Count == 0)
						continue;

					var gidSet = gids.ToHashSet();
					var usedRelations = coherentRelations
						.Where(relation => relation.Option.Matches.Any(match => gidSet.Contains(match.GroupId)))
						.ToList();
					if (usedRelations.Count == 0)
						continue;

					assigned.UnionWith(gids);
					representedRelations += usedRelations.Count;
					rows.Add(new ResourceRelationHeader(usedRelations, gids));
					foreach (var gid in gids.OrderBy(gid => byId[gid].GroupNumber))
						AppendCanonicalGroup(rows, byId[gid], expandedDetails);
				}
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

		internal static IReadOnlyList<IReadOnlyList<ResourceDirectedRelation>> SplitBySharedResourceEvidence(
			IReadOnlyList<ResourceDirectedRelation> relations) {
			if (relations.Count <= 1)
				return new[] { relations };

			var ids = relations.Select(relation => relation.GroupIds).ToList();
			var visited = new bool[relations.Count];
			var components = new List<IReadOnlyList<ResourceDirectedRelation>>();
			for (int seed = 0; seed < relations.Count; seed++) {
				if (visited[seed]) continue;
				var stack = new Stack<int>();
				var indexes = new List<int>();
				stack.Push(seed);
				visited[seed] = true;
				while (stack.Count > 0) {
					int current = stack.Pop();
					indexes.Add(current);
					for (int candidate = 0; candidate < relations.Count; candidate++) {
						if (visited[candidate] || !ids[current].Overlaps(ids[candidate]))
							continue;
						visited[candidate] = true;
						stack.Push(candidate);
					}
				}
				indexes.Sort();
				components.Add(indexes.Select(index => relations[index]).ToList());
			}
			return components;
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
