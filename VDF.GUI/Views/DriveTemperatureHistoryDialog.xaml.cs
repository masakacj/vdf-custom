// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using VDF.GUI.Data;

namespace VDF.GUI.Views {
	public sealed class DriveTemperatureHistoryDialog : Window {
		readonly ScanDriveRow row;
		TemperatureHistoryChart Chart => this.FindControl<TemperatureHistoryChart>("Chart")!;
		TextBlock ResumeLegend => this.FindControl<TextBlock>("ResumeLegend")!;
		TextBlock WarnLegend => this.FindControl<TextBlock>("WarnLegend")!;
		TextBlock PauseLegend => this.FindControl<TextBlock>("PauseLegend")!;
		TextBlock StartTimeText => this.FindControl<TextBlock>("StartTimeText")!;
		TextBlock EndTimeText => this.FindControl<TextBlock>("EndTimeText")!;
		TextBlock StatsText => this.FindControl<TextBlock>("StatsText")!;
		TextBlock NoDataText => this.FindControl<TextBlock>("NoDataText")!;

		public DriveTemperatureHistoryDialog() : this(new ScanDriveRow(string.Empty, string.Empty)) { }

		public DriveTemperatureHistoryDialog(ScanDriveRow row) {
			this.row = row;
			AvaloniaXamlLoader.Load(this);
			DataContext = row;
			Title = $"{App.Lang["Scan.HddThermalTitle"]} · {row.Root}";
			Chart.Attach(row);
			row.TemperatureHistory.CollectionChanged += HistoryChanged;
			SettingsFile.Instance.PropertyChanged += SettingsChanged;
			Closed += (_, _) => {
				row.TemperatureHistory.CollectionChanged -= HistoryChanged;
				SettingsFile.Instance.PropertyChanged -= SettingsChanged;
				Chart.Detach();
			};
			RefreshThresholds();
			RefreshStats();
		}

		void SettingsChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName is nameof(SettingsFile.HddProtectionWarnTemperatureC)
				or nameof(SettingsFile.HddProtectionPauseTemperatureC)
				or nameof(SettingsFile.HddProtectionResumeTemperatureC))
				RefreshThresholds();
		}

		void HistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshStats();

		void RefreshThresholds() {
			int warn = SettingsFile.Instance.HddProtectionWarnTemperatureC;
			int pause = SettingsFile.Instance.HddProtectionPauseTemperatureC;
			int resume = SettingsFile.Instance.HddProtectionResumeTemperatureC;
			ResumeLegend.Text = $"{resume}°C";
			WarnLegend.Text = $"{warn}°C";
			PauseLegend.Text = $"{pause}°C";
			Chart.SetThresholds(warn, pause, resume);
		}

		void RefreshStats() {
			if (row.TemperatureHistory.Count == 0) {
				NoDataText.IsVisible = true;
				StartTimeText.Text = string.Empty;
				EndTimeText.Text = string.Empty;
				StatsText.Text = string.Empty;
				return;
			}
			NoDataText.IsVisible = false;
			DriveTemperatureSample first = row.TemperatureHistory[0];
			DriveTemperatureSample last = row.TemperatureHistory[^1];
			int min = row.TemperatureHistory.Min(x => x.TemperatureC);
			int max = row.TemperatureHistory.Max(x => x.TemperatureC);
			StartTimeText.Text = first.Utc.ToLocalTime().ToString("MM-dd HH:mm");
			EndTimeText.Text = last.Utc.ToLocalTime().ToString("MM-dd HH:mm");
			StatsText.Text = $"{App.Lang["Scan.HddChartCurrent"]} {last.TemperatureC}°C · " +
				$"{App.Lang["Scan.HddChartRange"]} {min}–{max}°C · " +
				$"{App.Lang["Scan.HddChartSamples"]} {row.TemperatureHistory.Count:N0}";
		}
	}
}
