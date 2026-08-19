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

		internal static void AttachSwitcher(ResultsViewSwitcherRow switcher) {
			currentSwitcher = new WeakReference<ResultsViewSwitcherRow>(switcher);
			switcher.RefreshSeriesSelection();
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
			await ApplicationHelpers.MainWindowDataContext.ConsolidateSelectedResourceSeriesAsync(selected);
		}

		static void RefreshSwitcher() {
			if (currentSwitcher != null && currentSwitcher.TryGetTarget(out ResultsViewSwitcherRow? switcher))
				switcher.RefreshSeriesSelection();
		}
	}
}
