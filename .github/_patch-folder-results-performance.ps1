$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $content = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $oldNorm = $Old.Replace("`r`n", "`n")
    $newNorm = $New.Replace("`r`n", "`n")
    $count = ([regex]::Matches($content, [regex]::Escape($oldNorm))).Count
    if ($count -ne 1) { throw "$Label expected exactly one match in $Path, found $count" }
    $content = $content.Replace($oldNorm, $newNorm)
    [IO.File]::WriteAllText($Path, $content, [Text.UTF8Encoding]::new($false))
    Write-Host "Patched $Label"
}

$results = 'VDF.GUI/ViewModels/MainWindowVM_Results.cs'
Replace-Exact $results @'
		internal void SetResultsDisplayMode(ResultsDisplayMode mode) {
			if (mode == ActiveResultsDisplayMode) return;
			ActiveResultsDisplayMode = mode;
			RebuildResultsList();
		}
'@ @'
		internal void SetResultsDisplayMode(ResultsDisplayMode mode) {
			if (mode == ActiveResultsDisplayMode) return;
			ActiveResultsDisplayMode = mode;
			RefreshResultsDisplayModePresentation();
		}
'@ 'presentation-only mode switch'

Replace-Exact $results @'
		IReadOnlyList<PikPakFolderCoverageOption> BuildResourceCoverageOptions(IReadOnlyList<ResultsGroupHeader> canonicalGroups) {
			if (canonicalGroups.Count == 0)
				return Array.Empty<PikPakFolderCoverageOption>();

			// Folder identity is a level above the current presentation/filter. Build relation
			// evidence from the complete in-memory duplicate groups so hiding one item/group,
			// changing sort order, or collapsing details cannot change whether two directories
			// are considered the same collection. ResourceResultsBuilder intersects these
			// stable relations with canonicalGroups when deciding which level-2 details to show.
			var groups = Duplicates
				.GroupBy(item => item.ItemInfo.GroupId)
				.Select(group => group.ToList())
				.Where(group => group.Count >= 2)
				.ToList();
			if (groups.Count == 0)
				return Array.Empty<PikPakFolderCoverageOption>();

			var folders = groups
				.SelectMany(group => group)
				.Select(item => string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
					? GetPikPakFolder(item.ItemInfo.Path)
					: item.ItemInfo.Folder)
				.Where(folder => !string.IsNullOrWhiteSpace(folder))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			var stats = Scanner.GetDirectFolderMediaStats(folders);
			return ComputePikPakFolderCoverageOptions(groups, stats);
		}
'@ @'
		IReadOnlyList<PikPakFolderCoverageOption> BuildResourceCoverageOptions(IReadOnlyList<ResultsGroupHeader> canonicalGroups) {
			if (canonicalGroups.Count == 0)
				return Array.Empty<PikPakFolderCoverageOption>();
			return GetOrBuildResourceCoverageOptions();
		}
'@ 'resource coverage cache entry point'

$local = 'VDF.GUI/ViewModels/MainWindowVM_ResultsLocalPresentation.cs'
Replace-Exact $local @'
		bool TryRefreshItemDetailsPresentation(DuplicateItemVM item) {
			if (ActiveResultsDisplayMode != ResultsDisplayMode.SimilarityGroups)
				return false;

			ResultsItemRow? row = null;
'@ @'
		bool TryRefreshItemDetailsPresentation(DuplicateItemVM item) {
			// Details are a one-row presentation mutation in either display mode. Resource
			// mode used to reject this fast path and rebuild every folder relation instead.
			ResultsItemRow? row = null;
'@ 'resource item-details local refresh'

$builder = 'VDF.GUI/ViewModels/Results/ResourceResultsBuilder.cs'
Replace-Exact $builder @'
	public sealed class ResourceRelationHeader : ReactiveObject {
		readonly IReadOnlyList<ResourceDirectedRelation> sourceRelations;
		readonly HashSet<Guid> displayedGroupIds;
		bool _IsSelected;
'@ @'
	public sealed class ResourceRelationHeader : ReactiveObject {
		readonly IReadOnlyList<ResourceDirectedRelation> sourceRelations;
		readonly IReadOnlyList<ResultsGroupHeader> displayedGroups;
		readonly HashSet<Guid> displayedGroupIds;
		bool _IsSelected;
'@ 'retain canonical groups on resource header'

Replace-Exact $builder @'
		internal ResourceRelationHeader(
			IReadOnlyList<ResourceDirectedRelation> relations,
			IReadOnlyCollection<Guid> displayedGroupIds) {
			if (relations == null || relations.Count == 0)
				throw new ArgumentException("At least one folder relation is required.", nameof(relations));

			this.displayedGroupIds = new HashSet<Guid>(displayedGroupIds);
'@ @'
		internal ResourceRelationHeader(
			IReadOnlyList<ResourceDirectedRelation> relations,
			IReadOnlyList<ResultsGroupHeader> displayedGroups) {
			if (relations == null || relations.Count == 0)
				throw new ArgumentException("At least one folder relation is required.", nameof(relations));

			this.displayedGroups = displayedGroups;
			this.displayedGroupIds = displayedGroups.Select(group => group.GroupId).ToHashSet();
'@ 'resource header constructor canonical references'

Replace-Exact $builder @'
		internal IReadOnlyCollection<Guid> DisplayedGroupIds => displayedGroupIds;
		internal string SelectionKey { get; }
'@ @'
		internal IReadOnlyCollection<Guid> DisplayedGroupIds => displayedGroupIds;
		internal IReadOnlyList<ResultsGroupHeader> DisplayedGroups => displayedGroups;
		internal string SelectionKey { get; }
'@ 'resource header displayed groups property'

Replace-Exact $builder @'
		public ReactiveCommand<Unit, Unit> ToggleExpandedCommand => ReactiveCommand.Create(() => {
			IsExpanded = !IsExpanded;
			ApplicationHelpers.MainWindowDataContext.RefreshResultsView();
		});
'@ @'
		public ReactiveCommand<Unit, Unit> ToggleExpandedCommand => ReactiveCommand.Create(() => {
			IsExpanded = !IsExpanded;
			MainWindowVM vm = ApplicationHelpers.MainWindowDataContext;
			if (!vm.TryRefreshResourceRelationPresentation(this))
				vm.RefreshResultsView();
		});
'@ 'local resource expand command'

Replace-Exact $builder @'
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
'@ @'
		internal static IReadOnlyList<IReadOnlyList<ResourceDirectedRelation>> SplitBySharedResourceEvidence(
			IReadOnlyList<ResourceDirectedRelation> relations) {
			if (relations.Count <= 1)
				return new[] { relations };

			// Build an inverted GroupId -> relation index once. The previous implementation
			// compared every relation with every other relation (O(R^2)) for each target folder;
			// large folder sets made entering resource mode disproportionately expensive.
			var ids = relations.Select(relation => relation.GroupIds).ToList();
			var relationIndexesByGroup = new Dictionary<Guid, List<int>>();
			for (int relationIndex = 0; relationIndex < ids.Count; relationIndex++) {
				foreach (Guid groupId in ids[relationIndex]) {
					if (!relationIndexesByGroup.TryGetValue(groupId, out List<int>? indexes))
						relationIndexesByGroup[groupId] = indexes = new List<int>(2);
					indexes.Add(relationIndex);
				}
			}

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
					foreach (Guid groupId in ids[current]) {
						foreach (int candidate in relationIndexesByGroup[groupId]) {
							if (visited[candidate]) continue;
							visited[candidate] = true;
							stack.Push(candidate);
						}
					}
				}
				indexes.Sort();
				components.Add(indexes.Select(index => relations[index]).ToList());
			}
			return components;
		}
'@ 'near-linear relation component split'

Replace-Exact $builder @'
					var header = new ResourceRelationHeader(usedRelations, gids);
					ResourceSeriesSelectionSession.Register(header);
					rows.Add(header);

					// Folder-consolidation mode is permanently folder-first. A traditional
					// ResultsGroupHeader is never emitted here: once the level-1 relationship is
					// expanded, every participating ResultsItemRow lives beneath its ACTUAL
					// containing folder. GroupId remains only as invisible matching/action context.
					if (header.IsExpanded)
						AppendFolderGroupedRows(rows, header, gids, byId, expandedDetails);
'@ @'
					var displayedGroups = gids
						.Select(groupId => byId[groupId])
						.OrderBy(group => group.GroupNumber)
						.ToList();
					var header = new ResourceRelationHeader(usedRelations, displayedGroups);
					ResourceSeriesSelectionSession.Register(header);
					rows.Add(header);

					// Folder-consolidation mode is permanently folder-first. A traditional
					// ResultsGroupHeader is never emitted here: once the level-1 relationship is
					// expanded, every participating ResultsItemRow lives beneath its ACTUAL
					// containing folder. GroupId remains only as invisible matching/action context.
					if (header.IsExpanded)
						AppendFolderGroupedRows(rows, header, displayedGroups, expandedDetails);
'@ 'resource header canonical group reuse'

Replace-Exact $builder @'
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
'@ @'
		internal static List<object> BuildExpandedRows(
			ResourceRelationHeader header,
			IReadOnlySet<DuplicateItemVM>? expandedDetails = null) {
			var rows = new List<object>();
			if (header.IsExpanded)
				AppendFolderGroupedRows(rows, header, header.DisplayedGroups, expandedDetails);
			return rows;
		}

		static void AppendFolderGroupedRows(
			List<object> output,
			ResourceRelationHeader header,
			IReadOnlyList<ResultsGroupHeader> displayedGroups,
			IReadOnlySet<DuplicateItemVM>? expandedDetails) {
			// Normalize and rank relation roots once per expanded header. The old per-file LINQ
			// query normalized/depth-sorted every root again for every child row.
			var roots = header.FolderRows
				.Select((folder, index) => new {
					Folder = folder,
					Index = index,
					Normalized = MainWindowVM.NormalizePikPakPath(folder.Path),
					Depth = MainWindowVM.PikPakPathDepth(folder.Path),
				})
				.OrderByDescending(candidate => candidate.Depth)
				.ThenBy(candidate => candidate.Index)
				.ToList();
			var buckets = new Dictionary<string, FolderBucket>(StringComparer.OrdinalIgnoreCase);

			foreach (ResultsGroupHeader group in displayedGroups.OrderBy(group => group.GroupNumber)) {
				foreach (ResultsItemRow row in group.Rows) {
					string actualFolder = ItemFolder(row.Item);
					string normalizedActualFolder = MainWindowVM.NormalizePikPakPath(actualFolder);
					var root = roots.FirstOrDefault(candidate =>
						NormalizedPathIsWithin(normalizedActualFolder, candidate.Normalized));
					string role = root?.Folder.RoleLabel ?? "关联";
					string relationRoot = root?.Folder.Path ?? actualFolder;
					int rootOrder = root?.Index ?? int.MaxValue;
					string key = normalizedActualFolder;
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

		static bool NormalizedPathIsWithin(string path, string root) =>
			path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
			(path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == '/');
'@ 'local expanded-folder row builder'

Write-Host 'All guarded folder-result patches applied.'
