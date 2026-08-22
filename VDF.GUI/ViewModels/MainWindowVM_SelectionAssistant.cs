// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Reactive;
using ReactiveUI;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	internal sealed record SelectionAssistantPlan(
		IReadOnlyList<DuplicateItemVM> TouchedItems,
		IReadOnlyList<DuplicateItemVM> Keepers,
		IReadOnlyList<DuplicateItemVM> ToCheck,
		int ProcessedGroups,
		int GroupsWithMarks,
		int TiedGroups,
		int TieBreakSelections,
		int ActiveRules,
		long SelectedBytes);

	internal sealed record CompiledSelectionAssistantRule(
		SelectionAssistantRuleKind Kind,
		string[] Keywords);

	public partial class MainWindowVM : ReactiveObject {
		public ReactiveCommand<Unit, Unit> OpenSelectionAssistantCommand => ReactiveCommand.Create(() => {
			var dialog = new SelectionAssistantView(this);
			dialog.Show(ApplicationHelpers.MainWindow);
		});

		internal SelectionAssistantPlan PreviewSelectionAssistant(SelectionAssistantData data) {
			var scope = ScopedDuplicates();
			return ComputeSelectionAssistant(
				scope,
				item => !data.CurrentFilterOnly || item.IsVisibleInFilter,
				data,
				QualityCriteriaOrder);
		}

		internal SelectionAssistantPlan RunSelectionAssistant(SelectionAssistantData data) {
			SelectionAssistantPlan plan = PreviewSelectionAssistant(data);
			using var undoBatch = BeginSelectionUndoBatch();

			if (!data.PreserveExistingSelection) {
				foreach (DuplicateItemVM item in plan.TouchedItems)
					item.Checked = false;
			}

			// Safety is stronger than "preserve existing selection": every group the
			// assistant actually processes receives one explicit unchecked keeper, so this
			// operation can never leave a processed group fully checked by accident.
			foreach (DuplicateItemVM keeper in plan.Keepers)
				keeper.Checked = false;
			foreach (DuplicateItemVM item in plan.ToCheck)
				item.Checked = true;

			RefreshResultsView();
			return plan;
		}

		/// <summary>
		/// Pure Duplicate Cleaner-style ordered preference planner. Rules are lexicographic:
		/// the first active rule that differentiates two files wins. A positive comparison
		/// means the left candidate is more disposable. The least-disposable candidate is
		/// kept; path is only a deterministic final tie-break and never counts as a rule hit.
		/// </summary>
		internal static SelectionAssistantPlan ComputeSelectionAssistant(
			IReadOnlyList<DuplicateItemVM> allItems,
			Func<DuplicateItemVM, bool> eligible,
			SelectionAssistantData data,
			IEnumerable<string>? bestCriteriaOrder = null) {
			ArgumentNullException.ThrowIfNull(allItems);
			ArgumentNullException.ThrowIfNull(eligible);
			ArgumentNullException.ThrowIfNull(data);

			List<CompiledSelectionAssistantRule> rules = CompileSelectionAssistantRules(data.Rules);
			if (rules.Count == 0)
				return new SelectionAssistantPlan(Array.Empty<DuplicateItemVM>(), Array.Empty<DuplicateItemVM>(),
					Array.Empty<DuplicateItemVM>(), 0, 0, 0, 0, 0, 0);

			var touched = new List<DuplicateItemVM>();
			var keepers = new List<DuplicateItemVM>();
			var toCheck = new List<DuplicateItemVM>();
			int processedGroups = 0;
			int groupsWithMarks = 0;
			int tiedGroups = 0;
			int tieBreakSelections = 0;

			foreach (var sourceGroup in allItems.GroupBy(item => item.ItemInfo.GroupId)) {
				var members = sourceGroup
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
					.Where(eligible)
					.ToList();
				if (members.Count < 2)
					continue;

				processedGroups++;
				touched.AddRange(members);

				DuplicateItemVM? best = null;
				if (rules.Any(rule => rule.Kind == SelectionAssistantRuleKind.NonBest)) {
					best = bestCriteriaOrder == null
						? RecommendBest(members).Winner
						: RecommendBest(members, bestCriteriaOrder).Winner;
				}

				DuplicateItemVM keeper = PickSelectionAssistantKeeper(members, rules, best);
				keepers.Add(keeper);
				int groupMarksBefore = toCheck.Count;
				bool groupHadRuleTie = false;

				foreach (DuplicateItemVM item in members) {
					if (ReferenceEquals(item, keeper))
						continue;
					int ruleComparison = CompareSelectionAssistantCandidates(item, keeper, rules, best);
					if (data.Mode == SelectionAssistantMode.RulesOnly && ruleComparison <= 0)
						continue;
					if (ruleComparison == 0) {
						groupHadRuleTie = true;
						tieBreakSelections++;
					}
					toCheck.Add(item);
				}

				if (groupHadRuleTie)
					tiedGroups++;
				if (toCheck.Count > groupMarksBefore)
					groupsWithMarks++;
			}

			long selectedBytes = toCheck.Sum(item => Math.Max(0, item.ItemInfo.SizeLong));
			return new SelectionAssistantPlan(touched, keepers, toCheck, processedGroups,
				groupsWithMarks, tiedGroups, tieBreakSelections, rules.Count, selectedBytes);
		}

		internal static List<CompiledSelectionAssistantRule> CompileSelectionAssistantRules(
			IEnumerable<SelectionAssistantRuleData>? source) {
			var result = new List<CompiledSelectionAssistantRule>();
			if (source == null)
				return result;
			foreach (SelectionAssistantRuleData rule in source) {
				if (!rule.Enabled)
					continue;
				string[] keywords = RuleNeedsKeywords(rule.Kind)
					? ParseSelectionAssistantKeywords(rule.Value)
					: Array.Empty<string>();
				if (RuleNeedsKeywords(rule.Kind) && keywords.Length == 0)
					continue;
				result.Add(new CompiledSelectionAssistantRule(rule.Kind, keywords));
			}
			return result;
		}

		internal static bool RuleNeedsKeywords(SelectionAssistantRuleKind kind) => kind is
			SelectionAssistantRuleKind.KeepPathContaining or
			SelectionAssistantRuleKind.DeletePathContaining or
			SelectionAssistantRuleKind.DeleteFileNameContaining;

		internal static string[] ParseSelectionAssistantKeywords(string? value) =>
			(value ?? string.Empty)
				.Split(new[] { '\r', '\n', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(keyword => keyword.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

		static DuplicateItemVM PickSelectionAssistantKeeper(
			IReadOnlyList<DuplicateItemVM> members,
			IReadOnlyList<CompiledSelectionAssistantRule> rules,
			DuplicateItemVM? best) {
			DuplicateItemVM keeper = members[0];
			for (int i = 1; i < members.Count; i++) {
				DuplicateItemVM candidate = members[i];
				int comparison = CompareSelectionAssistantCandidates(candidate, keeper, rules, best);
				if (comparison < 0 || (comparison == 0 && CompareCanonicalPath(candidate, keeper) < 0))
					keeper = candidate;
			}
			return keeper;
		}

		/// <returns>Positive when <paramref name="left"/> is more disposable.</returns>
		internal static int CompareSelectionAssistantCandidates(
			DuplicateItemVM left,
			DuplicateItemVM right,
			IReadOnlyList<CompiledSelectionAssistantRule> rules,
			DuplicateItemVM? best) {
			foreach (CompiledSelectionAssistantRule rule in rules) {
				int comparison = rule.Kind switch {
					SelectionAssistantRuleKind.NonBest => CompareNonBest(left, right, best),
					SelectionAssistantRuleKind.KeepPathContaining => CompareKeepKeyword(left, right, rule.Keywords, fileName: false),
					SelectionAssistantRuleKind.DeletePathContaining => CompareDeleteKeyword(left, right, rule.Keywords, fileName: false),
					SelectionAssistantRuleKind.DeleteFileNameContaining => CompareDeleteKeyword(left, right, rule.Keywords, fileName: true),
					SelectionAssistantRuleKind.LowerResolution => right.ItemInfo.FrameSizeInt.CompareTo(left.ItemInfo.FrameSizeInt),
					SelectionAssistantRuleKind.LowerBitrate => right.ItemInfo.BitRateKbs.CompareTo(left.ItemInfo.BitRateKbs),
					SelectionAssistantRuleKind.LowerFps => right.ItemInfo.Fps.CompareTo(left.ItemInfo.Fps),
					SelectionAssistantRuleKind.ShorterDuration => right.ItemInfo.Duration.CompareTo(left.ItemInfo.Duration),
					SelectionAssistantRuleKind.LowerAudioBitrate => right.ItemInfo.AudioBitRateKbs.CompareTo(left.ItemInfo.AudioBitRateKbs),
					SelectionAssistantRuleKind.SmallerFile => right.ItemInfo.SizeLong.CompareTo(left.ItemInfo.SizeLong),
					SelectionAssistantRuleKind.OlderCreated => right.ItemInfo.DateCreated.CompareTo(left.ItemInfo.DateCreated),
					SelectionAssistantRuleKind.NewerCreated => left.ItemInfo.DateCreated.CompareTo(right.ItemInfo.DateCreated),
					SelectionAssistantRuleKind.LongerPath => left.ItemInfo.Path.Length.CompareTo(right.ItemInfo.Path.Length),
					SelectionAssistantRuleKind.LongerFileName => FileNameLength(left).CompareTo(FileNameLength(right)),
					SelectionAssistantRuleKind.DeeperFolder => left.ItemInfo.FolderDepth.CompareTo(right.ItemInfo.FolderDepth),
					_ => 0,
				};
				if (comparison != 0)
					return Math.Sign(comparison);
			}
			return 0;
		}

		static int CompareNonBest(DuplicateItemVM left, DuplicateItemVM right, DuplicateItemVM? best) {
			if (best == null)
				return 0;
			int leftLoss = ReferenceEquals(left, best) ? 0 : 1;
			int rightLoss = ReferenceEquals(right, best) ? 0 : 1;
			return leftLoss.CompareTo(rightLoss);
		}

		static int CompareKeepKeyword(DuplicateItemVM left, DuplicateItemVM right, IReadOnlyList<string> keywords, bool fileName) {
			bool leftMatch = MatchesSelectionAssistantKeywords(left, keywords, fileName);
			bool rightMatch = MatchesSelectionAssistantKeywords(right, keywords, fileName);
			// A non-match is more disposable when the rule says to preserve matches.
			return (!leftMatch).CompareTo(!rightMatch);
		}

		static int CompareDeleteKeyword(DuplicateItemVM left, DuplicateItemVM right, IReadOnlyList<string> keywords, bool fileName) {
			bool leftMatch = MatchesSelectionAssistantKeywords(left, keywords, fileName);
			bool rightMatch = MatchesSelectionAssistantKeywords(right, keywords, fileName);
			return leftMatch.CompareTo(rightMatch);
		}

		internal static bool MatchesSelectionAssistantKeywords(
			DuplicateItemVM item,
			IReadOnlyList<string> keywords,
			bool fileName) {
			string target;
			if (fileName) {
				target = Path.GetFileName(item.ItemInfo.Path) ?? string.Empty;
			}
			else {
				target = !string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
					? item.ItemInfo.Folder
					: Path.GetDirectoryName(item.ItemInfo.Path) ?? string.Empty;
			}
			return keywords.Any(keyword => target.Contains(keyword, StringComparison.OrdinalIgnoreCase));
		}

		static int FileNameLength(DuplicateItemVM item) =>
			(Path.GetFileName(item.ItemInfo.Path) ?? string.Empty).Length;

		static int CompareCanonicalPath(DuplicateItemVM left, DuplicateItemVM right) {
			int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.ItemInfo.Path, right.ItemInfo.Path);
			return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.ItemInfo.Path, right.ItemInfo.Path);
		}
	}
}
