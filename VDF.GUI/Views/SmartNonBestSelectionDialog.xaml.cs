// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
	public class SmartNonBestSelectionDialog : Window {
		TextBox FileNameKeywordsTextBox => this.FindControl<TextBox>("FileNameKeywordsTextBox")!;
		TextBox PathKeywordsTextBox => this.FindControl<TextBox>("PathKeywordsTextBox")!;

		public SmartNonBestSelectionDialog() {
			InitializeComponent();
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
		}

		void OnApplyClicked(object? sender, RoutedEventArgs e) => Close(new SmartNonBestSelectionOptions(
			(FileNameKeywordsTextBox.Text ?? string.Empty).Trim(),
			(PathKeywordsTextBox.Text ?? string.Empty).Trim()));

		void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);
	}
}
