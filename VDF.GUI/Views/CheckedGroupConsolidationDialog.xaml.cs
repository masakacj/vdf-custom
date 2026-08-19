// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
	public class CheckedGroupConsolidationDialog : Window {
		TextBlock SummaryText => this.FindControl<TextBlock>("SummaryText")!;
		ComboBox KeeperComboBox => this.FindControl<ComboBox>("KeeperComboBox")!;
		ComboBox FolderComboBox => this.FindControl<ComboBox>("FolderComboBox")!;
		TextBox DestinationFolderTextBox => this.FindControl<TextBox>("DestinationFolderTextBox")!;
		TextBlock BestReasonText => this.FindControl<TextBlock>("BestReasonText")!;
		TextBlock FinalPathText => this.FindControl<TextBlock>("FinalPathText")!;
		TextBlock ReleaseText => this.FindControl<TextBlock>("ReleaseText")!;

		readonly IReadOnlyList<DuplicateItemVM> candidates;
		readonly BestRecommendation recommendation;
		readonly List<string> folders;

		public CheckedGroupConsolidationDialog() {
			candidates = Array.Empty<DuplicateItemVM>();
			recommendation = null!;
			folders = new List<string>();
			InitializeComponent();
		}

		public CheckedGroupConsolidationDialog(
			IReadOnlyList<DuplicateItemVM> candidates,
			BestRecommendation recommendation) {
			this.candidates = candidates;
			this.recommendation = recommendation;
			folders = candidates.Select(CandidateFolder)
				.Where(folder => !string.IsNullOrWhiteSpace(folder))
				.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
				.ToList();
			InitializeComponent();
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

			SummaryText.Text = $"同一相似组中已勾选 {candidates.Count:N0} 个副本。默认选中系统推荐 BEST；你可以改选保留副本和目标文件夹。";
			KeeperComboBox.ItemsSource = candidates.Select(CandidateDisplay).ToList();
			int bestIndex = candidates.ToList().FindIndex(item => ReferenceEquals(item, recommendation.Winner));
			KeeperComboBox.SelectedIndex = bestIndex >= 0 ? bestIndex : 0;
			FolderComboBox.ItemsSource = folders;
			string bestFolder = CandidateFolder(recommendation.Winner);
			int folderIndex = folders.FindIndex(folder => PathsEqual(folder, bestFolder));
			FolderComboBox.SelectedIndex = folderIndex >= 0 ? folderIndex : (folders.Count > 0 ? 0 : -1);
			if (FolderComboBox.SelectedItem is string selectedFolder)
				DestinationFolderTextBox.Text = selectedFolder;
			BestReasonText.Text = recommendation.Reason;
			UpdatePreview();
		}

		static string CandidateDisplay(DuplicateItemVM item) {
			var info = item.ItemInfo;
			var parts = new List<string>();
			parts.Add(Path.GetFileName(info.Path));
			if (!string.IsNullOrWhiteSpace(info.FrameSize)) parts.Add(info.FrameSize);
			if (!info.IsImage && info.BitRateKbs > 0) parts.Add($"{info.BitRateKbs / 1000m:0.##} Mb/s");
			if (!string.IsNullOrWhiteSpace(info.Format)) parts.Add(info.Format);
			if (info.SizeLong > 0) parts.Add(info.SizeLong.BytesToString());
			return string.Join(" · ", parts) + "  |  " + info.Path;
		}

		static string CandidateFolder(DuplicateItemVM item) {
			string folder = !string.IsNullOrWhiteSpace(item.ItemInfo.Folder)
				? item.ItemInfo.Folder
				: Path.GetDirectoryName(item.ItemInfo.Path) ?? string.Empty;
			try { return Path.GetFullPath(folder); }
			catch { return folder; }
		}

		void OnChoiceChanged(object? sender, SelectionChangedEventArgs e) {
			if (candidates.Count == 0) return;
			int index = KeeperComboBox.SelectedIndex;
			if (index >= 0 && index < candidates.Count) {
				DuplicateItemVM chosen = candidates[index];
				BestReasonText.Text = ReferenceEquals(chosen, recommendation.Winner)
					? recommendation.Reason
					: $"你已手动改选此副本。系统原推荐：{recommendation.Reason}";
			}
			UpdatePreview();
		}

		void OnFolderChanged(object? sender, SelectionChangedEventArgs e) {
			if (FolderComboBox.SelectedItem is string folder)
				DestinationFolderTextBox.Text = folder;
			UpdatePreview();
		}

		void OnDestinationFolderTextChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

		async void OnBrowseClicked(object? sender, RoutedEventArgs e) {
			var result = await PickerDialogUtils.OpenDialogPicker(new FolderPickerOpenOptions {
				Title = "选择最终保存目录",
				AllowMultiple = false,
			});
			if (result == null || result.Count == 0 || string.IsNullOrWhiteSpace(result[0])) return;
			DestinationFolderTextBox.Text = result[0];
		}

		void UpdatePreview() {
			if (!TryCurrentChoice(out DuplicateItemVM? keeper, out string folder, out string destination)) {
				FinalPathText.Text = "请选择有效的保留副本和目标目录";
				ReleaseText.Text = string.Empty;
				return;
			}
			FinalPathText.Text = destination;
			long reclaim = candidates
				.Where(item => !ReferenceEquals(item, keeper))
				.Sum(item => Math.Max(0, item.ItemInfo.SizeLong));
			ReleaseText.Text = $"本次会保留 1 个，处理其余 {Math.Max(0, candidates.Count - 1):N0} 个副本；全部成功清理后预计释放 {reclaim.BytesToString()}。目标目录：{folder}";
		}

		bool TryCurrentChoice(out DuplicateItemVM keeper, out string folder, out string destination) {
			keeper = null!;
			folder = (DestinationFolderTextBox.Text ?? string.Empty).Trim();
			destination = string.Empty;
			int index = KeeperComboBox.SelectedIndex;
			if (index < 0 || index >= candidates.Count || folder.Length == 0) return false;
			keeper = candidates[index];
			try { folder = Path.GetFullPath(folder); }
			catch { return false; }

			DuplicateItemVM? anchor = null;
			if (PathsEqual(CandidateFolder(keeper), folder)) {
				anchor = keeper;
			}
			else {
				anchor = candidates.FirstOrDefault(item => PathsEqual(CandidateFolder(item), folder));
			}
			string fileName = anchor != null ? Path.GetFileName(anchor.ItemInfo.Path) : Path.GetFileName(keeper.ItemInfo.Path);
			if (string.IsNullOrWhiteSpace(fileName)) return false;
			destination = Path.GetFullPath(Path.Combine(folder, fileName));
			return true;
		}

		async void OnOkClicked(object? sender, RoutedEventArgs e) {
			if (!TryCurrentChoice(out DuplicateItemVM keeper, out string folder, out string destination)) {
				await MessageBoxService.Show("请选择有效的保留副本和保存目录。", title: "合并所勾选副本");
				return;
			}
			Close(new CheckedGroupConsolidationDialogResult(keeper, folder, destination));
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
