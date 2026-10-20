// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Reactive;
using ReactiveUI;
using VDF.Core.Utils;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		[Flags]
		enum GroupFolderPresence : byte {
			None = 0,
			HasAnchorFolder = 1,
			HasOtherFolder = 2,
			Qualifies = HasAnchorFolder | HasOtherFolder
		}

		internal static string ResultFolderKey(DuplicateItemVM? item) {
			if (item?.ItemInfo == null) return string.Empty;
			string folder = !string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
				? item.ItemInfo.Folder
				: Path.GetDirectoryName(item.ItemInfo.Path) ?? string.Empty;
			return NormalizeFolderStatsKey(folder);
		}

		IEnumerable<DuplicateItemVM> EnumerateVisibleResultItems() =>
			resultsGroups.SelectMany(group => group.Rows.Select(row => row.Item));

		public ReactiveCommand<DuplicateItemVM, Unit> CheckFolderResultHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				var matches = ComputeFolderResultHits(EnumerateVisibleResultItems(), anchor);
				if (matches.Count == 0) return;
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in matches)
						item.Checked = true;
				}
				ScheduleCheckedStructureRefresh();
			});

		public ReactiveCommand<DuplicateItemVM, Unit> CheckOtherFolderGroupHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				ResultsGroupHeader? visibleGroup = resultsGroups.FirstOrDefault(group => group.GroupId == anchor.ItemInfo.GroupId);
				if (visibleGroup == null) return;
				var matches = ComputeOtherFolderGroupHits(visibleGroup.Rows.Select(row => row.Item), anchor);
				if (matches.Count == 0) return;
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in matches)
						item.Checked = true;
				}
				ScheduleCheckedStructureRefresh();
			});

		public ReactiveCommand<string, Unit> SelectOtherPathFolderHitsCommand =>
			ReactiveCommand.Create<string>(folder => {
				var hits = ComputeOtherPathFolderHits(EnumerateVisibleResultItems(), folder);
				using (BeginSelectionUndoBatch()) {
					foreach (var item in hits) item.Checked = true;
				}
				if (hits.Count > 0) ScheduleCheckedStructureRefresh();
			});

		internal static IReadOnlyList<string> ResultFolderAncestors(DuplicateItemVM anchor) {
			string folder = ResultFolderKey(anchor);
			var levels = new List<string>();
			while (!string.IsNullOrEmpty(folder)) {
				levels.Add(folder);
				string parent = NormalizeFolderStatsKey(Path.GetDirectoryName(
					folder.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty);
				if (string.IsNullOrEmpty(parent) || parent == folder) break;
				folder = parent;
			}
			return levels;
		}

		internal static IReadOnlyList<DuplicateItemVM> ComputeOtherPathFolderHits(
			IEnumerable<DuplicateItemVM> items, string folder) {
			if (items == null || string.IsNullOrWhiteSpace(folder))
				return Array.Empty<DuplicateItemVM>();
			string key = NormalizeFolderStatsKey(folder);
			if (string.IsNullOrEmpty(key)) return Array.Empty<DuplicateItemVM>();
			StringComparison cmp = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			string prefix = key.EndsWith('/') ? key : key + "/";
			bool Inside(DuplicateItemVM x) {
				string p = ResultFolderKey(x);
				return p.Equals(key, cmp) || p.StartsWith(prefix, cmp);
			}
			var all = items.Where(x => x?.ItemInfo != null).ToList();
			var source = all.Where(Inside).Select(x => x.ItemInfo.GroupId).ToHashSet();
			return all.Where(x => !Inside(x) && source.Contains(x.ItemInfo.GroupId)).ToList();
		}

		public ReactiveCommand<DuplicateItemVM, Unit> SelectOtherFolderSharedGroupHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				var matches = ComputeOtherFolderSharedGroupHits(resultsGroups, anchor);
				if (matches.Count == 0) return;
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in matches)
						item.Checked = true;
				}
				ScheduleCheckedStructureRefresh();
			});

		public ReactiveCommand<DuplicateItemVM, Unit> SelectSameFolderSharedGroupHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				var matches = ComputeSameFolderSharedGroupHits(resultsGroups, anchor);
				if (matches.Count == 0) return;
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in matches)
						item.Checked = true;
				}
				ScheduleCheckedStructureRefresh();
			});

		public ReactiveCommand<DuplicateItemVM, Unit> SelectSameGroupOtherHitsCommand =>
			ReactiveCommand.Create<DuplicateItemVM>(anchor => {
				if (anchor?.ItemInfo == null) return;
				ResultsGroupHeader? visibleGroup = resultsGroups.FirstOrDefault(group => group.GroupId == anchor.ItemInfo.GroupId);
				if (visibleGroup == null) return;
				var matches = ComputeSameGroupOtherHits(visibleGroup.Rows.Select(row => row.Item), anchor);
				if (matches.Count == 0) return;
				using (BeginSelectionUndoBatch()) {
					foreach (DuplicateItemVM item in matches)
						item.Checked = true;
				}
				ScheduleCheckedStructureRefresh();
			});

		internal static IReadOnlyList<DuplicateItemVM> ComputeFolderResultHits(
			IEnumerable<DuplicateItemVM> visibleItems, DuplicateItemVM anchor) {
			if (anchor?.ItemInfo == null || visibleItems == null) return Array.Empty<DuplicateItemVM>();
			string key = ResultFolderKey(anchor);
			StringComparison comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			return visibleItems.Where(item => ResultFolderKey(item).Equals(key, comparison)).ToList();
		}

		internal static IReadOnlyList<DuplicateItemVM> ComputeFolderResultHits(
			IEnumerable<ResultsGroupHeader> groups, DuplicateItemVM anchor) =>
			groups == null ? Array.Empty<DuplicateItemVM>() : ComputeFolderResultHits(groups.SelectMany(g => g.Rows.Select(r => r.Item)), anchor);

		internal static IReadOnlyList<DuplicateItemVM> ComputeOtherFolderGroupHits(
			IEnumerable<DuplicateItemVM> visibleItems, DuplicateItemVM anchor) {
			if (anchor?.ItemInfo == null || visibleItems == null) return Array.Empty<DuplicateItemVM>();
			string key = ResultFolderKey(anchor);
			Guid groupId = anchor.ItemInfo.GroupId;
			StringComparison comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			return visibleItems
				.Where(item => item.ItemInfo != null && item.ItemInfo.GroupId == groupId && !ResultFolderKey(item).Equals(key, comparison))
				.ToList();
		}

		internal static IReadOnlyList<DuplicateItemVM> ComputeOtherFolderGroupHits(
			IEnumerable<ResultsGroupHeader> groups, DuplicateItemVM anchor) =>
			groups == null ? Array.Empty<DuplicateItemVM>() : ComputeOtherFolderGroupHits(groups.SelectMany(g => g.Rows.Select(r => r.Item)), anchor);

		internal static IReadOnlyList<DuplicateItemVM> ComputeOtherFolderSharedGroupHits(
			IEnumerable<DuplicateItemVM> visibleItems, DuplicateItemVM anchor) =>
			ComputeCrossFolderGroupHits(visibleItems, anchor, targetInsideAnchorFolder: false);

		internal static IReadOnlyList<DuplicateItemVM> ComputeOtherFolderSharedGroupHits(
			IEnumerable<ResultsGroupHeader> groups, DuplicateItemVM anchor) =>
			groups == null ? Array.Empty<DuplicateItemVM>() : ComputeOtherFolderSharedGroupHits(groups.SelectMany(g => g.Rows.Select(r => r.Item)), anchor);

		internal static IReadOnlyList<DuplicateItemVM> ComputeSameFolderSharedGroupHits(
			IEnumerable<DuplicateItemVM> visibleItems, DuplicateItemVM anchor) =>
			ComputeCrossFolderGroupHits(visibleItems, anchor, targetInsideAnchorFolder: true);

		internal static IReadOnlyList<DuplicateItemVM> ComputeSameFolderSharedGroupHits(
			IEnumerable<ResultsGroupHeader> groups, DuplicateItemVM anchor) =>
			groups == null ? Array.Empty<DuplicateItemVM>() : ComputeSameFolderSharedGroupHits(groups.SelectMany(g => g.Rows.Select(r => r.Item)), anchor);

		internal static IReadOnlyList<DuplicateItemVM> ComputeSameGroupOtherHits(
			IEnumerable<DuplicateItemVM> visibleItems, DuplicateItemVM anchor) {
			if (anchor?.ItemInfo == null || visibleItems == null) return Array.Empty<DuplicateItemVM>();
			Guid targetGroupId = anchor.ItemInfo.GroupId;
			return visibleItems
				.Where(item => item.ItemInfo != null &&
				               item.ItemInfo.GroupId == targetGroupId &&
				               !ReferenceEquals(item, anchor))
				.ToList();
		}

		internal static IReadOnlyList<DuplicateItemVM> ComputeSameGroupOtherHits(
			IEnumerable<ResultsGroupHeader> groups, DuplicateItemVM anchor) {
			if (anchor?.ItemInfo == null || groups == null) return Array.Empty<DuplicateItemVM>();
			ResultsGroupHeader? group = groups.FirstOrDefault(g => g.GroupId == anchor.ItemInfo.GroupId);
			if (group == null) return Array.Empty<DuplicateItemVM>();
			return ComputeSameGroupOtherHits(group.Rows.Select(row => row.Item), anchor);
		}

		static IReadOnlyList<DuplicateItemVM> ComputeCrossFolderGroupHits(
			IEnumerable<DuplicateItemVM> visibleItems,
			DuplicateItemVM anchor,
			bool targetInsideAnchorFolder) {
			if (anchor?.ItemInfo == null || visibleItems == null)
				return Array.Empty<DuplicateItemVM>();

			string anchorKey = ResultFolderKey(anchor);
			if (string.IsNullOrEmpty(anchorKey))
				return Array.Empty<DuplicateItemVM>();

			StringComparison comparison = CoreUtils.IsWindows
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;

			IReadOnlyList<DuplicateItemVM> itemList = visibleItems as IReadOnlyList<DuplicateItemVM>
				?? visibleItems.ToList();

			// Pass 1: Identify qualifying groups in O(N) time and O(G) memory.
			// A group qualifies if and only if it contains both an anchor-folder member
			// and a member in a different parent folder.
			var groupPresence = new Dictionary<Guid, GroupFolderPresence>();
			foreach (DuplicateItemVM item in itemList) {
				if (item.ItemInfo == null) continue;
				Guid gid = item.ItemInfo.GroupId;
				bool isAnchorFolder = ResultFolderKey(item).Equals(anchorKey, comparison);
				GroupFolderPresence flag = isAnchorFolder
					? GroupFolderPresence.HasAnchorFolder
					: GroupFolderPresence.HasOtherFolder;

				if (groupPresence.TryGetValue(gid, out GroupFolderPresence current)) {
					if ((current & flag) == 0)
						groupPresence[gid] = current | flag;
				} else {
					groupPresence[gid] = flag;
				}
			}

			// Pass 2: Produce matching items from qualifying groups in O(N) time.
			var results = new List<DuplicateItemVM>();
			foreach (DuplicateItemVM item in itemList) {
				if (item.ItemInfo == null) continue;
				Guid gid = item.ItemInfo.GroupId;
				if (groupPresence.TryGetValue(gid, out GroupFolderPresence presence) &&
				    presence == GroupFolderPresence.Qualifies) {
					bool isAnchorFolder = ResultFolderKey(item).Equals(anchorKey, comparison);
					if (targetInsideAnchorFolder == isAnchorFolder) {
						results.Add(item);
					}
				}
			}

			return results;
		}
	}
}
