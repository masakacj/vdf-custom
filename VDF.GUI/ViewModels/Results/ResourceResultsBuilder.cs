// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Linq;
using System.Reactive;
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
	/// First row of either results view. Resource mode exposes the persisted bilateral
	/// folder-overlap threshold and the action for explicitly selected series roots.
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
			ResourceSeriesSelectionSession.AttachSwitcher(this);
		}

		public IReadOnlyList<ResultsDisplayModeOption> Options { get; }
		public ResultsDisplayModeOption Selected {
			get => _Selected;
			set {
				if (value == null || value.Mode == _Selected.Mode) return;
				this.RaiseAndSetIfChanged(ref _Selected, value);
				this.RaisePropertyChanged(nameof(ShowFolderMatchThreshold));
				this.RaisePropertyChanged(nameof(ShowConsolidateControls));
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
		public bool ShowConsolidateControls => ShowFolderMatchThreshold;
		public int SelectedSeriesCount => ResourceSeriesSelectionSession.SelectedCount;
		public bool CanConsolidate => SelectedSeriesCount > 0;
		public string SelectedSeriesText => SelectedSeriesCount == 0 ? "未选择文件夹组" : $"已选 {SelectedSeriesCount:N0} 个文件夹组";
		public ReactiveCommand<Unit, Unit> ConsolidateSelectedCommand =>
			ReactiveCommand.CreateFromTask(ResourceSeriesSelectionSession.ConsolidateSelectedAsync);

		internal void RefreshSeriesSelection() {
			this.RaisePropertyChanged(nameof(SelectedSeriesCount));
			this.RaisePropertyChanged(nameof(CanConsolidate));
			this.RaisePropertyChanged(nameof(SelectedSeriesText));
		}

		public string Label => "显示方式";
		public string FolderMatchLabel => "文件夹匹配 ≥";
		public string FolderMatchTip => "文件夹匹配度 = min(A目录确认覆盖率, B目录确认覆盖率)。一级显示文件夹重复组；展开后资源始终按实际所在文件夹分组，不再切回传统相似 Group。";
		public string Hint => Selected.Mode == ResultsDisplayMode.SimilarityGroups
			? "传统 VDF：每个相似文件组独立显示。"
			: "文件夹合并：一级看重复目录关系；展开后按实际文件夹连续查看参与资源和 BEST。";
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
		internal double TargetCoverage => TargetIsA ? Option.CoverageA : Option.CoverageB;
		internal double SourceCoverage => TargetIsA ? Option.CoverageB : Option.CoverageA;
		internal double MatchPercent => Option.FolderMatchPercent;
		internal bool WholeSourceEligible => MainWindowVM.MayMergeWholeSource(SourceCoverage, Option.AutoBestReviewOnlyGroupCount);
		internal HashSet<Guid> GroupIds => Option.Matches.Select(match => match.GroupId).ToHashSet();
	}

	/// <summary>
	/// Relation-root metadata. This is retained on ResourceRelationHeader for planning,
	/// but the expanded list now renders ResourceFolderContentHeader for each actual
	/// containing folder instead of rendering these roots as standalone rows.
	/// </summary>
	public sealed class ResourceFolderMemberRow {
		internal ResourceFolderMemberRow(bool isTarget, string path, int files, long bytes, int resources,
			double coverage, double matchPercent) {
			IsTarget = isTarget;
			Path = path;
			Files = files;
			Bytes = Math.Max(0, bytes);
			Resources = resources;
			Coverage = coverage;
			MatchPercent = matchPercent;
		}

		public bool IsTarget { get; }
		public string RoleLabel => IsTarget ? "目标" : "来源";
		public string Path { get; }
		public int Files { get; }
		public long Bytes { get; }
		public int Resources { get; }
		public double Coverage { get; }
		public double MatchPercent { get; }
		public string Meta => $"{Files:N0} 文件 · {Bytes.BytesToString()} · 约 {Resources:N0} 资源";
		public string RelationText => IsTarget
			? $"确认覆盖 {Coverage:0.#}%"
			: $"确认覆盖 {Coverage:0.#}% · 双向重叠 {MatchPercent:0.#}%";
	}

	/// <summary>
	/// Visible level-2 folder header in resource-consolidation mode. Traditional
	/// ResultsGroupHeader remains internal context only; every file row is emitted below
	/// the actual folder that contains it.
	/// </summary>
	public sealed class ResourceFolderContentHeader {
		internal ResourceFolderContentHeader(
			string roleLabel,
			string path,
			string relationRoot,
			IReadOnlyList<ResultsItemRow> itemRows) {
			RoleLabel = roleLabel;
			Path = path;
			RelationRoot = relationRoot;
			FileCount = itemRows.Count;
			TotalBytes = itemRows.Sum(row => Math.Max(0, row.Item.ItemInfo.SizeLong));
			ResourceGroupCount = itemRows.Select(row => row.Group.GroupId).Distinct().Count();
			RecommendedBestCount = itemRows.Count(row => row.IsBest);
			ConfirmedBestCount = itemRows.Count(row => row.IsBestConfirmed);
			ReviewBestCount = itemRows.Count(row => row.IsBestNeedsReview);
			HasDifferentRelationRoot = !MainWindowVM.NormalizePikPakPath(path)
				.Equals(MainWindowVM.NormalizePikPakPath(relationRoot), StringComparison.OrdinalIgnoreCase);
		}

		public string RoleLabel { get; }
		public string Path { get; }
		public string RelationRoot { get; }
		public int FileCount { get; }
		public long TotalBytes { get; }
		public int ResourceGroupCount { get; }
		public int RecommendedBestCount { get; }
		public int ConfirmedBestCount { get; }
		public int ReviewBestCount { get; }
		public bool HasDifferentRelationRoot { get; }
		public string Meta => $"{FileCount:N0} 个参与文件 · {TotalBytes.BytesToString()} · 涉及 {ResourceGroupCount:N0} 个重复资源";
		public string BestMeta => $"BEST {RecommendedBestCount:N0} · 确认 {ConfirmedBestCount:N0} · 待复核 {ReviewBestCount:N0}";
		public string RootHint => HasDifferentRelationRoot ? $"所属比较根目录：{RelationRoot}" : string.Empty;
	}

	public sealed class ResourceRelationHeader : ReactiveObject {
		readonly IReadOnlyList<ResourceDirectedRelation> sourceRelations;
		readonly HashSet<Guid> displayedGroupIds;
		bool _IsSelected;
		bool _IsExpanded;

		internal ResourceRelationHeader(
			IReadOnlyList<ResourceDirectedRelation> relations,
			IReadOnlyCollection<Guid> displayedGroupIds) {
			if (relations == null || relations.Count == 0)
				throw new ArgumentException("At least one folder relation is required.", nameof(relations));

			this.displayedGroupIds = new HashSet<Guid>(displayedGroupIds);
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
			SelectionKey = NormalizeSelectionKey(TargetFolder, SourceFolders);
			TargetFiles = relations.Max(r => r.TargetFiles);
			TargetBytes = relations.Max(r => r.TargetBytes);
			TargetResources = relations.Max(r => r.TargetResources);
			SourceFiles = sourceRelations.Sum(r => r.SourceFiles);
			SourceBytes = sourceRelations.Sum(r => r.SourceBytes);
			SourceResources = sourceRelations.Sum(r => r.SourceResources);
			TargetCoverage = sourceRelations.Count == 0 ? 0d : sourceRelations.Min(r => r.TargetCoverage);
			SourceCoverage = sourceRelations.Count == 0 ? 0d : sourceRelations.Min(r => r.SourceCoverage);
			MinimumFolderMatchPercent = sourceRelations.Count == 0 ? 0d : sourceRelations.Min(r => r.MatchPercent);
			DisplayedResourceGroups = this.displayedGroupIds.Count;
			RelationMatchedResourceGroups = sourceRelations
				.SelectMany(r => r.Option.Matches)
				.Select(match => match.GroupId)
				.Distinct()
				.Count();

			var reviewByGroup = new Dictionary<Guid, bool>();
			foreach (var relation in relations) {
				foreach (var match in relation.Option.Matches) {
					if (!this.displayedGroupIds.Contains(match.GroupId)) continue;
					if (reviewByGroup.TryGetValue(match.GroupId, out bool existing))
						reviewByGroup[match.GroupId] = existing || match.AutoBestReviewOnly;
					else
						reviewByGroup[match.GroupId] = match.AutoBestReviewOnly;
				}
			}
			ReviewOnlyMatches = reviewByGroup.Count(pair => pair.Value);
			ConfirmedMatches = reviewByGroup.Count - ReviewOnlyMatches;
			WholeSourceEligible = ReviewOnlyMatches == 0 && sourceRelations.Count > 0 && sourceRelations.All(r => r.WholeSourceEligible);

			var folderRows = new List<ResourceFolderMemberRow> {
				new(true, TargetFolder, TargetFiles, TargetBytes, TargetResources, TargetCoverage, MinimumFolderMatchPercent),
			};
			folderRows.AddRange(sourceRelations.Select(r => new ResourceFolderMemberRow(
				false, r.SourceFolder, r.SourceFiles, r.SourceBytes, r.SourceResources, r.SourceCoverage, r.MatchPercent)));
			FolderRows = folderRows;
		}

		internal PikPakFolderCoverageOption Option => sourceRelations[0].Option;
		internal IReadOnlyList<ResourceDirectedRelation> SourceRelations => sourceRelations;
		internal IReadOnlyCollection<Guid> DisplayedGroupIds => displayedGroupIds;
		internal string SelectionKey { get; }
		public int DisplayedResourceGroups { get; }
		public int RelationMatchedResourceGroups { get; }
		public string TargetFolder { get; }
		public string SourceFolder => string.Join("；", SourceFolders);
		public IReadOnlyList<string> SourceFolders { get; }
		public IReadOnlyList<ResourceFolderMemberRow> FolderRows { get; }
		public int TargetFiles { get; }
		public int SourceFiles { get; }
		public long TargetBytes { get; }
		public long SourceBytes { get; }
		public int TargetResources { get; }
		public int SourceResources { get; }
		public double TargetCoverage { get; }
		public double SourceCoverage { get; }
		public double MinimumFolderMatchPercent { get; }
		public bool WholeSourceEligible { get; }
		public int ConfirmedMatches { get; }
		public int ReviewOnlyMatches { get; }
		public int SourceFolderCount => SourceFolders.Count;
		public int FolderCount => SourceFolderCount + 1;
		public int TotalComparedFiles => TargetFiles + SourceFiles;
		public long TotalComparedBytes => Math.Max(0, TargetBytes) + Math.Max(0, SourceBytes);

		public bool IsSelected {
			get => _IsSelected;
			set {
				if (value == _IsSelected) return;
				this.RaiseAndSetIfChanged(ref _IsSelected, value);
				ResourceSeriesSelectionSession.SetSelected(this, value);
			}
		}

		public bool IsExpanded {
			get => _IsExpanded;
			set {
				if (value == _IsExpanded) return;
				this.RaiseAndSetIfChanged(ref _IsExpanded, value);
				this.RaisePropertyChanged(nameof(ExpandGlyph));
				this.RaisePropertyChanged(nameof(ExpandActionText));
				ResourceSeriesSelectionSession.SetExpanded(this, value);
			}
		}

		internal void SetSelectedFromSession(bool value) {
			if (value == _IsSelected) return;
			this.RaiseAndSetIfChanged(ref _IsSelected, value, nameof(IsSelected));
		}

		internal void SetExpandedFromSession(bool value) {
			if (value == _IsExpanded) return;
			this.RaiseAndSetIfChanged(ref _IsExpanded, value, nameof(IsExpanded));
			this.RaisePropertyChanged(nameof(ExpandGlyph));
			this.RaisePropertyChanged(nameof(ExpandActionText));
		}

		public string ExpandGlyph => IsExpanded ? "▾" : "▸";
		public string ExpandActionText => IsExpanded ? "收起文件夹资源" : "展开文件夹资源";
		public ReactiveCommand<Unit, Unit> ToggleExpandedCommand => ReactiveCommand.Create(() => {
			IsExpanded = !IsExpanded;
			ApplicationHelpers.MainWindowDataContext.RefreshResultsView();
		});

		public string TargetRoleLabel => "建议目标";
		public string SourceRoleLabel => SourceFolderCount == 1 ? "来源副本" : $"{SourceFolderCount:N0} 个来源副本";
		public string TargetMeta => $"{TargetFiles:N0} 文件 · {TargetBytes.BytesToString()} · 约 {TargetResources:N0} 资源";
		public string SourceMeta => $"{SourceFiles:N0} 文件 · {SourceBytes.BytesToString()} · 约 {SourceResources:N0} 资源";
		public string OverlapText => $"{MinimumFolderMatchPercent:0.#}%";
		public string OverlapCaption => $"双向重叠 · 当前 {DisplayedResourceGroups:N0} 组";
		public string CoverageText => $"目标覆盖 {TargetCoverage:0.#}% · 来源覆盖 {SourceCoverage:0.#}%";
		public string BestReadinessText => $"确认 BEST {ConfirmedMatches:N0} · 待复核 {ReviewOnlyMatches:N0}";
		public string HierarchySummary =>
			$"{FolderCount:N0} 个文件夹 · {TotalComparedFiles:N0} 文件 · {TotalComparedBytes.BytesToString()} · 双向重叠 {MinimumFolderMatchPercent:0.#}%";
		public string VisibleGroupSummary => RelationMatchedResourceGroups == DisplayedResourceGroups
			? $"匹配资源 {DisplayedResourceGroups:N0} 组"
			: $"匹配资源 {RelationMatchedResourceGroups:N0} 组 · 当前显示 {DisplayedResourceGroups:N0} 组";
		public ReactiveCommand<Unit, Unit> PreviewConsolidationCommand =>
			ReactiveCommand.CreateFromTask(() => ApplicationHelpers.MainWindowDataContext
				.ConsolidateSelectedResourceSeriesInteractiveAsync(new[] { this }));

		public string DirectionLine => SourceFolderCount == 1
			? $"{TargetFolder}  ←  {SourceFolders[0]}"
			: $"{TargetFolder}  ←  {SourceFolderCount:N0} 个来源目录";
		public string TargetStats => $"目标树：{TargetFiles:N0} 文件 · {TargetBytes.BytesToString()} · 约 {TargetResources:N0} 资源";
		public string SourceStats => SourceFolderCount == 1
			? $"来源树：{sourceRelations[0].SourceFolder} · {sourceRelations[0].SourceFiles:N0} 文件 · {sourceRelations[0].SourceBytes.BytesToString()} · 约 {sourceRelations[0].SourceResources:N0} 资源 · 文件夹匹配 {sourceRelations[0].MatchPercent:0.#}% · 来源覆盖 {sourceRelations[0].SourceCoverage:0.#}%"
			: "来源副本：" + string.Join("；", sourceRelations.Select(r =>
				$"{r.SourceFolder}（{r.SourceFiles:N0} 文件 / {r.SourceBytes.BytesToString()} / 匹配 {r.MatchPercent:0.#}% / 来源覆盖 {r.SourceCoverage:0.#}%）"));
		public string RelationStats =>
			$"本系列归入 {DisplayedResourceGroups:N0} 个相似资源 · 最低文件夹匹配 {MinimumFolderMatchPercent:0.#}% · 确认 BEST {ConfirmedMatches:N0} · 推荐 BEST待复核 {ReviewOnlyMatches:N0}";
		public string ActionLabel => ReviewOnlyMatches > 0
			? "含待复核"
			: WholeSourceEligible ? "可自动合并" : "可处理匹配项";
		public string ActionHint => ReviewOnlyMatches > 0
			? "展开后按实际文件夹查看资源；待复核项可在合并预览中接受推荐 BEST 或改选 keeper。"
			: WholeSourceEligible
				? "来源树确认覆盖率 ≥ 90%，可连同来源独有的已索引媒体一起整合；子目录结构保留。"
				: "只自动处理确认 BEST；路径冲突不覆盖、不自动改名。";

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

		static string NormalizeSelectionKey(string target, IEnumerable<string> sources) =>
			MainWindowVM.NormalizePikPakPath(target) + "\0" + string.Join("\0", sources
				.Select(MainWindowVM.NormalizePikPakPath)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
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
		public string Title => "其他目录中的相似资源";
		public string Summary => $"{GroupCount:N0} 个重复资源 · {FileCount:N0} 文件 · {TotalBytes.BytesToString()} · 未形成当前阈值下的文件夹重复关系；下面仍按实际文件夹分组显示";
	}

	public sealed class ResourceResultsBuildResult {
		public required List<object> Rows { get; init; }
		public required int RelationCount { get; init; }
		public required int AssignedGroupCount { get; init; }
		public required int UnassignedGroupCount { get; init; }
	}

	public static class ResourceResultsBuilder {
		sealed class FolderBucket {
			internal required string Path { get; init; }
			internal required string RoleLabel { get; init; }
			internal required string RelationRoot { get; init; }
			internal required int RootOrder { get; init; }
			internal List<ResultsItemRow> Rows { get; } = new();
		}

		public static ResourceResultsBuildResult Build(
			IReadOnlyList<ResultsGroupHeader> canonicalGroups,
			IReadOnlyList<PikPakFolderCoverageOption> options,
			IReadOnlySet<DuplicateItemVM>? expandedDetails = null,
			double minimumFolderMatchPercent = double.NaN) {
			ResourceSeriesSelectionSession.BeginBuild();
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
					var header = new ResourceRelationHeader(usedRelations, gids);
					ResourceSeriesSelectionSession.Register(header);
					rows.Add(header);

					// Folder-consolidation mode is permanently folder-first. A traditional
					// ResultsGroupHeader is never emitted here: once the level-1 relationship is
					// expanded, every participating ResultsItemRow lives beneath its ACTUAL
					// containing folder. GroupId remains only as invisible matching/action context.
					if (header.IsExpanded)
						AppendFolderGroupedRows(rows, header, gids, byId, expandedDetails);
				}
			}

			var unassigned = canonicalGroups.Where(g => !assigned.Contains(g.GroupId)).ToList();
			if (unassigned.Count > 0) {
				rows.Add(new ResourceUnassignedHeader(unassigned));
				AppendUnassignedFolderGroupedRows(rows, unassigned, expandedDetails);
			}
			ResourceSeriesSelectionSession.FinishBuild();

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

		static void AppendFolderGroupedRows(
			List<object> output,
			ResourceRelationHeader header,
			IReadOnlyList<Guid> groupIds,
			IReadOnlyDictionary<Guid, ResultsGroupHeader> byId,
			IReadOnlySet<DuplicateItemVM>? expandedDetails) {
			var roots = header.FolderRows
				.Select((folder, index) => new { Folder = folder, Index = index })
				.ToList();
			var buckets = new Dictionary<string, FolderBucket>(StringComparer.OrdinalIgnoreCase);

			foreach (Guid gid in groupIds.OrderBy(gid => byId[gid].GroupNumber)) {
				foreach (ResultsItemRow row in byId[gid].Rows) {
					string actualFolder = ItemFolder(row.Item);
					var root = roots
						.Where(candidate => MainWindowVM.PikPakPathIsWithin(actualFolder, candidate.Folder.Path))
						.OrderByDescending(candidate => MainWindowVM.PikPakPathDepth(candidate.Folder.Path))
						.ThenBy(candidate => candidate.Index)
						.FirstOrDefault();
					string role = root?.Folder.RoleLabel ?? "关联";
					string relationRoot = root?.Folder.Path ?? actualFolder;
					int rootOrder = root?.Index ?? int.MaxValue;
					string key = MainWindowVM.NormalizePikPakPath(actualFolder);
					if (!buckets.TryGetValue(key, out FolderBucket? bucket)) {
						bucket = new FolderBucket {
							Path = actualFolder,
							RoleLabel = role,
							RelationRoot = relationRoot,
							RootOrder = rootOrder,
						};
						buckets[key] = bucket;
					}
					bucket.Rows.Add(row);
				}
			}

			foreach (FolderBucket bucket in buckets.Values
				.OrderBy(bucket => bucket.RootOrder)
				.ThenBy(bucket => MainWindowVM.PikPakPathDepth(bucket.Path))
				.ThenBy(bucket => bucket.Path, StringComparer.OrdinalIgnoreCase)) {
				AppendFolderBucket(output, bucket, expandedDetails);
			}
		}

		static void AppendUnassignedFolderGroupedRows(
			List<object> output,
			IReadOnlyList<ResultsGroupHeader> groups,
			IReadOnlySet<DuplicateItemVM>? expandedDetails) {
			var buckets = new Dictionary<string, FolderBucket>(StringComparer.OrdinalIgnoreCase);
			foreach (ResultsGroupHeader group in groups.OrderBy(group => group.GroupNumber)) {
				foreach (ResultsItemRow row in group.Rows) {
					string folder = ItemFolder(row.Item);
					string key = MainWindowVM.NormalizePikPakPath(folder);
					if (!buckets.TryGetValue(key, out FolderBucket? bucket)) {
						bucket = new FolderBucket {
							Path = folder,
							RoleLabel = "其他",
							RelationRoot = folder,
							RootOrder = int.MaxValue,
						};
						buckets[key] = bucket;
					}
					bucket.Rows.Add(row);
				}
			}

			foreach (FolderBucket bucket in buckets.Values.OrderBy(bucket => bucket.Path, StringComparer.OrdinalIgnoreCase))
				AppendFolderBucket(output, bucket, expandedDetails);
		}

		static void AppendFolderBucket(
			List<object> output,
			FolderBucket bucket,
			IReadOnlySet<DuplicateItemVM>? expandedDetails) {
			output.Add(new ResourceFolderContentHeader(bucket.RoleLabel, bucket.Path, bucket.RelationRoot, bucket.Rows));
			foreach (ResultsItemRow row in bucket.Rows
				.OrderBy(row => row.Group.GroupNumber)
				.ThenByDescending(row => row.IsBest)
				.ThenBy(row => row.Item.ItemInfo.Path, StringComparer.OrdinalIgnoreCase)) {
				output.Add(row);
				if (expandedDetails?.Contains(row.Item) == true)
					output.Add(new ResultsDetailsRow(row));
			}
		}

		static string ItemFolder(DuplicateItemVM item) {
			if (!string.IsNullOrWhiteSpace(item.ItemInfo.Folder))
				return item.ItemInfo.Folder;
			return MainWindowVM.GetPikPakFolder(item.ItemInfo.Path);
		}
	}
}