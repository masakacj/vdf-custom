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
			}
		}

		string _PikPakStatus = string.Empty;
		[JsonIgnore]
		public string PikPakStatus {
			get => _PikPakStatus;
			set => this.RaiseAndSetIfChanged(ref _PikPakStatus, value);
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