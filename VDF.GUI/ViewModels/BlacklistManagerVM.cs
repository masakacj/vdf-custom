// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	public sealed class BlacklistEntryVM {
		internal const int PreviewLimit = 3;
		internal const int MaxPathChars = 180;
		public HashSet<string> Source { get; }
		public string PathsDisplay { get; }
		public int Count => Source.Count;

		public BlacklistEntryVM(HashSet<string> source) {
			Source = source;
			// Never sort/join an entire group while opening the dialog: one maliciously
			// large false-match group used to allocate megabytes of text on the UI thread.
			var paths = new List<string>(PreviewLimit + 1);
			foreach (string path in source) {
				if (paths.Count == PreviewLimit) break;
				paths.Add(path.Length <= MaxPathChars ? path : path[..MaxPathChars] + "…");
			}
			if (source.Count > paths.Count)
				paths.Add($"… 另有 {source.Count - paths.Count:N0} 条路径，可使用路径关键词查找该组");
			PathsDisplay = string.Join(Environment.NewLine, paths);
		}
	}

	public sealed class BlacklistManagerVM : ReactiveObject {
		internal const int GroupPageSize = 100;
		internal const int FolderPageSize = 100;
		internal const int ExcludedFilePageSize = 200;
		readonly List<HashSet<string>> _liveList;
		readonly Func<Task> _persist;
		readonly Func<string?, bool, string?>? addResultPath;
		readonly Action? pathRulesChanged;
		readonly Func<string?, int, int, Task<ManuallyExcludedPage>>? loadExcludedFiles;
		readonly Func<IReadOnlyList<string>, Task<int>>? restoreExcludedFiles;
		readonly Action? scanDirectoriesChanged;

		public ObservableCollection<string> ScanDirectories { get; }
		public ObservableCollection<ResultPathExclusion> PathRules { get; }
		// The original settings collections stay untouched; ListBoxes bind only to
		// bounded windows so Avalonia never has to realize tens of thousands of rows.
		public ObservableCollection<string> VisibleScanDirectories { get; } = new();
		public ObservableCollection<ResultPathExclusion> VisiblePathRules { get; } = new();
		public ObservableCollection<string> ExcludedFiles { get; } = new();
		public ObservableCollection<BlacklistEntryVM> Entries { get; } = new();

		public ReactiveCommand<Unit, Unit> AddScanDirectoryCommand { get; }
		public ReactiveCommand<IList?, Unit> RemoveScanDirectoriesCommand { get; }
		public ReactiveCommand<Unit, Unit> SearchScanDirectoryCommand { get; }
		public ReactiveCommand<Unit, Unit> PreviousScanDirectoryPageCommand { get; }
		public ReactiveCommand<Unit, Unit> NextScanDirectoryPageCommand { get; }
		public ReactiveCommand<Unit, Unit> AddResultPathCommand { get; }
		public ReactiveCommand<IList?, Unit> RemoveResultPathsCommand { get; }
		public ReactiveCommand<Unit, Unit> SearchResultPathCommand { get; }
		public ReactiveCommand<Unit, Unit> PreviousResultPathPageCommand { get; }
		public ReactiveCommand<Unit, Unit> NextResultPathPageCommand { get; }
		public ReactiveCommand<Unit, Unit> LoadExcludedFilesCommand { get; }
		public ReactiveCommand<Unit, Unit> SearchExcludedFilesCommand { get; }
		public ReactiveCommand<Unit, Unit> PreviousExcludedFilePageCommand { get; }
		public ReactiveCommand<Unit, Unit> NextExcludedFilePageCommand { get; }
		public ReactiveCommand<IList?, Unit> RestoreExcludedFilesCommand { get; }
		public ReactiveCommand<Unit, Unit> SearchGroupCommand { get; }
		public ReactiveCommand<Unit, Unit> PreviousGroupPageCommand { get; }
		public ReactiveCommand<Unit, Unit> NextGroupPageCommand { get; }
		public ReactiveCommand<IList?, Unit> UnmarkSelectedCommand { get; }
		public ReactiveCommand<Unit, Unit> PruneMissingFilesCommand { get; }
		public ReactiveCommand<Unit, Unit> ClearAllCommand { get; }

		string scanDirectoryInput = string.Empty;
		public string ScanDirectoryInput {
			get => scanDirectoryInput;
			set => this.RaiseAndSetIfChanged(ref scanDirectoryInput, value);
		}
		string scanDirectoryQuery = string.Empty;
		public string ScanDirectoryQuery {
			get => scanDirectoryQuery;
			set => this.RaiseAndSetIfChanged(ref scanDirectoryQuery, value);
		}
		string resultPathQuery = string.Empty;
		public string ResultPathQuery {
			get => resultPathQuery;
			set => this.RaiseAndSetIfChanged(ref resultPathQuery, value);
		}
		string newExcludedPath = string.Empty;
		public string NewExcludedPath {
			get => newExcludedPath;
			set => this.RaiseAndSetIfChanged(ref newExcludedPath, value);
		}
		bool newSkipScan;
		public bool NewSkipScan {
			get => newSkipScan;
			set => this.RaiseAndSetIfChanged(ref newSkipScan, value);
		}
		string excludedFileQuery = string.Empty;
		public string ExcludedFileQuery {
			get => excludedFileQuery;
			set => this.RaiseAndSetIfChanged(ref excludedFileQuery, value);
		}
		string excludedFileSummary = "点击“加载 / 刷新记录”按页读取历史单文件排除，不会复制整份数据库。";
		public string ExcludedFileSummary {
			get => excludedFileSummary;
			private set => this.RaiseAndSetIfChanged(ref excludedFileSummary, value);
		}
		bool isLoadingExcludedFiles;
		public bool IsLoadingExcludedFiles {
			get => isLoadingExcludedFiles;
			private set {
				this.RaiseAndSetIfChanged(ref isLoadingExcludedFiles, value);
				UpdateNavigation();
			}
		}
		string groupQuery = string.Empty;
		public string GroupQuery {
			get => groupQuery;
			set => this.RaiseAndSetIfChanged(ref groupQuery, value);
		}
		string groupPageSummary = string.Empty;
		public string GroupPageSummary {
			get => groupPageSummary;
			private set => this.RaiseAndSetIfChanged(ref groupPageSummary, value);
		}
		bool isLoadingGroups;
		public bool IsLoadingGroups {
			get => isLoadingGroups;
			private set {
				this.RaiseAndSetIfChanged(ref isLoadingGroups, value);
				UpdateNavigation();
			}
		}
		string scanDirectoryPageSummary = string.Empty;
		public string ScanDirectoryPageSummary {
			get => scanDirectoryPageSummary;
			private set => this.RaiseAndSetIfChanged(ref scanDirectoryPageSummary, value);
		}
		string resultPathPageSummary = string.Empty;
		public string ResultPathPageSummary {
			get => resultPathPageSummary;
			private set => this.RaiseAndSetIfChanged(ref resultPathPageSummary, value);
		}
		string statusMessage = string.Empty;
		public string StatusMessage {
			get => statusMessage;
			private set => this.RaiseAndSetIfChanged(ref statusMessage, value);
		}

		int groupPageIndex, scanDirectoryPageIndex, resultPathPageIndex, excludedFilePageIndex;
		bool groupHasNext, scanDirectoryHasNext, resultPathHasNext, excludedFilesLoaded;
		int excludedFileTotal;
		public bool CanPreviousGroupPage => !IsLoadingGroups && groupPageIndex > 0;
		public bool CanNextGroupPage => !IsLoadingGroups && groupHasNext;
		public bool CanPreviousScanDirectoryPage => scanDirectoryPageIndex > 0;
		public bool CanNextScanDirectoryPage => scanDirectoryHasNext;
		public bool CanPreviousResultPathPage => resultPathPageIndex > 0;
		public bool CanNextResultPathPage => resultPathHasNext;
		public bool CanPreviousExcludedFilePage => !IsLoadingExcludedFiles && excludedFilePageIndex > 0;
		public bool CanNextExcludedFilePage =>
			!IsLoadingExcludedFiles && excludedFilesLoaded &&
			(long)(excludedFilePageIndex + 1) * ExcludedFilePageSize < excludedFileTotal;

		public BlacklistManagerVM(List<HashSet<string>> liveList, Func<Task> persist,
			ObservableCollection<ResultPathExclusion>? pathRules = null,
			Func<string?, bool, string?>? addResultPath = null,
			Action? pathRulesChanged = null,
			ObservableCollection<string>? scanDirectories = null,
			Func<string?, int, int, Task<ManuallyExcludedPage>>? loadExcludedFiles = null,
			Func<IReadOnlyList<string>, Task<int>>? restoreExcludedFiles = null,
			Action? scanDirectoriesChanged = null) {
			_liveList = liveList;
			_persist = persist;
			PathRules = pathRules ?? new();
			ScanDirectories = scanDirectories ?? new();
			this.addResultPath = addResultPath;
			this.pathRulesChanged = pathRulesChanged;
			this.loadExcludedFiles = loadExcludedFiles;
			this.restoreExcludedFiles = restoreExcludedFiles;
			this.scanDirectoriesChanged = scanDirectoriesChanged;

			// All initial pages are bounded and render immediately; avoid any full-list
			// Count + OrderBy + string.Join on startup. Search is explicitly requested.
			RefreshGroupPreview();
			RefreshScanDirectoryPage();
			RefreshResultPathPage();

			UnmarkSelectedCommand = ReactiveCommand.CreateFromTask<IList?>(UnmarkSelectedAsync);
			PruneMissingFilesCommand = ReactiveCommand.CreateFromTask(PruneMissingAsync);
			ClearAllCommand = ReactiveCommand.CreateFromTask(ClearAllAsync);
			SearchGroupCommand = ReactiveCommand.CreateFromTask(() => LoadGroupPageAsync(0));
			PreviousGroupPageCommand = ReactiveCommand.CreateFromTask(() => LoadGroupPageAsync(groupPageIndex - 1));
			NextGroupPageCommand = ReactiveCommand.CreateFromTask(() => LoadGroupPageAsync(groupPageIndex + 1));

			AddResultPathCommand = ReactiveCommand.Create(() => {
				if (this.addResultPath == null) return;
				try {
					string? error = this.addResultPath(NewExcludedPath, NewSkipScan);
					StatusMessage = error ?? string.Empty;
					if (error == null) NewExcludedPath = string.Empty;
					ResetResultPathPage();
				}
				catch (Exception ex) { StatusMessage = $"保存排除规则失败：{ex.Message}"; }
			});
			RemoveResultPathsCommand = ReactiveCommand.Create<IList?>(selected => {
				if (selected == null || selected.Count == 0) return;
				foreach (var entry in selected.OfType<ResultPathExclusion>().ToList())
					PathRules.Remove(entry);
				OnResultPathChanged();
				ResetResultPathPage();
			});
			SearchResultPathCommand = ReactiveCommand.Create(() => {
				resultPathPageIndex = 0;
				RefreshResultPathPage();
			});
			PreviousResultPathPageCommand = ReactiveCommand.Create(() => {
				if (CanPreviousResultPathPage) { resultPathPageIndex--; RefreshResultPathPage(); }
			});
			NextResultPathPageCommand = ReactiveCommand.Create(() => {
				if (CanNextResultPathPage) { resultPathPageIndex++; RefreshResultPathPage(); }
			});

			AddScanDirectoryCommand = ReactiveCommand.Create(() => {
				if (!ResultPathExclusion.TryNormalize(ScanDirectoryInput, out var folder, out var error)) {
					StatusMessage = error;
					return;
				}
				if (ScanDirectories.Contains(folder, PathComparer.ForCurrentPlatform)) {
					ScanDirectoryInput = string.Empty;
					StatusMessage = string.Empty;
					return;
				}
				ChangeScanDirectories(() => ScanDirectories.Add(folder));
				if (string.IsNullOrEmpty(StatusMessage))
					ScanDirectoryInput = string.Empty;
			});
			RemoveScanDirectoriesCommand = ReactiveCommand.Create<IList?>(selected => {
				if (selected == null || selected.Count == 0) return;
				var paths = selected.OfType<string>().ToList();
				if (!paths.Any(ScanDirectories.Contains)) return;
				ChangeScanDirectories(() => {
					foreach (string path in paths) ScanDirectories.Remove(path);
				});
			});
			SearchScanDirectoryCommand = ReactiveCommand.Create(() => {
				scanDirectoryPageIndex = 0;
				RefreshScanDirectoryPage();
			});
			PreviousScanDirectoryPageCommand = ReactiveCommand.Create(() => {
				if (CanPreviousScanDirectoryPage) { scanDirectoryPageIndex--; RefreshScanDirectoryPage(); }
			});
			NextScanDirectoryPageCommand = ReactiveCommand.Create(() => {
				if (CanNextScanDirectoryPage) { scanDirectoryPageIndex++; RefreshScanDirectoryPage(); }
			});

			LoadExcludedFilesCommand = ReactiveCommand.CreateFromTask(() => QueryExcludedFilePageAsync(0));
			SearchExcludedFilesCommand = ReactiveCommand.CreateFromTask(() => QueryExcludedFilePageAsync(0));
			PreviousExcludedFilePageCommand =
				ReactiveCommand.CreateFromTask(() => QueryExcludedFilePageAsync(excludedFilePageIndex - 1));
			NextExcludedFilePageCommand =
				ReactiveCommand.CreateFromTask(() => QueryExcludedFilePageAsync(excludedFilePageIndex + 1));
			RestoreExcludedFilesCommand = ReactiveCommand.CreateFromTask<IList?>(RestoreSelectedExcludedFilesAsync);
		}

		// Read-only, first N matching members of a possibly huge live collection.
		internal static (List<T> Rows, bool HasNext) PageOf<T>(
			IEnumerable<T> source, string? query, Func<T, string> key, int pageIndex, int pageSize) {
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(key);
			if (pageIndex < 0 || pageSize <= 0)
				throw new ArgumentOutOfRangeException(nameof(pageIndex));
			long offset = (long)pageIndex * pageSize;
			long seen = 0;
			var rows = new List<T>(pageSize);
			string needle = query?.Trim() ?? string.Empty;
			foreach (T item in source) {
				if (needle.Length != 0 && !key(item).Contains(needle, StringComparison.OrdinalIgnoreCase))
					continue;
				if (seen++ < offset) continue;
				if (rows.Count == pageSize) return (rows, true);
				rows.Add(item);
			}
			return (rows, false);
		}

		internal static (List<BlacklistEntryVM> Rows, bool HasNext) GroupPageOf(
			IEnumerable<HashSet<string>> groups, string? query, int pageIndex, int pageSize = GroupPageSize) {
			// Searching one giant group scans its paths only when the user explicitly
			// presses Search. The first page with an empty query avoids this entirely.
			string needle = query?.Trim() ?? string.Empty;
			var page = PageOf(groups, needle,
				set => needle.Length == 0 ? string.Empty :
					set.FirstOrDefault(path => path.Contains(needle, StringComparison.OrdinalIgnoreCase)) ?? string.Empty,
				pageIndex, pageSize);
			return (page.Rows.Select(set => new BlacklistEntryVM(set)).ToList(), page.HasNext);
		}

		void UpdateNavigation() {
			this.RaisePropertyChanged(nameof(CanPreviousGroupPage));
			this.RaisePropertyChanged(nameof(CanNextGroupPage));
			this.RaisePropertyChanged(nameof(CanPreviousExcludedFilePage));
			this.RaisePropertyChanged(nameof(CanNextExcludedFilePage));
			this.RaisePropertyChanged(nameof(CanPreviousScanDirectoryPage));
			this.RaisePropertyChanged(nameof(CanNextScanDirectoryPage));
			this.RaisePropertyChanged(nameof(CanPreviousResultPathPage));
			this.RaisePropertyChanged(nameof(CanNextResultPathPage));
		}

		void ApplyGroupPage((List<BlacklistEntryVM> Rows, bool HasNext) page, int target) {
			groupPageIndex = target;
			groupHasNext = page.HasNext;
			Entries.Clear();
			foreach (var entry in page.Rows) Entries.Add(entry);
			GroupPageSummary = $"共 {_liveList.Count:N0} 个误判组 · 第 {target + 1:N0} 页 · 显示 {page.Rows.Count:N0} 个" +
				(page.HasNext ? " · 还有下一页" : "");
			this.RaisePropertyChanged(nameof(GroupCountText));
			this.RaisePropertyChanged(nameof(IsEmpty));
			UpdateNavigation();
		}
		void RefreshGroupPreview() => ApplyGroupPage(GroupPageOf(_liveList, string.Empty, 0), 0);

		async Task LoadGroupPageAsync(int page) {
			if (IsLoadingGroups || page < 0) return;
			IsLoadingGroups = true;
			try {
				// Snapshot ONLY group references, not paths, to avoid a concurrent
				// mutation while the explicit filtered search runs off-thread.
				HashSet<string>[] snapshot = _liveList.ToArray();
				string query = GroupQuery;
				var result = await Task.Run(() => GroupPageOf(snapshot, query, page));
				if (result.Rows.Count == 0 && page > 0) {
					groupPageIndex = page - 1;
					result = await Task.Run(() => GroupPageOf(snapshot, query, groupPageIndex));
					page = groupPageIndex;
				}
				ApplyGroupPage(result, page);
				StatusMessage = string.Empty;
			}
			catch (Exception ex) { StatusMessage = $"查询误判组失败：{ex.Message}"; }
			finally { IsLoadingGroups = false; }
		}

		void RefreshScanDirectoryPage() {
			var page = PageOf(ScanDirectories, ScanDirectoryQuery, s => s,
				scanDirectoryPageIndex, FolderPageSize);
			VisibleScanDirectories.Clear();
			foreach (var path in page.Rows) VisibleScanDirectories.Add(path);
			scanDirectoryHasNext = page.HasNext;
			ScanDirectoryPageSummary = $"原版扫描排除共 {ScanDirectories.Count:N0} 项 · 第 {scanDirectoryPageIndex + 1:N0} 页 · 显示 {page.Rows.Count} 条" +
				(page.HasNext ? " · 还有下一页" : "");
			UpdateNavigation();
		}
		void ResetScanDirectoryPage() {
			scanDirectoryPageIndex = 0;
			ScanDirectoryQuery = string.Empty;
			RefreshScanDirectoryPage();
		}

		void RefreshResultPathPage() {
			var page = PageOf(PathRules, ResultPathQuery, rule => rule.Path,
				resultPathPageIndex, FolderPageSize);
			VisiblePathRules.Clear();
			foreach (var rule in page.Rows) VisiblePathRules.Add(rule);
			resultPathHasNext = page.HasNext;
			ResultPathPageSummary = $"结果目录排除共 {PathRules.Count:N0} 项 · 第 {resultPathPageIndex + 1:N0} 页 · 显示 {page.Rows.Count} 条" +
				(page.HasNext ? " · 还有下一页" : "");
			UpdateNavigation();
		}
		void ResetResultPathPage() {
			resultPathPageIndex = 0;
			ResultPathQuery = string.Empty;
			RefreshResultPathPage();
		}

		// Persist original scan blacklist with immediate rollback on failure.
		void ChangeScanDirectories(Action change) {
			string[] previous = ScanDirectories.ToArray();
			try {
				change();
				scanDirectoriesChanged?.Invoke();
				StatusMessage = string.Empty;
			}
			catch (Exception ex) {
				ScanDirectories.Clear();
				foreach (string path in previous) ScanDirectories.Add(path);
				StatusMessage = $"保存扫描目录排除失败：{ex.Message}";
			}
			ResetScanDirectoryPage();
		}

		async Task QueryExcludedFilePageAsync(int page) {
			if (page < 0 || IsLoadingExcludedFiles || loadExcludedFiles == null) return;
			IsLoadingExcludedFiles = true;
			ExcludedFileSummary = "正在按页查询历史单文件排除（仅扫描内存索引）…";
			try {
				var result = await loadExcludedFiles(ExcludedFileQuery, page, ExcludedFilePageSize);
				if (result.Paths.Count == 0 && page > 0) {
					page = Math.Max(0, (result.TotalCount - 1) / ExcludedFilePageSize);
					result = await loadExcludedFiles(ExcludedFileQuery, page, ExcludedFilePageSize);
				}
				ExcludedFiles.Clear();
				foreach (string path in result.Paths) ExcludedFiles.Add(path);
				excludedFilePageIndex = page;
				excludedFileTotal = result.TotalCount;
				excludedFilesLoaded = true;
				int start = page * ExcludedFilePageSize + (result.Paths.Count > 0 ? 1 : 0);
				int end = page * ExcludedFilePageSize + result.Paths.Count;
				ExcludedFileSummary = $"匹配 {result.TotalCount:N0} 个历史排除文件，显示第 {start:N0}–{end:N0} 条（第 {page + 1:N0} 页）";
				StatusMessage = string.Empty;
			}
			catch (Exception ex) {
				ExcludedFileSummary = "查询失败，未更改原数据库。可以重试。";
				StatusMessage = $"查询历史单文件排除失败：{ex.Message}";
			}
			finally { IsLoadingExcludedFiles = false; }
		}

		async Task RestoreSelectedExcludedFilesAsync(IList? selection) {
			if (IsLoadingExcludedFiles || restoreExcludedFiles == null || selection == null) return;
			var paths = selection.OfType<string>().Distinct(PathComparer.ForCurrentPlatform).ToList();
			if (paths.Count == 0) return;
			var confirmation = await MessageBoxService.Show(
				$"确认解除 {paths.Count:N0} 个文件的历史扫描排除？将保存扫描数据库；文件需下次扫描才会重新参与匹配。",
				MessageBoxButtons.Yes | MessageBoxButtons.No);
			if (confirmation != MessageBoxButtons.Yes) return;
			IsLoadingExcludedFiles = true;
			ExcludedFileSummary = "正在更新数据库并保存，请勿启动扫描或关闭程序…";
			int restored;
			try { restored = await restoreExcludedFiles(paths); }
			catch (Exception ex) {
				ExcludedFileSummary = "保存失败，原有排除标记未确认修改。";
				StatusMessage = $"解除单文件排除失败：{ex.Message}";
				return;
			}
			finally { IsLoadingExcludedFiles = false; }
			await QueryExcludedFilePageAsync(excludedFilePageIndex);
			StatusMessage = $"已解除 {restored:N0} 个文件的排除标记。下次扫描后生效。";
		}

		public void OnResultPathChanged() {
			try {
				pathRulesChanged?.Invoke();
				StatusMessage = string.Empty;
			}
			catch (Exception ex) { StatusMessage = $"保存排除规则失败：{ex.Message}"; }
		}

		public string GroupCountText =>
			string.Format(App.Lang["BlacklistManager.GroupCount"], _liveList.Count);
		public bool IsEmpty => _liveList.Count == 0;

		async Task UnmarkSelectedAsync(IList? selected) {
			if (IsLoadingGroups || selected == null || selected.Count == 0) return;
			var toRemove = selected.OfType<BlacklistEntryVM>().Select(e => e.Source).ToHashSet();
			_liveList.RemoveAll(s => toRemove.Contains(s));
			try { await _persist(); StatusMessage = string.Empty; }
			catch (Exception ex) { StatusMessage = $"保存误判组黑名单失败：{ex.Message}"; }
			await LoadGroupPageAsync(0);
		}

		async Task PruneMissingAsync() {
			if (IsLoadingGroups) return;
			IsLoadingGroups = true;
			int removed;
			try {
				// File.Exists on remote paths can be slow. Do not block the GUI thread
				// while scanning large historic group blacklists.
				removed = await Task.Run(() => BlacklistStore.PruneMissingFiles(_liveList));
				if (removed > 0) await _persist();
				StatusMessage = removed > 0
					? string.Format(App.Lang["BlacklistManager.PruneResult"], removed)
					: App.Lang["BlacklistManager.NothingPruned"];
			}
			catch (Exception ex) { StatusMessage = $"清理误判组失败：{ex.Message}"; }
			finally { IsLoadingGroups = false; }
			await LoadGroupPageAsync(0);
		}

		async Task ClearAllAsync() {
			if (IsLoadingGroups || _liveList.Count == 0) return;
			var result = await MessageBoxService.Show(
				App.Lang["BlacklistManager.ClearAllConfirm"],
				MessageBoxButtons.Yes | MessageBoxButtons.No);
			if (result != MessageBoxButtons.Yes) return;
			_liveList.Clear();
			try { await _persist(); StatusMessage = string.Empty; }
			catch (Exception ex) { StatusMessage = $"保存误判组黑名单失败：{ex.Message}"; }
			GroupQuery = string.Empty;
			RefreshGroupPreview();
		}
	}
}