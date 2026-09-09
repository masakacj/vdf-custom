// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Threading;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		/// <summary>
		/// Signals an in-place item mutation (rename/metadata edit) that does not raise an
		/// Avalonia collection event. The affected group can normally use the dirty-group path;
		/// an already-running background global build is version-invalidated so it cannot later
		/// overwrite the freshly rebuilt local presentation.
		/// </summary>
		void MarkResultItemMutation(Guid groupId) {
			EnsureResultsMutationTracking();
			if (!resultsIncrementalInvalidated)
				resultsDirtyGroupIds.Add(groupId);
			if (filterResultsVersionTrackingInstalled)
				Interlocked.Increment(ref filterResultsCollectionVersion);
		}
	}
}
