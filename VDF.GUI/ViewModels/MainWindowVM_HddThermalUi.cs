// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Reactive;
using ReactiveUI;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
		int _ScanHddWarnTemperatureC = SettingsFile.Instance.HddProtectionWarnTemperatureC;
		public int ScanHddWarnTemperatureC {
			get => _ScanHddWarnTemperatureC;
			set => this.RaiseAndSetIfChanged(ref _ScanHddWarnTemperatureC, Math.Clamp(value, 20, 80));
		}

		int _ScanHddPauseTemperatureC = SettingsFile.Instance.HddProtectionPauseTemperatureC;
		public int ScanHddPauseTemperatureC {
			get => _ScanHddPauseTemperatureC;
			set => this.RaiseAndSetIfChanged(ref _ScanHddPauseTemperatureC, Math.Clamp(value, 20, 80));
		}

		int _ScanHddResumeTemperatureC = SettingsFile.Instance.HddProtectionResumeTemperatureC;
		public int ScanHddResumeTemperatureC {
			get => _ScanHddResumeTemperatureC;
			set => this.RaiseAndSetIfChanged(ref _ScanHddResumeTemperatureC, Math.Clamp(value, 20, 80));
		}

		string _ScanHddThresholdStatus = string.Empty;
		public string ScanHddThresholdStatus {
			get => _ScanHddThresholdStatus;
			private set => this.RaiseAndSetIfChanged(ref _ScanHddThresholdStatus, value);
		}

		public ReactiveCommand<Unit, Unit> ApplyScanHddThresholdsCommand => ReactiveCommand.Create(() => {
			if (ScanHddResumeTemperatureC >= ScanHddPauseTemperatureC) {
				ScanHddThresholdStatus = App.Lang["Scan.HddThresholdInvalidResume"];
				return;
			}
			if (ScanHddWarnTemperatureC > ScanHddPauseTemperatureC) {
				ScanHddThresholdStatus = App.Lang["Scan.HddThresholdInvalidWarn"];
				return;
			}

			SettingsFile.Instance.HddProtectionWarnTemperatureC = ScanHddWarnTemperatureC;
			SettingsFile.Instance.HddProtectionPauseTemperatureC = ScanHddPauseTemperatureC;
			SettingsFile.Instance.HddProtectionResumeTemperatureC = ScanHddResumeTemperatureC;
			bool appliedLive = Scanner.UpdateHddProtectionTemperatureThresholds(
				ScanHddWarnTemperatureC, ScanHddPauseTemperatureC, ScanHddResumeTemperatureC);
			SettingsFile.SaveSettings();
			ScanDrives.RefreshThermalLabels();
			ScanHddThresholdStatus = appliedLive
				? App.Lang["Scan.HddThresholdAppliedLive"]
				: App.Lang["Scan.HddThresholdSaved"];
		});

		internal void ShowDriveTemperatureHistory(ScanDriveRow row) {
			if (row == null || !row.HddProtectionEnabled)
				return;
			var window = new DriveTemperatureHistoryDialog(row);
			window.Show(ApplicationHelpers.MainWindow);
		}
	}
}
