// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using VDF.Core.Utils;
using VDF.GUI.Controls;
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
		StackPanel AutomaticPanel => this.FindControl<StackPanel>("AutomaticPanel")!;
		StackPanel ManualReviewPanel => this.FindControl<StackPanel>("ManualReviewPanel")!;
		TextBlock AutomaticHeader => this.FindControl<TextBlock>("AutomaticHeader")!;
		TextBlock ManualHeader => this.FindControl<TextBlock>("ManualHeader")!;
		Button ConfirmAllManualButton => this.FindControl<Button>("ConfirmAllManualButton")!;
		Button ClearAllManualButton => this.FindControl<Button>("ClearAllManualButton")!;
		Expander AutomaticExpander => this.FindControl<Expander>("AutomaticExpander")!;
		Expander ManualExpander => this.FindControl<Expander>("ManualExpander")!;
		TabItem ReviewTab => this.FindControl<TabItem>("ReviewTab")!;
		TabItem DeletionTab => this.FindControl<TabItem>("DeletionTab")!;
		TabControl DetailTabs => this.FindControl<TabControl>("DetailTabs")!;

		readonly Dictionary<Guid, DuplicateItemVM> keeperOverrides = new();
		readonly List<CheckBox> manualAcceptBoxes = new();
		Func<IReadOnlyDictionary<Guid, DuplicateItemVM>, ResourceSeriesConsolidationPreview>? previewFactory;
		int manualReviewCount;
		bool legacyBooleanResult;
		bool suppressInteractivePreviewRefresh;

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
			AutomaticPanel.Children.Add(InfoBox("此旧版预览流程未提供系统确认组明细。"));
			ManualReviewPanel.Children.Add(InfoBox("此旧版预览流程不支持在窗口内处理人工复核。"));
			AutomaticHeader.Text = "系统确认可合并";
			UpdateReviewHeaders();
		}

		internal ResourceConsolidationPreviewDialog(
			ResourceSeriesConsolidationPreview preview,
			IReadOnlyList<ResourceSeriesConfirmedReview> confirmedReviews,
			IReadOnlyList<ResourceSeriesManualReview> manualReviews,
			Func<IReadOnlyDictionary<Guid, DuplicateItemVM>, ResourceSeriesConsolidationPreview> previewFactory) {
			InitializeComponent();
			ConfigureWindow();
			this.previewFactory = previewFactory;
			manualReviewCount = manualReviews.Count;
			BuildAutomaticReviewCards(confirmedReviews);
			BuildManualReviewCards(manualReviews);
			ApplyPreview(preview);
			UpdateReviewHeaders();
			AutomaticExpander.IsExpanded = false;
			ManualExpander.IsExpanded = true;
			DetailTabs.SelectedItem = ReviewTab;
		}

		void ConfigureWindow() {
			Owner = ApplicationHelpers.MainWindow;
			Icon = ApplicationHelpers.MainWindow.Icon;
			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
		}

		void BuildAutomaticReviewCards(IReadOnlyList<ResourceSeriesConfirmedReview> reviews) {
			if (reviews.Count == 0) {
				AutomaticPanel.Children.Add(InfoBox("当前没有达到“确认 BEST”门槛的资源组。所有匹配组都需要人工审核，或因路径/覆盖安全条件保持原位。"));
				AutomaticHeader.Text = "系统确认可合并 (0)";
				return;
			}

			int loserCount = reviews.Sum(review => Math.Max(0, review.Candidates.Count - 1));
			long reclaim = MainWindowVM.ComputeConfirmedReclaimBytes(
				reviews.SelectMany(review => review.Candidates.Where(candidate => !ReferenceEquals(candidate, review.ConfirmedKeeper))));
			AutomaticHeader.Text = $"系统确认可合并 ({reviews.Count:N0} 组 · {loserCount:N0} 个副本 · {reclaim.BytesToString()})";

			int number = 0;
			foreach (ResourceSeriesConfirmedReview review in reviews) {
				number++;
				var body = new StackPanel { Spacing = 7 };
				body.Children.Add(GroupTitle(
					$"✓ 系统确认 {number:N0} · 资源组 {review.GroupId.ToString()[..8]}",
					review.ConfirmationReason,
					good: true));

				foreach (DuplicateItemVM candidate in review.Candidates) {
					bool keeper = ReferenceEquals(candidate, review.ConfirmedKeeper);
					var selector = new CheckBox {
						IsChecked = !keeper,
						IsEnabled = false,
						Content = keeper ? "保留" : "删除",
						VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
					};
					var badge = new TextBlock {
						Text = keeper ? "✓ 确认 BEST" : "☑ 合并后清理",
						FontSize = 10.5,
						FontWeight = FontWeight.SemiBold,
						Foreground = keeper ? GoodBrush() : DangerBrush(),
					};
					body.Children.Add(BuildCandidateRow(candidate, selector, badge, keeper));
				}

				long bytes = MainWindowVM.ComputeConfirmedReclaimBytes(
					review.Candidates.Where(candidate => !ReferenceEquals(candidate, review.ConfirmedKeeper)));
				body.Children.Add(new TextBlock {
					Text = $"将保留 1 个确认 BEST · 清理 {Math.Max(0, review.Candidates.Count - 1):N0} 个副本 · 预计释放 {bytes.BytesToString()}",
					FontSize = 11,
					Opacity = 0.72,
				});

				AutomaticPanel.Children.Add(GroupBorder(body, good: true));
			}
		}

		void BuildManualReviewCards(IReadOnlyList<ResourceSeriesManualReview> reviews) {
			manualAcceptBoxes.Clear();
			if (reviews.Count == 0) {
				ManualReviewPanel.Children.Add(InfoBox("当前没有需要人工审核的资源组。所有可自动处理的组都已达到确认 BEST 门槛。"));
				return;
			}

			int number = 0;
			foreach (ResourceSeriesManualReview review in reviews) {
				number++;
				DuplicateItemVM selected = review.RecommendedKeeper;
				var badges = new Dictionary<DuplicateItemVM, TextBlock>(ReferenceEqualityComparer<DuplicateItemVM>.Instance);
				var radios = new Dictionary<DuplicateItemVM, RadioButton>(ReferenceEqualityComparer<DuplicateItemVM>.Instance);
				var accept = new CheckBox {
					Content = "确认当前保留选择；其余副本纳入本次清理",
					FontWeight = FontWeight.SemiBold,
				};
				manualAcceptBoxes.Add(accept);
				var status = new TextBlock {
					Text = "尚未确认：该组保持原样",
					FontSize = 11,
					Opacity = 0.62,
				};

				var body = new StackPanel { Spacing = 7 };
				body.Children.Add(GroupTitle(
					$"⚠ 人工审核 {number:N0} · 资源组 {review.GroupId.ToString()[..8]}",
					review.RecommendationReason,
					good: false));

				string groupName = "merge-review-" + review.GroupId.ToString("N");
				foreach (DuplicateItemVM candidate in review.Candidates) {
					bool recommended = ReferenceEquals(candidate, review.RecommendedKeeper);
					var radio = new RadioButton {
						GroupName = groupName,
						IsChecked = recommended,
						Content = "保留",
						VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
					};
					var badge = new TextBlock {
						Text = recommended ? "★ 推荐 BEST · 待确认" : "候选副本",
						FontSize = 10.5,
						FontWeight = recommended ? FontWeight.SemiBold : FontWeight.Normal,
						Foreground = recommended ? WarnBrush() : MutedBrush(),
					};
					radios[candidate] = radio;
					badges[candidate] = badge;
					body.Children.Add(BuildCandidateRow(candidate, radio, badge, recommended));
				}

				void RefreshBadges() {
					bool confirmed = accept.IsChecked == true;
					foreach (DuplicateItemVM candidate in review.Candidates) {
						TextBlock badge = badges[candidate];
						if (!confirmed) {
							bool recommended = ReferenceEquals(candidate, review.RecommendedKeeper);
							badge.Text = recommended ? "★ 推荐 BEST · 待确认" : "候选副本";
							badge.Foreground = recommended ? WarnBrush() : MutedBrush();
							continue;
						}
						bool keeper = ReferenceEquals(candidate, selected);
						badge.Text = keeper ? "✓ 人工确认保留" : "☑ 合并后清理";
						badge.Foreground = keeper ? GoodBrush() : DangerBrush();
					}
				}

				void ApplyChoice() {
					if (accept.IsChecked == true) {
						keeperOverrides[review.GroupId] = selected;
						status.Text = ReferenceEquals(selected, review.RecommendedKeeper)
							? "已确认：采用系统推荐 BEST；其余副本将进入删除/释放统计"
							: "已确认：使用你手动选择的保留副本；其余副本将进入删除/释放统计";
						status.Opacity = 0.9;
					}
					else {
						keeperOverrides.Remove(review.GroupId);
						status.Text = "尚未确认：该组保持原样";
						status.Opacity = 0.62;
					}
					RefreshBadges();
					RefreshInteractivePreview();
				}

				foreach (var pair in radios) {
					DuplicateItemVM candidate = pair.Key;
					RadioButton radio = pair.Value;
					radio.IsCheckedChanged += (_, _) => {
						if (radio.IsChecked != true) return;
						selected = candidate;
						// Deliberately changing away from the preselected recommendation is an
						// explicit human review action. Accept it immediately. If the user keeps
						// the default recommendation, the confirmation checkbox remains required.
						if (!ReferenceEquals(candidate, review.RecommendedKeeper) && accept.IsChecked != true)
							accept.IsChecked = true;
						else if (accept.IsChecked == true)
							ApplyChoice();
					};
				}
				accept.IsCheckedChanged += (_, _) => ApplyChoice();

				body.Children.Add(accept);
				body.Children.Add(status);
				ManualReviewPanel.Children.Add(GroupBorder(body, good: false));
			}
		}

		Border BuildCandidateRow(
			DuplicateItemVM candidate,
			Control selector,
			TextBlock actionBadge,
			bool emphasize) {
			var row = new DockPanel { LastChildFill = true, MinHeight = 86 };

			var selectorHost = new Border {
				Width = 76,
				Padding = new Thickness(4),
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
				Child = selector,
			};
			DockPanel.SetDock(selectorHost, Dock.Left);
			row.Children.Add(selectorHost);

			var previewHost = new Border {
				Width = 190,
				MinHeight = 78,
				Margin = new Thickness(0, 3, 8, 3),
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
			};
			var bitmap = candidate.Thumbnail;
			if (bitmap != null) {
				previewHost.Child = new WrappedFilmstrip {
					Source = bitmap,
					FrameCount = candidate.ThumbnailFrameCount,
					GridColumns = candidate.ThumbnailGridColumns,
					Compact = true,
					HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
					VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
				};
			}
			else {
				previewHost.Child = new TextBlock {
					Text = "暂无 thumbnail",
					FontSize = 11,
					Opacity = 0.5,
					HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
					VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
				};
			}
			DockPanel.SetDock(previewHost, Dock.Left);
			row.Children.Add(previewHost);

			var sizeCell = new StackPanel {
				Width = 108,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
				Spacing = 2,
			};
			sizeCell.Children.Add(new TextBlock {
				Text = Math.Max(0, candidate.ItemInfo.SizeLong).BytesToString(),
				FontSize = 12,
				FontWeight = FontWeight.SemiBold,
			});
			sizeCell.Children.Add(new TextBlock {
				Text = candidate.ItemInfo.DateCreated == default ? string.Empty : candidate.ItemInfo.DateCreated.ToString("yyyy-MM-dd"),
				FontSize = 10.5,
				Opacity = 0.62,
			});
			DockPanel.SetDock(sizeCell, Dock.Right);
			row.Children.Add(sizeCell);

			var bitrateCell = new StackPanel {
				Width = 110,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
				Spacing = 2,
			};
			if (candidate.ItemInfo.IsImage) {
				bitrateCell.Children.Add(new TextBlock { Text = "图片", FontSize = 11.5, Opacity = 0.65 });
			}
			else {
				bitrateCell.Children.Add(new TextBlock {
					Text = candidate.ItemInfo.BitRateKbs > 0 ? $"{candidate.ItemInfo.BitRateKbs / 1000m:0.##} Mb/s" : "码率 ?",
					FontSize = 12,
				});
				bitrateCell.Children.Add(new TextBlock {
					Text = candidate.ItemInfo.Fps > 0 ? $"{candidate.ItemInfo.Fps:0.##} fps" : string.Empty,
					FontSize = 10.5,
					Opacity = 0.68,
				});
			}
			DockPanel.SetDock(bitrateCell, Dock.Right);
			row.Children.Add(bitrateCell);

			var qualityCell = new StackPanel {
				Width = 126,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
				Spacing = 2,
			};
			qualityCell.Children.Add(new TextBlock {
				Text = string.IsNullOrWhiteSpace(candidate.ItemInfo.FrameSize) ? "分辨率 ?" : candidate.ItemInfo.FrameSize,
				FontSize = 12,
				FontWeight = FontWeight.SemiBold,
			});
			qualityCell.Children.Add(new TextBlock {
				Text = CandidateFormatLine(candidate),
				FontSize = 10.5,
				Opacity = 0.68,
				TextWrapping = TextWrapping.Wrap,
			});
			DockPanel.SetDock(qualityCell, Dock.Right);
			row.Children.Add(qualityCell);

			var identity = new StackPanel {
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
				Spacing = 3,
				Margin = new Thickness(4, 0, 8, 0),
			};
			identity.Children.Add(actionBadge);
			identity.Children.Add(new TextBlock {
				Text = Path.GetFileName(candidate.ItemInfo.Path),
				FontSize = 12.5,
				FontWeight = emphasize ? FontWeight.SemiBold : FontWeight.Normal,
				TextTrimming = TextTrimming.CharacterEllipsis,
			});
			var pathText = new TextBlock {
				Text = candidate.ItemInfo.Path,
				FontSize = 10.8,
				Opacity = 0.65,
				TextTrimming = TextTrimming.CharacterEllipsis,
			};
			ToolTip.SetTip(pathText, candidate.ItemInfo.Path);
			identity.Children.Add(pathText);
			row.Children.Add(identity);

			return new Border {
				Padding = new Thickness(6, 4),
				CornerRadius = new CornerRadius(6),
				BorderThickness = new Thickness(1),
				BorderBrush = emphasize ? GoodBorderBrush() : NeutralBorderBrush(),
				Background = emphasize ? GoodSoftBrush() : Brushes.Transparent,
				Child = row,
			};
		}

		static string CandidateFormatLine(DuplicateItemVM candidate) {
			var info = candidate.ItemInfo;
			var parts = new List<string>();
			if (!string.IsNullOrWhiteSpace(info.Format)) parts.Add(info.Format);
			if (!string.IsNullOrWhiteSpace(info.HdrFormat)) parts.Add(info.HdrFormat);
			if (!info.IsImage && !string.IsNullOrWhiteSpace(info.AudioFormat)) parts.Add(info.AudioFormat);
			if (!info.IsImage && !string.IsNullOrWhiteSpace(info.AudioChannel)) parts.Add(info.AudioChannel);
			return parts.Count == 0 ? "格式 ?" : string.Join(" · ", parts);
		}

		static StackPanel GroupTitle(string title, string reason, bool good) {
			var stack = new StackPanel { Spacing = 3 };
			stack.Children.Add(new TextBlock {
				Text = title,
				FontSize = 13,
				FontWeight = FontWeight.SemiBold,
				Foreground = good ? GoodBrush() : WarnBrush(),
			});
			stack.Children.Add(new TextBlock {
				Text = reason,
				FontSize = 11.2,
				Opacity = 0.74,
				TextWrapping = TextWrapping.Wrap,
			});
			return stack;
		}

		static Border GroupBorder(Control body, bool good) => new() {
			Padding = new Thickness(11),
			CornerRadius = new CornerRadius(8),
			BorderThickness = new Thickness(1),
			BorderBrush = good ? GoodBorderBrush() : WarnBorderBrush(),
			Background = good ? GoodSoftBrush() : Brushes.Transparent,
			Child = body,
		};

		static Border InfoBox(string text) => new() {
			Padding = new Thickness(12),
			CornerRadius = new CornerRadius(8),
			BorderThickness = new Thickness(1),
			BorderBrush = NeutralBorderBrush(),
			Child = new TextBlock {
				Text = text,
				Opacity = 0.7,
				TextWrapping = TextWrapping.Wrap,
			},
		};

		void SetAllManualReviewsConfirmed(bool confirmed) {
			if (manualAcceptBoxes.Count == 0) {
				UpdateReviewHeaders();
				return;
			}

			suppressInteractivePreviewRefresh = true;
			try {
				foreach (CheckBox accept in manualAcceptBoxes) {
					if (accept.IsChecked != confirmed)
						accept.IsChecked = confirmed;
				}
			}
			finally {
				suppressInteractivePreviewRefresh = false;
			}

			RefreshInteractivePreview();
			UpdateReviewHeaders();
		}

		void RefreshInteractivePreview() {
			if (suppressInteractivePreviewRefresh || previewFactory == null) return;
			ApplyPreview(previewFactory(keeperOverrides));
			UpdateReviewHeaders();
		}

		void UpdateReviewHeaders() {
			ManualHeader.Text = manualReviewCount == 0
				? "需要人工选择 (0)"
				: $"需要人工选择 ({keeperOverrides.Count:N0}/{manualReviewCount:N0} 已确认)";
			ConfirmAllManualButton.IsEnabled = manualReviewCount > 0 && keeperOverrides.Count < manualReviewCount;
			ClearAllManualButton.IsEnabled = keeperOverrides.Count > 0;
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

		void OnConfirmAllManualClicked(object? sender, RoutedEventArgs e) => SetAllManualReviewsConfirmed(true);

		void OnClearAllManualClicked(object? sender, RoutedEventArgs e) => SetAllManualReviewsConfirmed(false);

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

		static IBrush GoodBrush() => new SolidColorBrush(Color.Parse(SettingsFile.Instance.DarkMode ? "#62D59A" : "#137A4C"));
		static IBrush WarnBrush() => new SolidColorBrush(Color.Parse(SettingsFile.Instance.DarkMode ? "#E6B85C" : "#8A5A00"));
		static IBrush DangerBrush() => new SolidColorBrush(Color.Parse(SettingsFile.Instance.DarkMode ? "#F08A8A" : "#B42318"));
		static IBrush MutedBrush() => new SolidColorBrush(Color.Parse(SettingsFile.Instance.DarkMode ? "#A7AFB7" : "#667085"));
		static IBrush GoodSoftBrush() => new SolidColorBrush(Color.Parse(SettingsFile.Instance.DarkMode ? "#14271E" : "#F0FAF5"));
		static IBrush GoodBorderBrush() => new SolidColorBrush(Color.Parse(SettingsFile.Instance.DarkMode ? "#2F654A" : "#B7E4CE"));
		static IBrush WarnBorderBrush() => new SolidColorBrush(Color.Parse(SettingsFile.Instance.DarkMode ? "#6E5423" : "#E9D39E"));
		static IBrush NeutralBorderBrush() => new SolidColorBrush(Color.Parse(SettingsFile.Instance.DarkMode ? "#3A4046" : "#D0D5DD"));
	}
}