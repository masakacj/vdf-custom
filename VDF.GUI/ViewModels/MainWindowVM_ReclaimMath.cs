// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using ReactiveUI;
using VDF.Core.Utils;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
		/// <summary>
		/// Reclaimable bytes must come from the actual loser rows, not from a second folder
		/// scan/path lookup. Full-path de-duplication prevents the same loser being counted twice
		/// when a series is represented by more than one folder relation.
		/// </summary>
		internal static long ComputeConfirmedReclaimBytes(IEnumerable<DuplicateItemVM> losers) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var byPath = new Dictionary<string, long>(comparer);
			foreach (DuplicateItemVM loser in losers ?? Array.Empty<DuplicateItemVM>()) {
				string path;
				try { path = Path.GetFullPath(loser.ItemInfo.Path); }
				catch { path = loser.ItemInfo.Path; }
				long bytes = Math.Max(0, loser.ItemInfo.SizeLong);
				if (!byPath.TryGetValue(path, out long old) || bytes > old)
					byPath[path] = bytes;
			}
			return byPath.Values.Sum();
		}
	}
}
