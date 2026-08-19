// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// Resource-series selection is intentionally independent of DuplicateItemVM.Checked:
	/// a checked series means "include in consolidation", never "delete this file".
	/// The key set survives results rebuilds while current header instances are replaced.
	/// </summary>
	internal static class ResourceSeriesSelectionSession {
		static readonly HashSet<string> selectedKeys = new(StringComparer.OrdinalIgnoreCase);
		static readonly Dictionary<string, ResourceRelationHeader> currentHeaders = new(StringComparer.OrdinalIgnoreCase);
		static WeakReference<ResultsViewSwitcherRow>? currentSwitcher;

		internal static bool ShouldResetViewportForModeChange(ResultsDisplayMode previous, ResultsDisplayMode current) =>
			previous != current;

		internal static void AttachSwitcher(ResultsViewSwitcherRow switcher) {
			ResultsDisplayMode? previousMode = null;
			if (currentSwitcher != null && currentSwitcher.TryGetTarget(out ResultsViewSwitcherRow? previousSwitcher))
				previousMode = previousSwitcher.Selected.Mode;

			currentSwitcher = new WeakReference<ResultsViewSwitcherRow>(switcher);
			switcher.RefreshSeriesSelection();

			// The mode switcher is itself the first row of ResultsRows. RebuildResultsList
			// normally restores the previous scroll anchor, which can immediately scroll this
			// row out of view after the user changes “相似文件组 / 文件夹合并”. That made the
			// folder-match threshold, selected-folder count and merge controls appear to vanish.
			// Queue a top restore after the rebuild/anchor restore has been scheduled. Normal
			// filter/sort/selection rebuilds keep their existing scroll position because their
			// display mode does not change.
			if (previousMode is { } oldMode && ShouldResetViewportForModeChange(oldMode, switcher.Selected.Mode)) {
				Avalonia.Threading.Dispatcher.UIThread.Post(() => {
					try {
						MainWindowVM vm = ApplicationHelpers.MainWindowDataContext;
						if (vm.ResultsRows.Count == 0 || !ReferenceEquals(vm.ResultsRows[0], switcher))
							return;
						vm.ResultsScrollToRow?.Invoke(switcher, 0d);
					}
					catch {
						// No active window during headless/unit-test construction: there is no viewport
						// to restore, so the UI-only convenience can safely be skipped.
					}
				}, Avalonia.Threading.DispatcherPriority.Loaded);
			}
		}

		internal static void BeginBuild() => currentHeaders.Clear();

		internal static void Register(ResourceRelationHeader header) {
			currentHeaders[header.SelectionKey] = header;
			header.SetSelectedFromSession(selectedKeys.Contains(header.SelectionKey));
		}

		internal static void FinishBuild() {
			// A hidden relation (threshold changed, scan result removed) is no longer selected.
			selectedKeys.RemoveWhere(key => !currentHeaders.ContainsKey(key));
			RefreshSwitcher();
		}

		internal static void SetSelected(ResourceRelationHeader header, bool selected) {
			if (selected)
				selectedKeys.Add(header.SelectionKey);
			else
				selectedKeys.Remove(header.SelectionKey);
			RefreshSwitcher();
		}

		internal static int SelectedCount => currentHeaders.Values.Count(header => header.IsSelected);

		internal static IReadOnlyList<ResourceRelationHeader> SelectedHeaders() => currentHeaders.Values
			.Where(header => header.IsSelected)
			.OrderBy(header => header.TargetFolder, StringComparer.OrdinalIgnoreCase)
			.ToList();

		internal static async Task ConsolidateSelectedAsync() {
			var selected = SelectedHeaders();
			if (selected.Count == 0)
				return;
			await ApplicationHelpers.MainWindowDataContext.ConsolidateSelectedResourceSeriesInteractiveAsync(selected);
		}

		static void RefreshSwitcher() {
			if (currentSwitcher != null && currentSwitcher.TryGetTarget(out ResultsViewSwitcherRow? switcher))
				switcher.RefreshSeriesSelection();
		}
	}
}
