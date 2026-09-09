// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		Guid? cachedResultNavigationGroupId;
		int cachedResultNavigationIndex = -1;

		/// <summary>
		/// Sequential keyboard triage should not linearly scan 200k group headers for every
		/// next/previous click. Cache only the last group/index (a few bytes, not a 200k-entry
		/// dictionary). Any sort/filter/rebuild that moves the group invalidates itself because
		/// the cached slot is verified before reuse.
		/// </summary>
		int FindResultsGroupIndex(Guid groupId) {
			if (cachedResultNavigationGroupId == groupId &&
				cachedResultNavigationIndex >= 0 && cachedResultNavigationIndex < resultsGroups.Count &&
				resultsGroups[cachedResultNavigationIndex].GroupId == groupId)
				return cachedResultNavigationIndex;

			int index = resultsGroups.FindIndex(group => group.GroupId == groupId);
			if (index >= 0)
				RememberResultNavigationIndex(index);
			return index;
		}

		void RememberResultNavigationIndex(int index) {
			if (index < 0 || index >= resultsGroups.Count) {
				cachedResultNavigationGroupId = null;
				cachedResultNavigationIndex = -1;
				return;
			}
			cachedResultNavigationIndex = index;
			cachedResultNavigationGroupId = resultsGroups[index].GroupId;
		}
	}
}
