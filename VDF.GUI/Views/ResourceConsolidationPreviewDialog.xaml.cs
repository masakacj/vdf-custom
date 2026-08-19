// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VDF.GUI.Data;
using VDF.GUI.Utils;

namespace VDF.GUI.Views {
	public class ResourceConsolidationPreviewDialog : Window {
		TextBlock ScopeText => this.FindControl<TextBlock>("ScopeText")!;
		TextBlock BeforeText => this.FindControl<TextBlock>("BeforeText")!;
		TextBlock ChangesText => this.FindControl<TextBlock>("ChangesText")!;
		TextBlock AfterText => this.FindControl<TextBlock>("AfterText")!;
		TextBlock RelationText => this.FindControl<TextBlock>("RelationText")!;
		TextBox TreeTextBox => this.FindControl<TextBox>("TreeTextBox")!;

		public ResourceConsolidationPreviewDialog() => InitializeComponent();

		public ResourceConsolidationPreviewDialog(
			string scope,
			string before,
			string changes,
			string after,
			string relations,
			string tree) {
			InitializeComponent();
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			ScopeText.Text = scope;
			BeforeText.Text = before;
			ChangesText.Text = changes;
			AfterText.Text = after;
			RelationText.Text = relations;
			TreeTextBox.Text = tree;
			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
		}

		void OnStartClicked(object? sender, RoutedEventArgs e) => Close(true);
		void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);
	}
}
