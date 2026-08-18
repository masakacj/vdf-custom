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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
	public partial class SetupView : UserControl {
		public SetupView() {
			AvaloniaXamlLoader.Load(this);
			AttachLightweightQualityOption();
			// Dropping folders anywhere on the setup screen includes them;
			// holding Alt while dropping excludes them instead.
			DragDrop.SetAllowDrop(this, true);
			AddHandler(DragDrop.DropEvent, OnDrop);
			AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
		}

		MainWindowVM? ViewModel => DataContext as MainWindowVM;

		/// <summary>
		/// Adds the custom cache-only quality switch next to the three fixed scan modes without
		/// changing the upstream profile XAML. The analysis runs after matching and only consumes
		/// cached 32x32 VDF samples, so enabling it never adds media reads/seeks on HDDs.
		/// </summary>
		void AttachLightweightQualityOption() {
			var startButton = this.GetVisualDescendants()
				.OfType<Button>()
				.FirstOrDefault(button => Equals(button.CommandParameter, "FullScan"));
			if (startButton?.Parent is not StackPanel scanRow || scanRow.Parent is not StackPanel host)
				return;

			var check = new CheckBox {
				Content = "轻量画质诊断（疑似二次转码 / 水印）",
				FontSize = 12.5,
				FontWeight = FontWeight.SemiBold,
			};
			check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MainWindowVM.EnableLightweightQualityDiagnostics)) {
				Mode = BindingMode.TwoWay,
			});
			ToolTip.SetTip(check,
				"仅在相似文件已经匹配完成后分析，不额外打开、Seek 或解码视频；复用 ScannedFiles.db 中已有的灰度采样和元数据。可随时关闭。");

			var hint = new TextBlock {
				Text = "仅分析已匹配重复组，复用缓存采样；对媒体 HDD 无额外读取。疑似结果只影响 BEST 建议，不会自动删除。",
				FontSize = 11.5,
				Opacity = 0.62,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(24, 1, 0, 0),
			};
			var panel = new StackPanel {
				Spacing = 1,
				Margin = new Thickness(0, 14, 0, 0),
			};
			panel.Children.Add(check);
			panel.Children.Add(hint);

			int index = host.Children.IndexOf(scanRow);
			if (index < 0) host.Children.Add(panel);
			else host.Children.Insert(index, panel);
		}

		void OnDrop(object? sender, DragEventArgs e) {
			if (!e.DataTransfer.Contains(DataFormat.File)) return;
			bool exclude = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
			foreach (var item in e.DataTransfer.GetItems(DataFormat.File) ?? Array.Empty<IDataTransferItem>()) {
				string? path = item.TryGetFile()?.TryGetLocalPath();
				if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
				var target = exclude ? SettingsFile.Instance.Blacklists : SettingsFile.Instance.Includes;
				if (!target.Contains(path))
					target.Add(path);
			}
		}

		void OnProfileCardPressed(object? sender, PointerPressedEventArgs e) {
			if ((sender as Control)?.DataContext is ScanProfileOptionVM option)
				ViewModel?.SelectScanProfileCommand.Execute(option).Subscribe();
		}

		void OnAdvancedSettingsClick(object? sender, RoutedEventArgs e) {
			if (ViewModel != null)
				ViewModel.ActiveShellView = Data.ShellView.Settings;
		}
	}
}
