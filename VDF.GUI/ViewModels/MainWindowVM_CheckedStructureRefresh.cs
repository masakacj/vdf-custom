// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Threading;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		bool checkedStructureRefreshScheduled;

		/// <summary>
		/// Checkbox changes are normally presentation-only because rows bind Checked live. The
		/// checked-groups filter/sort is different: changing one checkbox can add/remove/reorder
		/// a whole group. Coalesce every checkbox change in the current UI turn into ONE refresh,
		/// so a 100k-item bulk selection does not schedule 100k global result rebuilds.
		/// </summary>
		void ScheduleCheckedStructureRefresh() {
			if (!FilterGroupsWithCheckedItems &&
				SettingsFile.Instance.ResultsSortMode != ResultsSortMode.GroupsWithCheckedItems)
				return;
			if (checkedStructureRefreshScheduled)
				return;

			checkedStructureRefreshScheduled = true;
			Dispatcher.UIThread.Post(() => {
				checkedStructureRefreshScheduled = false;
				if (!FilterGroupsWithCheckedItems &&
					SettingsFile.Instance.ResultsSortMode != ResultsSortMode.GroupsWithCheckedItems)
					return;
				RequestFilterResultsRefresh();
				RefreshResultsView();
			}, DispatcherPriority.Background);
		}
	}
}
