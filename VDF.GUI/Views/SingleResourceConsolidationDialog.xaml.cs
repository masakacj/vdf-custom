// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using VDF.GUI.Data;

namespace VDF.GUI.Views {
	internal sealed record SingleResourceConsolidationDialogResult(string DestinationPath, string? AnchorPath);

	public sealed class SingleResourceConsolidationDialog : Window {
		readonly string bestPath;
		readonly IReadOnlyList<string> anchors;
		ComboBox AnchorComboBox => this.FindControl<ComboBox>("AnchorComboBox")!;
		TextBox DestinationTextBox => this.FindControl<TextBox>("DestinationTextBox")!;
		SelectableTextBlock BestPathText => this.FindControl<SelectableTextBlock>("BestPathText")!;

		public SingleResourceConsolidationDialog() {
			bestPath = string.Empty;
			anchors = Array.Empty<string>();
			InitializeComponent();
		}

		internal SingleResourceConsolidationDialog(
			string bestPath,
			IReadOnlyList<string> anchors,
			string? suggestedAnchor = null) {
			this.bestPath = bestPath;
			this.anchors = anchors;
			InitializeComponent();
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

			BestPathText.Text = bestPath;
			AnchorComboBox.ItemsSource = anchors;
			if (!string.IsNullOrWhiteSpace(suggestedAnchor)) {
				int index = anchors.ToList().FindIndex(path => PathEquals(path, suggestedAnchor));
				if (index >= 0)
					AnchorComboBox.SelectedIndex = index;
			}
		}

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);

		internal static string BuildAnchoredDestination(string anchorPath, string bestPath) {
			string? folder = Path.GetDirectoryName(anchorPath);
			if (string.IsNullOrWhiteSpace(folder))
				throw new ArgumentException("目标锚点没有有效的父目录。", nameof(anchorPath));
			string stem = Path.GetFileNameWithoutExtension(anchorPath);
			string extension = Path.GetExtension(bestPath);
			return Path.Combine(folder, stem + extension);
		}

		static bool PathEquals(string a, string b) {
			try {
				return Path.GetFullPath(a).Equals(Path.GetFullPath(b),
					OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
			}
			catch { return false; }
		}

		void OnAnchorSelectionChanged(object? sender, SelectionChangedEventArgs e) {
			if (AnchorComboBox.SelectedItem is string anchor)
				DestinationTextBox.Text = BuildAnchoredDestination(anchor, bestPath);
		}

		async void OnBrowseClicked(object? sender, RoutedEventArgs e) {
			var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
				Title = "选择最终系列目录",
				AllowMultiple = false,
			});
			if (folders.Count == 0)
				return;

			string folder;
			try { folder = folders[0].Path.LocalPath; }
			catch { return; }
			string fileName = AnchorComboBox.SelectedItem is string anchor
				? Path.GetFileName(BuildAnchoredDestination(anchor, bestPath))
				: Path.GetFileName(bestPath);
			DestinationTextBox.Text = Path.Combine(folder, fileName);
		}

		async void OnOkClicked(object? sender, RoutedEventArgs e) {
			string destination = (DestinationTextBox.Text ?? string.Empty).Trim();
			if (string.IsNullOrWhiteSpace(destination)) {
				await MessageBoxService.Show("请先选择位置锚点、浏览目标目录，或直接输入最终文件路径。", title: "整合到系列");
				return;
			}

			try { destination = Path.GetFullPath(destination); }
			catch (Exception ex) {
				await MessageBoxService.Show($"最终路径无效：{ex.Message}", title: "整合到系列");
				return;
			}

			string bestExtension = Path.GetExtension(bestPath);
			if (!Path.GetExtension(destination).Equals(bestExtension,
				OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
				await MessageBoxService.Show(
					$"最终文件扩展名必须与 BEST 的真实容器一致：{bestExtension}\n\n请修改最终路径后重试。",
					title: "整合到系列");
				return;
			}

			Close(new SingleResourceConsolidationDialogResult(
				destination,
				AnchorComboBox.SelectedItem as string));
		}

		void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
	}
}
