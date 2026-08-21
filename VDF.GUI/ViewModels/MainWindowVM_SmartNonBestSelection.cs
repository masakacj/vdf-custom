// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Reactive;
using ReactiveUI;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	public sealed record SmartNonBestSelectionOptions(
		string FileNameKeywords,
		string PathKeywords);

	public partial class MainWindowVM : ReactiveObject {
		/// <summary>
		/// Recomputes the checked state for the currently visible result scope: every
		/// duplicate group keeps its recommended BEST unchecked, while eligible non-BEST
		/// members are checked. Optional filename/folder-path keywords narrow the losers.
		/// Hidden-by-filter items are deliberately not modified.
		/// </summary>
		public ReactiveCommand<Unit, Unit> SmartSelectNonBestCommand =>
			ReactiveCommand.CreateFromTask(async () => {
				var dialog = new SmartNonBestSelectionDialog();
				SmartNonBestSelectionOptions? options =
					await dialog.ShowDialog<SmartNonBestSelectionOptions?>(ApplicationHelpers.MainWindow);
				if (options == null) return;

				var visible = Duplicates.Where(item => item.IsVisibleInFilter).ToList();
				var visibleSet = new HashSet<DuplicateItemVM>(
					visible, ReferenceEqualityComparer<DuplicateItemVM>.Instance);
				var selected = ComputeSmartNonBestSelection(
					Duplicates.ToList(),
					item => visibleSet.Contains(item),
					options);
				var selectedSet = new HashSet<DuplicateItemVM>(
					selected, ReferenceEqualityComparer<DuplicateItemVM>.Instance);

				using var undoBatch = BeginSelectionUndoBatch();
				foreach (DuplicateItemVM item in visible)
					item.Checked = selectedSet.Contains(item);

				RefreshResultsView();
			});

		internal static IReadOnlyList<DuplicateItemVM> ComputeSmartNonBestSelection(
			IReadOnlyList<DuplicateItemVM> allItems,
			Func<DuplicateItemVM, bool> eligible,
			SmartNonBestSelectionOptions options) {
			string[] fileNameKeywords = ParseSmartSelectionKeywords(options.FileNameKeywords);
			string[] pathKeywords = ParseSmartSelectionKeywords(options.PathKeywords);
			var selected = new List<DuplicateItemVM>();

			foreach (var group in allItems.GroupBy(item => item.ItemInfo.GroupId)) {
				var members = group
					.Distinct(ReferenceEqualityComparer<DuplicateItemVM>.Instance)
					.ToList();
				if (members.Count < 2) continue;

				BestRecommendation recommendation = RecommendBestUsingCurrentRules(members);
				DuplicateItemVM? best = recommendation.Winner;
				if (best == null) continue;

				foreach (DuplicateItemVM item in members) {
					if (ReferenceEquals(item, best) || !eligible(item)) continue;
					if (MatchesSmartSelectionKeywords(item, fileNameKeywords, pathKeywords))
						selected.Add(item);
				}
			}
			return selected;
		}

		internal static string[] ParseSmartSelectionKeywords(string? value) =>
			(value ?? string.Empty)
				.Split(new[] { '\r', '\n', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(keyword => keyword.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

		internal static bool MatchesSmartSelectionKeywords(
			DuplicateItemVM item,
			IReadOnlyList<string> fileNameKeywords,
			IReadOnlyList<string> pathKeywords) {
			string fileName = Path.GetFileName(item.ItemInfo.Path) ?? string.Empty;
			string folder = !string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
				? item.ItemInfo.Folder
				: Path.GetDirectoryName(item.ItemInfo.Path) ?? string.Empty;

			bool fileMatch = fileNameKeywords.Count == 0 ||
				fileNameKeywords.Any(keyword => fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
			bool pathMatch = pathKeywords.Count == 0 ||
				pathKeywords.Any(keyword => folder.Contains(keyword, StringComparison.OrdinalIgnoreCase));
			return fileMatch && pathMatch;
		}
	}
}
