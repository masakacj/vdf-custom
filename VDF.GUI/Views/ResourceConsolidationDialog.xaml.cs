// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using VDF.GUI.Utils;

namespace VDF.GUI.Views {
	public class ResourceConsolidationDialog : Window {
		TextBox DestinationTextBox => this.FindControl<TextBox>("DestinationTextBox")!;
		TextBlock SummaryText => this.FindControl<TextBlock>("SummaryText")!;
		StackPanel CandidatePanel => this.FindControl<StackPanel>("CandidatePanel")!;
		ComboBox CandidateComboBox => this.FindControl<ComboBox>("CandidateComboBox")!;

		public ResourceConsolidationDialog() => InitializeComponent();

		public ResourceConsolidationDialog(
			string summary,
			string initialPath,
			bool multipleSeries,
			IReadOnlyList<string>? candidatePaths = null) {
			InitializeComponent();
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			SummaryText.Text = summary + (multipleSeries
				? "\n多个系列时，此处作为总目标目录，每个系列会建立自己的系列根目录。"
				: "\n单个系列时，此处就是最终系列根目录；修改目标后会重新计算增减和最终目录树。");
			DestinationTextBox.Text = initialPath;

			var candidates = (candidatePaths ?? Array.Empty<string>())
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(path => {
					try { return Path.GetFullPath(path); }
					catch { return path; }
				})
				.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
				.ToList();
			CandidatePanel.IsVisible = !multipleSeries && candidates.Count > 1;
			if (CandidatePanel.IsVisible) {
				CandidateComboBox.ItemsSource = candidates;
				int initialIndex = candidates.FindIndex(path => PathsEqual(path, initialPath));
				CandidateComboBox.SelectedIndex = initialIndex >= 0 ? initialIndex : 0;
			}

			if (!VDF.GUI.Data.SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
			Opened += (_, _) => { DestinationTextBox.Focus(); DestinationTextBox.SelectAll(); };
		}

		void OnCandidateSelectionChanged(object? sender, SelectionChangedEventArgs e) {
			if (CandidateComboBox.SelectedItem is string path && !string.IsNullOrWhiteSpace(path))
				DestinationTextBox.Text = path;
		}

		async void OnBrowseClicked(object? sender, RoutedEventArgs e) {
			var result = await PickerDialogUtils.OpenDialogPicker(new FolderPickerOpenOptions {
				Title = "选择文件夹合并目标目录",
				AllowMultiple = false,
			});
			if (result == null || result.Count == 0 || string.IsNullOrWhiteSpace(result[0]))
				return;
			DestinationTextBox.Text = result[0];
		}

		async void OnOkClicked(object? sender, RoutedEventArgs e) {
			string path = (DestinationTextBox.Text ?? string.Empty).Trim();
			if (path.Length == 0) {
				await MessageBoxService.Show("请输入或选择文件夹合并目标目录。", title: "文件夹合并");
				return;
			}
			try {
				path = Path.GetFullPath(path);
			}
			catch (Exception ex) {
				await MessageBoxService.Show($"目标路径无效：{ex.Message}", title: "文件夹合并");
				return;
			}
			Close(path);
		}

		void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

		static bool PathsEqual(string a, string b) {
			try {
				return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
					OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
			}
			catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
		}

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);
	}
}
