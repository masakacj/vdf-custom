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

		public ResourceConsolidationDialog() => InitializeComponent();

		public ResourceConsolidationDialog(string summary, string initialPath, bool multipleSeries) {
			InitializeComponent();
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			SummaryText.Text = summary + (multipleSeries
				? "\n多个系列时，此处作为总目标目录，每个系列会建立自己的系列根目录。"
				: "\n单个系列时，此处就是最终系列根目录。");
			DestinationTextBox.Text = initialPath;
			if (!VDF.GUI.Data.SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
			Opened += (_, _) => { DestinationTextBox.Focus(); DestinationTextBox.SelectAll(); };
		}

		async void OnBrowseClicked(object? sender, RoutedEventArgs e) {
			var result = await PickerDialogUtils.OpenDialogPicker(new FolderPickerOpenOptions {
				Title = "选择资源整合目标目录",
				AllowMultiple = false,
			});
			if (result == null || result.Count == 0 || string.IsNullOrWhiteSpace(result[0]))
				return;
			DestinationTextBox.Text = result[0];
		}

		async void OnOkClicked(object? sender, RoutedEventArgs e) {
			string path = (DestinationTextBox.Text ?? string.Empty).Trim();
			if (path.Length == 0) {
				await MessageBoxService.Show("请输入或选择资源整合目标目录。", title: "资源整合");
				return;
			}
			try {
				path = Path.GetFullPath(path);
			}
			catch (Exception ex) {
				await MessageBoxService.Show($"目标路径无效：{ex.Message}", title: "资源整合");
				return;
			}
			Close(path);
		}

		void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);
	}
}
