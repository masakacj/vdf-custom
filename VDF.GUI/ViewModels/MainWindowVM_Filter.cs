// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GPLv3 as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//     You should have received a copy of the GNU General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.ComponentModel;
using System.Linq;
using Avalonia.Collections;
using ReactiveUI;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {

		public FileTypeFilterOption[] TypeFilters { get; } = {
			new FileTypeFilterOption("All", FileTypeFilter.All),
			new FileTypeFilterOption("Videos", FileTypeFilter.Videos),
			new FileTypeFilterOption("Images", FileTypeFilter.Images),
		};

		FileTypeFilterOption _FileType;

		public FileTypeFilterOption FileType {
			get => _FileType;
			set {
				if (value.Name == _FileType.Name) return;
				_FileType = value;
				this.RaisePropertyChanged(nameof(FileType));
				RequestFilterResultsRefresh();
				RefreshResultsView();
			}
		}
		bool _FilterGroupsWithCheckedItems;
		public bool FilterGroupsWithCheckedItems {
			get => _FilterGroupsWithCheckedItems;
			set {
				if (value == _FilterGroupsWithCheckedItems) return;
				this.RaiseAndSetIfChanged(ref _FilterGroupsWithCheckedItems, value);
				RequestFilterResultsRefresh();
				RefreshResultsView();
			}
		}

		internal bool HasActiveResultsFilter =>
			!string.IsNullOrEmpty(FilterByPath) ||
			FileType.Value != FileTypeFilter.All ||
			FilterSimilarityFrom != 0 || FilterSimilarityTo != 100 ||
			FilterGroupsWithCheckedItems;

		HashSet<Guid> _groupsWithPathHit = new();
		void RebuildSearchPathIndex() {
			// The old implementation scanned every result path on the UI thread, then the
			// result builder scanned the same paths again. Mark this global filter refresh and
			// let MainWindowVM_FilterAsync build the hit set once on a worker thread.
			if (string.IsNullOrEmpty(FilterByPath))
				_groupsWithPathHit.Clear();
			RequestFilterResultsRefresh();
		}

		internal static HashSet<Guid> BuildPathHitGroups(
			IEnumerable<DuplicateItemVM> items, string needle, CancellationToken cancellationToken = default) {
			var result = new HashSet<Guid>();
			if (string.IsNullOrEmpty(needle)) return result;
			int n = 0;
			foreach (DuplicateItemVM item in items) {
				if ((n++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
				if (PathMatchesFilter(item.ItemInfo.Path, needle))
					result.Add(item.ItemInfo.GroupId);
			}
			return result;
		}

		/// <summary>
		/// Substring match by default; when the needle contains * or ? it is treated
		/// as a wildcard pattern instead (unanchored, so "season?\ep*" works without
		/// the user having to wrap it in stars themselves).
		/// </summary>
		internal static bool PathMatchesFilter(string path, string needle) {
			if (needle.IndexOfAny(['*', '?']) < 0)
				return path.Contains(needle, StringComparison.OrdinalIgnoreCase);
			string pattern = EscapeWildcardBackslashes(needle);
			if (!pattern.StartsWith('*')) pattern = "*" + pattern;
			if (!pattern.EndsWith('*')) pattern += "*";
			return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, path);
		}

		/// <summary>
		/// MatchesSimpleExpression treats '\' in the pattern as an escape character, so a
		/// raw Windows path fragment ("*D:\Videos*") would never match anything (#864).
		/// VDF's path patterns treat backslashes literally instead - '*' and '?' are
		/// illegal in Windows file names, so escape syntax has nothing to express here.
		/// </summary>
		internal static string EscapeWildcardBackslashes(string pattern) => pattern.Replace("\\", "\\\\");

		string _FilterByPath = string.Empty;
		public string FilterByPath {
			get => _FilterByPath;
			set {
				if (value == _FilterByPath) return;
				_FilterByPath = value;
				this.RaisePropertyChanged(nameof(FilterByPath));
			}
		}
		int _FilterSimilarityFrom = 0;
		public int FilterSimilarityFrom {
			get => _FilterSimilarityFrom;
			set {
				if (value == _FilterSimilarityFrom) return;
				this.RaiseAndSetIfChanged(ref _FilterSimilarityFrom, value);
				RequestFilterResultsRefresh();
				RefreshResultsView();
			}
		}
		int _FilterSimilarityTo = 100;
		public int FilterSimilarityTo {
			get => _FilterSimilarityTo;
			set {
				if (value == _FilterSimilarityTo) return;
				this.RaiseAndSetIfChanged(ref _FilterSimilarityTo, value);
				RequestFilterResultsRefresh();
				RefreshResultsView();
			}
		}

		/// <summary>The results filter; the view exposes it as always-active toolbar chips.</summary>
		internal bool DuplicatesFilterCore(DuplicateItemVM data) {
			bool ok = true;
			if (!string.IsNullOrEmpty(FilterByPath))
				ok = _groupsWithPathHit.Contains(data.ItemInfo.GroupId);

			if (ok && FileType.Value != FileTypeFilter.All)
				ok = FileType.Value == FileTypeFilter.Images ? data.ItemInfo.IsImage : !data.ItemInfo.IsImage;

			if (ok)
				ok = data.ItemInfo.Similarity >= FilterSimilarityFrom && data.ItemInfo.Similarity <= FilterSimilarityTo;

			if (ok && FilterGroupsWithCheckedItems)
				ok = GroupHasCheckedItems(data.ItemInfo.GroupId);

			data.IsVisibleInFilter = ok;
			return ok;
		}
	}
}
