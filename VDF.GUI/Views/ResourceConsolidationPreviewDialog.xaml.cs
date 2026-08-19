// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
	public class ResourceConsolidationPreviewDialog : Window {
		TextBlock ScopeText => this.FindControl<TextBlock>("ScopeText")!;
		TextBlock BeforeText => this.FindControl<TextBlock>("BeforeText")!;
		TextBlock ChangesText => this.FindControl<TextBlock>("ChangesText")!;
		TextBlock AfterText => this.FindControl<TextBlock>("AfterText")!;
		TextBlock RelationText => this.FindControl<TextBlock>("RelationText")!;
		TextBox TreeTextBox => this.FindControl<TextBox>("TreeTextBox")!;
		TextBox DeletionTextBox => this.FindControl<TextBox>("DeletionTextBox")!;
		StackPanel ManualReviewPanel => this.FindControl<StackPanel>("ManualReviewPanel")!;
		TabItem ManualTab => this.FindControl<TabItem>("ManualTab")!;
		TabControl DetailTabs => this.FindControl<TabControl>("DetailTabs")!;

		readonly Dictionary<Guid, DuplicateItemVM> keeperOverrides = new();
		Func<IReadOnlyDictionary<Guid, DuplicateItemVM>, ResourceSeriesConsolidationPreview>? previewFactory;
		int manualReviewCount;
		bool legacyBooleanResult;

		public ResourceConsolidationPreviewDialog() => InitializeComponent();

		/// <summary>Compatibility constructor used by the older non-interactive workflow.</summary>
		public ResourceConsolidationPreviewDialog(
			string scope,
			string before,
			string changes,
			string after,
			string relations,
			string tree) {
			InitializeComponent();
			ConfigureWindow();
			legacyBooleanResult = true;
			ScopeText.Text = scope;
			BeforeText.Text = before;
			ChangesText.Text = changes;
			AfterText.Text = after;
			RelationText.Text = relations;
			TreeTextBox.Text = tree;
			DeletionTextBox.Text = "此旧版预览流程未提供逐项删除明细。";
			ManualReviewPanel.Children.Add(new TextBlock {
				Text = "此旧版预览流程不支持在窗口内处理人工复核。",
				Opacity = 0.65,
				TextWrapping = Avalonia.Media.TextWrapping.Wrap,
			});
		}

		internal ResourceConsolidationPreviewDialog(
			ResourceSeriesConsolidationPreview preview,
			IReadOnlyList<ResourceSeriesManualReview> manualReviews,
			Func<IReadOnlyDictionary<Guid, DuplicateItemVM>, ResourceSeriesConsolidationPreview> previewFactory) {
			InitializeComponent();
			ConfigureWindow();
			this.previewFactory = previewFactory;
			manualReviewCount = manualReviews.Count;
			BuildManualReviewCards(manualReviews);
			ApplyPreview(preview);
			UpdateManualTabHeader();
			if (manualReviews.Count > 0)
				DetailTabs.SelectedItem = ManualTab;
		}

		void ConfigureWindow() {
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
		}

		void BuildManualReviewCards(IReadOnlyList<ResourceSeriesManualReview> reviews) {
			if (reviews.Count == 0) {
				ManualReviewPanel.Children.Add(new Border {
					Padding = new Avalonia.Thickness(12),
					CornerRadius = new Avalonia.CornerRadius(8),
					BorderThickness = new Avalonia.Thickness(1),
					BorderBrush = Avalonia.Media.Brushes.Transparent,
					Child = new TextBlock {
						Text = "当前没有需要人工复核的资源组。所有可自动处理的组都已达到确认 BEST 门槛。",
						Opacity = 0.7,
						TextWrapping = Avalonia.Media.TextWrapping.Wrap,
					}
				});
				return;
			}

			int number = 0;
			foreach (ResourceSeriesManualReview review in reviews) {
				number++;
				var display = review.Candidates.Select(candidate => CandidateDisplay(candidate, review.RecommendedKeeper)).ToList();
				int recommendedIndex = review.Candidates.ToList().FindIndex(candidate => ReferenceEquals(candidate, review.RecommendedKeeper));
				var combo = new ComboBox {
					ItemsSource = display,
					SelectedIndex = recommendedIndex >= 0 ? recommendedIndex : 0,
					HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
					MinHeight = 34,
				};
				var accept = new CheckBox {
					Content = "采用此选择，并把该组纳入本次合并",
					FontWeight = Avalonia.Media.FontWeight.SemiBold,
				};
				var status = new TextBlock {
					Text = "尚未确认：该组保持原样",
					FontSize = 11,
					Opacity = 0.62,
				};

				void ApplyChoice() {
					int index = combo.SelectedIndex;
					if (accept.IsChecked == true && index >= 0 && index < review.Candidates.Count) {
						keeperOverrides[review.GroupId] = review.Candidates[index];
						status.Text = index == recommendedIndex
							? "已确认：采用系统推荐 BEST"
							: "已确认：使用你手动选择的保留副本";
						status.Opacity = 0.9;
					}
					else {
						keeperOverrides.Remove(review.GroupId);
						status.Text = "尚未确认：该组保持原样";
						status.Opacity = 0.62;
					}
					RefreshInteractivePreview();
				}

				combo.SelectionChanged += (_, _) => {
					// Changing the candidate is an explicit review action: select it and include
					// this group. The IsCheckedChanged handler performs the actual refresh.
					if (accept.IsChecked != true)
						accept.IsChecked = true;
					else
						ApplyChoice();
				};
				accept.IsCheckedChanged += (_, _) => ApplyChoice();

				var body = new StackPanel { Spacing = 7 };
				body.Children.Add(new TextBlock {
					Text = $"人工复核 {number:N0} · 资源组 {review.GroupId.ToString()[..8]}",
					FontSize = 13,
					FontWeight = Avalonia.Media.FontWeight.SemiBold,
				});
				body.Children.Add(new TextBlock {
					Text = review.RecommendationReason,
					FontSize = 11.3,
					Opacity = 0.75,
					TextWrapping = Avalonia.Media.TextWrapping.Wrap,
				});
				body.Children.Add(combo);
				body.Children.Add(accept);
				body.Children.Add(status);

				ManualReviewPanel.Children.Add(new Border {
					Padding = new Avalonia.Thickness(12),
					CornerRadius = new Avalonia.CornerRadius(8),
					BorderThickness = new Avalonia.Thickness(1),
					BorderBrush = SettingsFile.Instance.DarkMode
						? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A4642"))
						: new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D6DEDB")),
					Child = body,
				});
			}
		}

		static string CandidateDisplay(DuplicateItemVM candidate, DuplicateItemVM recommended) {
			var info = candidate.ItemInfo;
			var parts = new List<string>();
			parts.Add(ReferenceEquals(candidate, recommended) ? "★ 推荐 BEST" : "候选");
			if (!string.IsNullOrWhiteSpace(info.FrameSize)) parts.Add(info.FrameSize);
			if (!info.IsImage && info.BitRateKbs > 0) parts.Add($"{info.BitRateKbs / 1000m:0.##} Mb/s");
			if (!string.IsNullOrWhiteSpace(info.Format)) parts.Add(info.Format);
			if (info.SizeLong > 0) parts.Add(info.SizeLong.BytesToString());
			return string.Join(" · ", parts) + "  |  " + info.Path;
		}

		void RefreshInteractivePreview() {
			if (previewFactory == null) return;
			ApplyPreview(previewFactory(keeperOverrides));
			UpdateManualTabHeader();
		}

		void UpdateManualTabHeader() {
			ManualTab.Header = manualReviewCount == 0
				? "人工复核 (0)"
				: $"人工复核 ({keeperOverrides.Count:N0}/{manualReviewCount:N0} 已确认)";
		}

		void ApplyPreview(ResourceSeriesConsolidationPreview preview) {
			ScopeText.Text = preview.Scope;
			BeforeText.Text = preview.Before;
			ChangesText.Text = preview.Changes;
			AfterText.Text = preview.After;
			RelationText.Text = preview.Relations;
			TreeTextBox.Text = preview.Tree;
			DeletionTextBox.Text = preview.DeletionDetails;
		}

		void OnStartClicked(object? sender, RoutedEventArgs e) {
			if (legacyBooleanResult) {
				Close(true);
				return;
			}
			Close(new Dictionary<Guid, DuplicateItemVM>(keeperOverrides));
		}

		void OnCancelClicked(object? sender, RoutedEventArgs e) {
			if (legacyBooleanResult) {
				Close(false);
				return;
			}
			Close(null);
		}

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);
	}
}
