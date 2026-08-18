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

using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
	public class DatabaseViewer : Window {
		readonly ListBox list;
		DatabaseViewerVM VM => (DatabaseViewerVM)DataContext!;

		public DatabaseViewer() {
			InitializeComponent();
			list = this.FindControl<ListBox>("dbList")!;
			BindFreshViewModel();
			Owner = ApplicationHelpers.MainWindow;
			Closing += DatabaseViewer_Closing;
			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

			list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
			AddImportDatabaseButton();
		}

		void BindFreshViewModel() {
			var vm = new DatabaseViewerVM {
				SelectionProvider = () => list.SelectedItems?.OfType<DatabaseEntryVM>() ?? Enumerable.Empty<DatabaseEntryVM>(),
			};
			DataContext = vm;
			var commandMap = new Dictionary<string, ICommand> {
				["DB_DeleteSelectedEntries"] = vm.DeleteSelectedEntries,
			};
			KeyboardShortcutManager.Instance.ApplyBindings(list, commandMap);
		}

		/// <summary>
		/// Keep the existing XAML/localization surface untouched: the custom import action is
		/// appended to the database viewer status strip at runtime. It remains visible even
		/// when no row is selected, which is where a library-level operation belongs.
		/// </summary>
		void AddImportDatabaseButton() {
			if (Content is not Grid root)
				return;
			var statusBorder = root.Children
				.OfType<Border>()
				.FirstOrDefault(border => Grid.GetRow(border) == 4);
			if (statusBorder?.Child is not DockPanel dock)
				return;
			var left = dock.Children.OfType<StackPanel>().FirstOrDefault();
			if (left == null)
				return;

			var button = new Button {
				Content = "导入 VDF 数据库…",
				FontSize = 12,
			};
			button.Classes.Add("pencil");
			button.Click += OnImportDatabaseClicked;
			left.Children.Add(button);
		}

		async void OnImportDatabaseClicked(object? sender, RoutedEventArgs e) {
			if (ApplicationHelpers.MainWindow.DataContext is MainWindowVM main && (main.IsScanning || main.IsBusy)) {
				await MessageBoxService.Show("扫描或其他数据库操作正在进行，请完成后再导入数据库。");
				return;
			}

			var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
				Title = "选择原版 VDF 的 ScannedFiles.db",
				AllowMultiple = false,
			});
			if (files.Count == 0)
				return;

			string path;
			try {
				path = files[0].Path.LocalPath;
			}
			catch (Exception) {
				await MessageBoxService.Show("请选择本机或已映射磁盘上的 VDF 数据库文件。");
				return;
			}

			Button? importButton = sender as Button;
			if (importButton != null) importButton.IsEnabled = false;
			try {
				// Persist edits made in the viewer first. The native importer then backs up this
				// exact state before adding anything, so an import can never silently replace it.
				VM.Save();
				DatabaseImportPreview preview = await Task.Run(() => ScanEngine.PreviewNativeDatabaseImport(path));
				string legacyNote = preview.LegacyImageEntriesNeedingRehash > 0
					? $"\n旧版图片哈希：{preview.LegacyImageEntriesNeedingRehash:N0} 个图片条目将在下次扫描重新生成（视频缓存不受影响）。"
					: string.Empty;
				string message =
					$"来源：{preview.SourcePath}\n" +
					$"格式：{preview.SourceFormat} · DB v{preview.SourceVersion}\n" +
					$"来源数据库文件：{preview.SourceDatabaseBytes.BytesToString()}\n" +
					$"来源索引：{preview.SourceEntryCount:N0} 个文件 · {preview.SourceMediaBytes.BytesToString()}\n\n" +
					$"当前数据库：{preview.CurrentEntryCount:N0} 个文件 · {preview.CurrentMediaBytes.BytesToString()} · DB {preview.CurrentDatabaseBytes.BytesToString()}\n" +
					$"可新增：{preview.NewEntryCount:N0} 个文件 · {preview.NewMediaBytes.BytesToString()}\n" +
					$"同路径已存在：{preview.ExistingPathCount:N0} 个文件（保留当前版本，不覆盖）" + legacyNote +
					"\n\n导入只合并缺少的路径；执行前会自动备份当前 ScannedFiles.db。AI 的 UnionEmbeddings.db 不包含在此次导入中。";

				if (preview.NewEntryCount == 0) {
					await MessageBoxService.Show(message + "\n\n没有需要新增的条目。", title: "导入 VDF 数据库");
					return;
				}

				var confirm = await MessageBoxService.Show(
					message + "\n\n确认导入？",
					MessageBoxButtons.Yes | MessageBoxButtons.No,
					"导入 VDF 数据库",
					MessageBoxButtons.No);
				if (confirm != MessageBoxButtons.Yes)
					return;

				DatabaseImportResult result = await Task.Run(() => ScanEngine.ImportNativeDatabase(path));
				// The previous VM was already saved above. Rebuild from the now-merged live DB
				// so closing this window cannot write a stale pre-import snapshot back over it.
				BindFreshViewModel();
				string backup = string.IsNullOrEmpty(result.BackupPath) ? "未生成（无新增条目）" : result.BackupPath;
				await MessageBoxService.Show(
					$"导入完成。\n\n" +
					$"新增：{result.ImportedEntryCount:N0} 个文件 · {result.ImportedMediaBytes.BytesToString()}\n" +
					$"跳过同路径：{result.Preview.ExistingPathCount:N0} 个文件\n" +
					$"当前数据库：{result.FinalEntryCount:N0} 个文件 · {result.FinalMediaBytes.BytesToString()}\n" +
					$"备份：{backup}\n\n" +
					"现在可直接重新比较/扫描；已有灰度、pHash、媒体信息和音频指纹会按原 VDF 缓存继续复用。",
					title: "导入 VDF 数据库");
			}
			catch (Exception ex) {
				Logger.Instance.Error($"VDF database import failed: {ex}");
				await MessageBoxService.Show(
					$"数据库导入失败，当前数据库不会被覆盖。\n\n{ex.Message}",
					title: "导入 VDF 数据库");
			}
			finally {
				if (importButton != null) importButton.IsEnabled = true;
			}
		}

		void DatabaseViewer_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
			=> VM.Save();

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);

		public void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
			=> VM.SelectedCount = list.SelectedItems?.Count ?? 0;

		// F2 starts the explicit path edit on the focused row (same as ✎ / double-click)
		void OnListKeyDown(object? sender, KeyEventArgs e) {
			if (e.Key != Key.F2) return;
			if (list.SelectedItems?.OfType<DatabaseEntryVM>().FirstOrDefault() is { } entry) {
				entry.BeginPathEdit();
				e.Handled = true;
			}
		}

		public void OnListDoubleTapped(object? sender, TappedEventArgs e) {
			// Ignore double-taps on interactive children (chips, pencil, the editor itself)
			if (e.Source is Control c && c.FindAncestorOfType<Button>(includeSelf: true) != null) return;
			if (e.Source is Control t && t.FindAncestorOfType<TextBox>(includeSelf: true) != null) return;
			if ((e.Source as Control)?.DataContext is DatabaseEntryVM entry)
				entry.BeginPathEdit();
		}

		// The edit TextBox materializes when IsEditingPath flips — grab focus then.
		public void OnPathEditorAttached(object? sender, VisualTreeAttachmentEventArgs e) {
			if (sender is TextBox box)
				Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
		}

		public void OnPathEditorKeyDown(object? sender, KeyEventArgs e) {
			if (sender is not TextBox box || box.DataContext is not DatabaseEntryVM entry) return;
			if (e.Key == Key.Enter) {
				// Moving focus pushes the pending text through the LostFocus binding,
				// which also fires CommitPathEdit below.
				list.Focus();
				e.Handled = true;
			}
			else if (e.Key == Key.Escape) {
				entry.CancelPathEdit();
				list.Focus();
				e.Handled = true;
			}
		}

		public void OnPathEditorLostFocus(object? sender, RoutedEventArgs e) {
			if (sender is TextBox box && box.DataContext is DatabaseEntryVM { IsEditingPath: true } entry)
				entry.CommitPathEdit();
		}
	}
}
