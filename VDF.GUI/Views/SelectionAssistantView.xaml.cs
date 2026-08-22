// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
	public class SelectionAssistantView : Window {
		public SelectionAssistantView() {
			InitializeComponent();
		}

		internal SelectionAssistantView(MainWindowVM main) {
			InitializeComponent();
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
			DataContext = new SelectionAssistantVM(this, main);
		}

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);
	}
}
