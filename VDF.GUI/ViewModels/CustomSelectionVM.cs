// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Platform.Storage.FileIO;
using ReactiveUI;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	public sealed class CustomSelectionVM : ReactiveObject {
		[JsonIgnore]
		readonly CustomSelectionView? host;
		public CustomSelectionVM(CustomSelectionView customSelectionView) {
			host = customSelectionView;
		}
		CustomSelectionData _Data = new();
		public CustomSelectionData Data {
			get => _Data;
			set {
				this.RaiseAndSetIfChanged(ref _Data, value);
				PikPakStatus = string.Empty;
				FolderCoverageStatus = string.Empty;
				FolderCoverageOptions.Clear();
				SelectedFolderCoverage = null;
			}
		}

		string _PikPakStatus = string.Empty;
		[JsonIgnore]
		public string PikPakStatus {
			get => _PikPakStatus;
			set => this.RaiseAndSetIfChanged(ref _PikPakStatus, value);
		}

		[JsonIgnore]
		public ObservableCollection<PikPakFolderCoverageOption> FolderCoverageOptions { get; } = new();

		PikPakFolderCoverageOption? _SelectedFolderCoverage;
		[JsonIgnore]
		public PikPakFolderCoverageOption? SelectedFolderCoverage {
			get => _SelectedFolderCoverage;
			set => this.RaiseAndSetIfChanged(ref _SelectedFolderCoverage, value);
		}

		string _FolderCoverageStatus = string.Empty;
		[JsonIgnore]
		public string FolderCoverageStatus {
			get => _FolderCoverageStatus;
			set => this.RaiseAndSetIfChanged(ref _FolderCoverageStatus, value);
		}

		[JsonIgnore]
		public ObservableCollection<CustomSelectionPreset> CustomSelectionPresets => SettingsFile.Instance.CustomSelectionPresets;

		[JsonIgnore]
		CustomSelectionPreset? _SelectedPreset;
		[JsonIgnore]
		public CustomSelectionPreset? SelectedPreset {
			get => _SelectedPreset;
			set {
				this.RaiseAndSetIfChanged(ref _SelectedPreset, value);
				if (value != null)
					Data = JsonSerializer.Deserialize(JsonSerializer.Serialize(value.Data, GuiJsonContext.Default.CustomSelectionData), GuiJsonContext.Default.CustomSelectionData)!;
			}
		}

		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> SavePresetCommand => ReactiveCommand.CreateFromTask(async () => {
			var name = await InputBoxService.Show(App.Lang["Preset.NamePrompt"], _SelectedPreset?.Name ?? string.Empty, title: App.Lang["Preset.SaveTitle"]);
			if (string.IsNullOrWhiteSpace(name)) return;
			var dataCopy = JsonSerializer.Deserialize(JsonSerializer.Serialize(Data, GuiJsonContext.Default.CustomSelectionData), GuiJsonContext.Default.CustomSelectionData)!;
			var existing = CustomSelectionPresets.FirstOrDefault(p => p.Name == name);
			if (existing != null)
				existing.Data = dataCopy;
			else
				CustomSelectionPresets.Add(new CustomSelectionPreset { Name = name, Data = dataCopy });
		});

		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> DeletePresetCommand => ReactiveCommand.Create(() => {
			if (_SelectedPreset != null) {
				CustomSelectionPresets.Remove(_SelectedPreset);
				SelectedPreset = null;
			}
		});

		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> RefreshFolderCoverageCommand => ReactiveCommand.Create(() => {
			var options = ApplicationHelpers.MainWindowDataContext.BuildPikPakFolderCoverageOptions();
			FolderCoverageOptions.Clear();
			foreach (var option in options)
				FolderCoverageOptions.Add(option);
			SelectedFolderCoverage = FolderCoverageOptions.FirstOrDefault();
			int review = options.Sum(option => option.ReviewOnlyGroupCount);
			FolderCoverageStatus = options.Count == 0
				? "当前可见结果中没有跨文件夹的相似资源组。"
				: $"已整理出 {options.Count:N0} 组目录关系；其中 {review:N0} 个关系内资源组因 AI/片段/版本差异被设为只复核。确认覆盖率不计这些组；文件数和大小仍完整显示。";
		});

		/// <summary>
		/// Editable preview only. Only confirmed complete-resource groups enter BEST-quality
		/// selection. AI-only, clip and material edition differences remain untouched.
		/// </summary>
		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> ApplyFolderMergeCommand => ReactiveCommand.Create(() => {
			if (SelectedFolderCoverage == null) {
				FolderCoverageStatus = "请先分析覆盖关系并选择一组目录。";
				return;
			}

			bool swapDirection = Data.PikPakFolderMergeTargetSelection == 1;
			var (target, source) = SelectedFolderCoverage.ResolveDirection(swapDirection);
			var (_, sourceCoverage) = SelectedFolderCoverage.ResolveCoverage(swapDirection);
			var result = ApplicationHelpers.MainWindowDataContext.RunPikPakFolderBestSelection(
				SelectedFolderCoverage, swapDirection);

			bool wholeAllowed = MainWindowVM.MayMergeWholeSource(sourceCoverage, result.ReviewOnlyGroups);
			string scope = wholeAllowed
				? $"确认来源资源覆盖率 {sourceCoverage:0.#}% ≥ 90%，且没有待复核组，可进一步执行“安全整合”补齐来源独有资源。"
				: result.ReviewOnlyGroups > 0
					? $"有 {result.ReviewOnlyGroups:N0} 个资源组必须人工复核；因此禁止整目录补齐，仅允许处理其他已确认匹配资源。"
					: $"确认来源资源覆盖率仅 {sourceCoverage:0.#}%：只处理已匹配资源，不会移动来源目录中的其他文件。";
			FolderCoverageStatus = result.Selected > 0
				? $"BEST 预选：{target} ← {source}；已勾选 {result.Selected:N0} 个低质量/多余版本；待复核 {result.ReviewOnlyGroups:N0} 组未改动。{scope}"
				: $"没有安全的自动淘汰项；待复核 {result.ReviewOnlyGroups:N0} 组保持原样。{scope}";
		});

		/// <summary>
		/// Explicit, confirmed consolidation. It moves BEST keepers only for confirmed
		/// complete-resource groups. Whole-source union requires >=90% confirmed coverage
		/// AND zero review-only groups. It never deletes losers.
		/// </summary>
		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> ExecuteFolderConsolidationCommand => ReactiveCommand.CreateFromTask(async () => {
			if (SelectedFolderCoverage == null) {
				FolderCoverageStatus = "请先分析覆盖关系并选择一组目录。";
				return;
			}

			var main = ApplicationHelpers.MainWindowDataContext;
			bool swapDirection = Data.PikPakFolderMergeTargetSelection == 1;
			var plan = main.BuildPikPakFolderConsolidationPlan(SelectedFolderCoverage, swapDirection);
			if (plan.TotalRelatedGroups == 0) {
				FolderCoverageStatus = "没有可整合或复核的匹配资源。";
				return;
			}
			if (plan.MatchedGroups == 0) {
				FolderCoverageStatus = $"这组目录关系的 {plan.ManualReviewGroups:N0} 个相关资源全部属于待复核版本（AI 同源、片段或明显版本差异），不会自动移动或勾选。";
				return;
			}

			string wholeSourceLine;
			if (plan.WholeSourceEligible) {
				wholeSourceLine = $"确认来源覆盖率 {plan.SourceCoverage:0.#}% ≥ 90%，且无待复核组：另外将补入 {plan.UniqueSourceFiles.Count:N0} 个来源独有文件（{plan.UniqueSourceBytes.BytesToString()}）。";
			}
			else if (plan.ManualReviewGroups > 0) {
				wholeSourceLine = $"待复核 {plan.ManualReviewGroups:N0} 组：即使表面覆盖率较高，也禁止整目录补齐；来源未匹配文件全部保持原位。";
			}
			else {
				wholeSourceLine = $"确认来源覆盖率 {plan.SourceCoverage:0.#}% < 90%：来源未匹配文件全部保持原位。";
			}

			string message =
				$"目标合集：{plan.TargetFolder}\n" +
				$"来源目录：{plan.SourceFolder}\n\n" +
				$"可自动整合：{plan.MatchedGroups:N0} 组\n" +
				$"只复核、不自动处理：{plan.ManualReviewGroups:N0} 组\n" +
				$"需要把 BEST 移入目标：{plan.KeeperMoveCount:N0} 个\n" +
				$"成功后仅勾选、不会自动删除：最多 {plan.LoserCount:N0} 个低质量/多余版本\n" +
				wholeSourceLine + "\n\n" +
				"同盘使用文件系统移动；跨盘先完整复制并校验 SHA-256，校验成功后才删除来源。任何 BEST 移动失败时，该资源组的旧版本都不会被勾选。继续？";

			var confirmed = await MessageBoxService.Show(message, MessageBoxButtons.Yes | MessageBoxButtons.No,
				defaultButton: MessageBoxButtons.No);
			if (confirmed != MessageBoxButtons.Yes)
				return;

			main.IsBusy = true;
			main.IsBusyOverlayText = "正在安全整合最高质量资源…";
			FolderConsolidationResult result;
			try {
				result = await main.ExecutePikPakFolderConsolidationAsync(plan);
			}
			finally {
				main.IsBusy = false;
			}

			FolderCoverageStatus =
				$"整合完成：准备 {result.GroupsPrepared:N0}/{plan.MatchedGroups:N0} 个确认资源组；" +
				$"待复核 {plan.ManualReviewGroups:N0} 组未动；BEST 移入 {result.KeeperMovesSucceeded:N0} 个；" +
				$"来源独有文件移入 {result.UniqueMovesSucceeded:N0} 个；已勾选 {result.SafeLosersMarked:N0} 个待删除复核版本。" +
				(result.GroupMoveFailures + result.UniqueMoveFailures > 0
					? $" 有 {result.GroupMoveFailures + result.UniqueMoveFailures:N0} 个移动失败，原文件保持不删，请查看日志。"
					: " 没有自动删除任何文件。");
		});

		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> SelectCommand => ReactiveCommand.Create(() => {
			var action = (PikPakQuickAction)Data.PikPakActionSelection;
			if (action != PikPakQuickAction.Disabled) {
				bool needsKeyword = action is PikPakQuickAction.KeepPathContainingKeyword
					or PikPakQuickAction.KeepFileNameContainingKeyword
					or PikPakQuickAction.SelectPathContainingKeyword
					or PikPakQuickAction.SelectFileNameContainingKeyword;
				if (needsKeyword && string.IsNullOrWhiteSpace(Data.PikPakKeyword)) {
					PikPakStatus = "请输入关键词后再执行。";
					return;
				}

				bool needsTargetPaths = action is PikPakQuickAction.SelectInsideTargetPaths
					or PikPakQuickAction.SelectOutsideTargetPaths;
				if (needsTargetPaths && string.IsNullOrWhiteSpace(Data.PikPakTargetPaths)) {
					PikPakStatus = "请输入至少一个目标目录（每行一个）后再执行。";
					return;
				}

				int selected = ApplicationHelpers.MainWindowDataContext.RunPikPakSelection(Data);
				PikPakStatus = selected > 0
					? $"已按当前结果范围勾选 {selected:N0} 个文件；筛选隐藏项不会被修改。"
					: "当前结果范围内没有符合该规则的重复项，原勾选状态未修改。";
				return;
			}

			PikPakStatus = string.Empty;
			ApplicationHelpers.MainWindowDataContext.RunCustomSelection(Data);
		});
		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> CancelCommand => ReactiveCommand.Create(() => {
			host?.Close(MessageBoxButtons.Cancel);
		});
		[JsonIgnore]
		public ReactiveCommand<ListBox, Action> AddFilePathContainsTextToListCommand => ReactiveCommand.CreateFromTask<ListBox, Action>(async lbox => {
			var result = await PromptForWildcardEntryAsync(App.Lang["Dialog.Add"], string.Empty);
			if (string.IsNullOrEmpty(result)) return null!;
			if (!Data.PathContains.Contains(result))
				Data.PathContains.Add(result);
			return null!;
		});
		[JsonIgnore]
		public ReactiveCommand<ListBox, Action> RemoveFilePathContainsTextFromListCommand => ReactiveCommand.Create<ListBox, Action>(lbox => {
			while (lbox.SelectedItems?.Count > 0)
				Data.PathContains.Remove((string)lbox.SelectedItems[0]!);
			return null!;
		});
		[JsonIgnore]
		public ReactiveCommand<ListBox, Action> AddFilePathNotContainsTextToListCommand => ReactiveCommand.CreateFromTask<ListBox, Action>(async lbox => {
			var result = await PromptForWildcardEntryAsync(App.Lang["Dialog.Add"], string.Empty);
			if (string.IsNullOrEmpty(result)) return null!;
			if (!Data.PathNotContains.Contains(result))
				Data.PathNotContains.Add(result);
			return null!;
		});
		[JsonIgnore]
		public ReactiveCommand<ListBox, Action> RemoveFilePathNotContainsTextFromListCommand => ReactiveCommand.Create<ListBox, Action>(lbox => {
			while (lbox.SelectedItems?.Count > 0)
				Data.PathNotContains.Remove((string)lbox.SelectedItems[0]!);
			return null!;
		});
		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> SaveCommand => ReactiveCommand.CreateFromTask(async () => {
			var result = await Utils.PickerDialogUtils.SaveFilePicker(new FilePickerSaveOptions() {
				SuggestedStartLocation = await ApplicationHelpers.MainWindow.StorageProvider.TryGetFolderFromPathAsync(CoreUtils.CurrentFolder),
				DefaultExtension = ".vdfselection",
				FileTypeChoices = new FilePickerFileType[] {
					 new FilePickerFileType("Selection File") { Patterns = new string[] { "*.vdfselection" }}}
			});
			if (string.IsNullOrEmpty(result)) return;

			try {
				File.WriteAllText(result, JsonSerializer.Serialize(Data, GuiJsonContext.Default.CustomSelectionData));
			}
			catch (Exception ex) {
				await MessageBoxService.Show($"Saving to file has failed: {ex.Message}");
			}
		});
		[JsonIgnore]
		public ReactiveCommand<Unit, Unit> LoadCommand => ReactiveCommand.CreateFromTask(async () => {
			var result = await Utils.PickerDialogUtils.OpenFilePicker(new FilePickerOpenOptions() {
				SuggestedStartLocation = await ApplicationHelpers.MainWindow.StorageProvider.TryGetFolderFromPathAsync(CoreUtils.CurrentFolder),
				FileTypeFilter = new FilePickerFileType[] {
					 new FilePickerFileType("Selection File") { Patterns = new string[] { "*.vdfselection" }}}
			});
			if (string.IsNullOrEmpty(result)) return;

			try {
				Data = JsonSerializer.Deserialize(File.ReadAllText(result), GuiJsonContext.Default.CustomSelectionData)!;
			}
			catch (Exception ex) {
				await MessageBoxService.Show($"Loading from file has failed: {ex.Message}");
			}
		});

		static async Task<string?> PromptForWildcardEntryAsync(string title, string initialValue) {
			var currentValue = initialValue;
			while (true) {
				var result = await InputBoxService.Show(App.Lang["CustomSelection.NewEntry"], currentValue, title: title);
				if (string.IsNullOrEmpty(result))
					return null;
				if (HasTrailingWildcard(result))
					return result;
				await MessageBoxService.Show(App.Lang["CustomSelection.WildcardRequired"]);
				currentValue = result;
			}
		}

		static bool HasTrailingWildcard(string value) =>
			value.EndsWith("*", StringComparison.Ordinal) || value.EndsWith("?", StringComparison.Ordinal);
	}
}
