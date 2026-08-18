// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System;
using System.Collections.Generic;
using System.Linq;
using ReactiveUI;
using VDF.Core.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// Quick duplicate-selection rules ported from the user's PikPak enhancement script.
	/// The rules operate only on the currently visible result groups and preserve the
	/// current per-group display order for tie breaking and "keep first" behavior.
	/// </summary>
	public partial class MainWindowVM : ReactiveObject {

		internal int RunPikPakSelection(CustomSelectionData data) {
			var action = (PikPakQuickAction)data.PikPakActionSelection;
			if (action == PikPakQuickAction.Disabled)
				return 0;

			var groups = GetPikPakVisibleGroupsInDisplayOrder();
			if (groups.Count == 0)
				return 0;

			PikPakSelectionPlan plan = action switch {
				PikPakQuickAction.KeepNewest => ComputePikPakKeepSelection(groups, PikPakKeepRule.Newest),
				PikPakQuickAction.KeepOldest => ComputePikPakKeepSelection(groups, PikPakKeepRule.Oldest),
				PikPakQuickAction.KeepLargest => ComputePikPakKeepSelection(groups, PikPakKeepRule.Largest),
				PikPakQuickAction.KeepSmallest => ComputePikPakKeepSelection(groups, PikPakKeepRule.Smallest),
				PikPakQuickAction.KeepShortestFileName => ComputePikPakKeepSelection(groups, PikPakKeepRule.ShortestFileName),
				PikPakQuickAction.KeepLongestFileName => ComputePikPakKeepSelection(groups, PikPakKeepRule.LongestFileName),
				PikPakQuickAction.KeepShortestPath => ComputePikPakKeepSelection(groups, PikPakKeepRule.ShortestPath),
				PikPakQuickAction.KeepLongestPath => ComputePikPakKeepSelection(groups, PikPakKeepRule.LongestPath),
				PikPakQuickAction.SameFolderExtras => ComputePikPakSameFolderExtras(groups),
				PikPakQuickAction.KeepPathContainingKeyword => ComputePikPakKeywordKeepSelection(groups, data.PikPakKeyword, pathMode: true),
				PikPakQuickAction.KeepFileNameContainingKeyword => ComputePikPakKeywordKeepSelection(groups, data.PikPakKeyword, pathMode: false),
				PikPakQuickAction.SelectPathContainingKeyword => ComputePikPakKeywordDirectSelection(groups, data.PikPakKeyword, pathMode: true),
				PikPakQuickAction.SelectFileNameContainingKeyword => ComputePikPakKeywordDirectSelection(groups, data.PikPakKeyword, pathMode: false),
				PikPakQuickAction.SelectInsideTargetPaths => ComputePikPakPathScopeSelection(groups, data.PikPakTargetPaths, selectInside: true),
				PikPakQuickAction.SelectOutsideTargetPaths => ComputePikPakPathScopeSelection(groups, data.PikPakTargetPaths, selectInside: false),
				_ => new PikPakSelectionPlan(),
			};

			// Match the PikPak script's explicit-selection semantics: once a rule actually
			// matched, replace check state inside the CURRENT visible/scoped result set.
			// Filter-hidden rows and rows outside a multi-row highlight scope are untouched.
			if (plan.MatchedGroups == 0 || plan.ToCheck.Count == 0)
				return 0;

			using var undoBatch = BeginSelectionUndoBatch();
			foreach (var item in groups.SelectMany(g => g).Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance))
				item.Checked = false;
			foreach (var item in plan.ToCheck)
				item.Checked = true;

			RefreshResultsView();
			return plan.ToCheck.Count;
		}

		List<List<DuplicateItemVM>> GetPikPakVisibleGroupsInDisplayOrder() {
			var scoped = ScopedDuplicates();
			HashSet<DuplicateItemVM>? scope = scoped.Count == Duplicates.Count
				? null
				: new HashSet<DuplicateItemVM>(scoped, ReferenceEqualityComparer<DuplicateItemVM>.Instance);

			var groups = new List<List<DuplicateItemVM>>();
			foreach (var header in resultsGroups) {
				var members = new List<DuplicateItemVM>();
				foreach (var row in header.Rows) {
					if (!row.Item.IsVisibleInFilter)
						continue;
					if (scope != null && !scope.Contains(row.Item))
						continue;
					members.Add(row.Item);
				}
				if (members.Count >= 2)
					groups.Add(members);
			}
			return groups;
		}

		internal static PikPakSelectionPlan ComputePikPakKeepSelection(
			IReadOnlyList<List<DuplicateItemVM>> groups, PikPakKeepRule rule) {
			var plan = new PikPakSelectionPlan();
			foreach (var group in groups) {
				if (group.Count < 2) continue;
				var keeper = PickPikPakKeeper(group, rule);
				plan.Keepers.Add(keeper);
				plan.MatchedGroups++;
				foreach (var item in group)
					if (!ReferenceEquals(item, keeper))
						plan.ToCheck.Add(item);
			}
			return plan;
		}

		internal static PikPakSelectionPlan ComputePikPakKeywordKeepSelection(
			IReadOnlyList<List<DuplicateItemVM>> groups, string? keyword, bool pathMode) {
			var plan = new PikPakSelectionPlan();
			keyword = keyword?.Trim();
			if (string.IsNullOrEmpty(keyword))
				return plan;

			foreach (var group in groups) {
				DuplicateItemVM? keeper = null;
				foreach (var item in group) {
					string haystack = pathMode ? item.ItemInfo.Path : GetPikPakFileName(item.ItemInfo.Path);
					if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)) {
						// Multiple hits: exactly like the PikPak script, current group order wins.
						keeper = item;
						break;
					}
				}
				if (keeper == null)
					continue;

				plan.Keepers.Add(keeper);
				plan.MatchedGroups++;
				foreach (var item in group)
					if (!ReferenceEquals(item, keeper))
						plan.ToCheck.Add(item);
			}
			return plan;
		}

		internal static PikPakSelectionPlan ComputePikPakKeywordDirectSelection(
			IReadOnlyList<List<DuplicateItemVM>> groups, string? keyword, bool pathMode) {
			var plan = new PikPakSelectionPlan();
			keyword = keyword?.Trim();
			if (string.IsNullOrEmpty(keyword))
				return plan;

			foreach (var group in groups) {
				bool groupMatched = false;
				foreach (var item in group) {
					string haystack = pathMode ? item.ItemInfo.Path : GetPikPakFileName(item.ItemInfo.Path);
					if (!haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
						continue;
					plan.ToCheck.Add(item);
					groupMatched = true;
				}
				if (groupMatched)
					plan.MatchedGroups++;
			}
			return plan;
		}

		internal static PikPakSelectionPlan ComputePikPakSameFolderExtras(
			IReadOnlyList<List<DuplicateItemVM>> groups) {
			var plan = new PikPakSelectionPlan();
			foreach (var group in groups) {
				var byFolder = new Dictionary<string, List<DuplicateItemVM>>(StringComparer.OrdinalIgnoreCase);
				var folderOrder = new List<string>();
				foreach (var item in group) {
					string folder = GetPikPakFolder(item.ItemInfo.Path);
					if (!byFolder.TryGetValue(folder, out var list)) {
						byFolder[folder] = list = new List<DuplicateItemVM>();
						folderOrder.Add(folder);
					}
					list.Add(item);
				}

				bool groupMatched = false;
				foreach (var folder in folderOrder) {
					var list = byFolder[folder];
					if (list.Count < 2)
						continue;
					groupMatched = true;
					plan.Keepers.Add(list[0]);
					for (int i = 1; i < list.Count; i++)
						plan.ToCheck.Add(list[i]);
				}
				if (groupMatched)
					plan.MatchedGroups++;
			}
			return plan;
		}

		internal static PikPakSelectionPlan ComputePikPakPathScopeSelection(
			IReadOnlyList<List<DuplicateItemVM>> groups, string? targetPathsText, bool selectInside) {
			var plan = new PikPakSelectionPlan();
			var targets = ParsePikPakTargetPaths(targetPathsText);
			if (targets.Count == 0)
				return plan;

			foreach (var group in groups) {
				var inside = new List<DuplicateItemVM>();
				var outside = new List<DuplicateItemVM>();
				foreach (var item in group) {
					if (PikPakPathIsInScope(item.ItemInfo.Path, targets))
						inside.Add(item);
					else
						outside.Add(item);
				}
				// Only crossing groups qualify: target-side files must have a counterpart
				// outside the target paths (and vice versa).
				if (inside.Count == 0 || outside.Count == 0)
					continue;
				plan.MatchedGroups++;
				plan.ToCheck.AddRange(selectInside ? inside : outside);
			}
			return plan;
		}

		static DuplicateItemVM PickPikPakKeeper(IReadOnlyList<DuplicateItemVM> members, PikPakKeepRule rule) {
			var winner = members[0];
			for (int i = 1; i < members.Count; i++) {
				var candidate = members[i];
				if (PikPakCandidateIsBetter(candidate, winner, rule))
					winner = candidate;
			}
			return winner;
		}

		static bool PikPakCandidateIsBetter(DuplicateItemVM candidate, DuplicateItemVM winner, PikPakKeepRule rule) => rule switch {
			PikPakKeepRule.Newest => candidate.ItemInfo.DateCreated > winner.ItemInfo.DateCreated,
			PikPakKeepRule.Oldest => candidate.ItemInfo.DateCreated < winner.ItemInfo.DateCreated,
			PikPakKeepRule.Largest => candidate.ItemInfo.SizeLong > winner.ItemInfo.SizeLong,
			PikPakKeepRule.Smallest => candidate.ItemInfo.SizeLong < winner.ItemInfo.SizeLong,
			PikPakKeepRule.ShortestFileName => GetPikPakFileName(candidate.ItemInfo.Path).Length < GetPikPakFileName(winner.ItemInfo.Path).Length,
			PikPakKeepRule.LongestFileName => GetPikPakFileName(candidate.ItemInfo.Path).Length > GetPikPakFileName(winner.ItemInfo.Path).Length,
			PikPakKeepRule.ShortestPath => candidate.ItemInfo.Path.Length < winner.ItemInfo.Path.Length,
			PikPakKeepRule.LongestPath => candidate.ItemInfo.Path.Length > winner.ItemInfo.Path.Length,
			_ => false,
		};

		internal static string GetPikPakFileName(string path) {
			int i = path.LastIndexOfAny(new[] { '\\', '/' });
			return i >= 0 && i + 1 < path.Length ? path[(i + 1)..] : path;
		}

		internal static string GetPikPakFolder(string path) {
			int i = path.LastIndexOfAny(new[] { '\\', '/' });
			return i > 0 ? NormalizePikPakPath(path[..i]) : string.Empty;
		}

		internal static List<string> ParsePikPakTargetPaths(string? text) {
			if (string.IsNullOrWhiteSpace(text))
				return new List<string>();
			return text
				.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(NormalizePikPakPath)
				.Where(s => s.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		internal static bool PikPakPathIsInScope(string path, IReadOnlyList<string> normalizedTargets) {
			string normalized = NormalizePikPakPath(path);
			foreach (string target in normalizedTargets) {
				if (normalized.Equals(target, StringComparison.OrdinalIgnoreCase))
					return true;
				if (normalized.Length > target.Length &&
					normalized.StartsWith(target, StringComparison.OrdinalIgnoreCase) &&
					normalized[target.Length] == '/')
					return true;
			}
			return false;
		}

		internal static string NormalizePikPakPath(string path) {
			string value = (path ?? string.Empty).Trim().Replace('\\', '/');
			while (value.Length > 1 && value.EndsWith('/', StringComparison.Ordinal))
				value = value[..^1];
			return value;
		}
	}

	internal enum PikPakKeepRule {
		Newest,
		Oldest,
		Largest,
		Smallest,
		ShortestFileName,
		LongestFileName,
		ShortestPath,
		LongestPath,
	}

	internal enum PikPakQuickAction {
		Disabled = 0,
		KeepNewest = 1,
		KeepOldest = 2,
		KeepLargest = 3,
		KeepSmallest = 4,
		KeepShortestFileName = 5,
		KeepLongestFileName = 6,
		KeepShortestPath = 7,
		KeepLongestPath = 8,
		SameFolderExtras = 9,
		KeepPathContainingKeyword = 10,
		KeepFileNameContainingKeyword = 11,
		SelectPathContainingKeyword = 12,
		SelectFileNameContainingKeyword = 13,
		SelectInsideTargetPaths = 14,
		SelectOutsideTargetPaths = 15,
	}

	internal sealed class PikPakSelectionPlan {
		public List<DuplicateItemVM> Keepers { get; } = new();
		public List<DuplicateItemVM> ToCheck { get; } = new();
		public int MatchedGroups { get; set; }
	}
}
