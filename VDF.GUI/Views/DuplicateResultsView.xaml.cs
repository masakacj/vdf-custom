// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
	public partial class DuplicateResultsView : UserControl {
		const string ConsolidateMenuTag = "vdf-single-resource-consolidate";

		public DuplicateResultsView() {
			AvaloniaXamlLoader.Load(this);
			DataContextChanged += (_, _) => WireViewModel();
			WireViewModel();
			if (this.FindControl<Button>("AutoSelectButton")?.Flyout is MenuFlyout autoSelectFlyout)
				autoSelectFlyout.Opening += (_, _) => RebuildSavedExpressionItems();
		}

		/// <summary>
		/// Saved Expression Builder presets in the Auto-select menu (#850). Rebuilt each
		/// time the flyout opens so preset adds/renames/deletes show up immediately;
		/// the submenu hides entirely while no presets exist.
		/// </summary>
		void RebuildSavedExpressionItems() {
			var menu = this.FindControl<MenuItem>("SavedExpressionsMenu");
			if (menu == null || ViewModel is not MainWindowVM vm) return;
			var presets = SettingsFile.Instance.ExpressionPresets;
			menu.IsVisible = presets.Count > 0;
			menu.ItemsSource = presets.Select(p => new MenuItem {
				Header = p.Name,
				Command = vm.ApplyExpressionPresetCommand,
				CommandParameter = p,
			}).ToList();
		}

		ListBox ResultsListControl => this.FindControl<ListBox>("ResultsList")!;

		/// <summary>The control keyboard shortcuts are attached to (see ApplyKeyboardShortcuts).</summary>
		internal ListBox ShortcutTarget => ResultsListControl;

		MainWindowVM? ViewModel => DataContext as MainWindowVM;

		void WireViewModel() {
			if (ViewModel is not MainWindowVM vm) return;
			vm.NewResultsSelectionProvider = () =>
				ResultsListControl.SelectedItems?.OfType<ResultsItemRow>().Select(r => r.Item).ToList() ?? new();
			vm.NewResultsSelectAndScrollTo = row => {
				ResultsListControl.SelectedItems?.Clear();
				ResultsListControl.SelectedItem = row;
				ResultsListControl.ScrollIntoView(row);
			};
			vm.ResultsAnchorProvider = CaptureScrollAnchor;
			vm.ResultsScrollToRow = ScrollRowToViewportOffset;
		}

		/// <summary>Row whose realized container is topmost in the viewport (partially visible counts), plus its viewport offset.</summary>
		ResultsScrollAnchor.Capture? CaptureScrollAnchor() {
			if (resultsScrollViewer == null) return null;
			object? best = null;
			double bestTop = double.MaxValue;
			foreach (var container in ResultsListControl.GetRealizedContainers()) {
				if (container.TranslatePoint(new Point(0, 0), resultsScrollViewer) is not { } p) continue;
				if (p.Y + container.Bounds.Height <= 0) continue;
				if (p.Y < bestTop) {
					bestTop = p.Y;
					best = container.DataContext;
				}
			}
			return best == null ? null : new ResultsScrollAnchor.Capture(best, bestTop);
		}

		void ScrollRowToViewportOffset(object row, double viewportOffsetY) {
			Avalonia.Threading.Dispatcher.UIThread.Post(() => {
				int index = ResultsListControl.Items.IndexOf(row);
				if (index < 0) return;
				ResultsListControl.ScrollIntoView(index);
				Avalonia.Threading.Dispatcher.UIThread.Post(() => {
					if (resultsScrollViewer == null) return;
					var container = ResultsListControl.ContainerFromIndex(index);
					if (container?.TranslatePoint(new Point(0, 0), resultsScrollViewer) is not { } p) return;
					resultsScrollViewer.Offset = new Vector(
						resultsScrollViewer.Offset.X,
						Math.Max(0, resultsScrollViewer.Offset.Y + p.Y - viewportOffsetY));
				}, Avalonia.Threading.DispatcherPriority.Loaded);
			}, Avalonia.Threading.DispatcherPriority.Loaded);
		}

		readonly SelectionHeaderCleanup selectionHeaderCleanup = new();
		void OnResultsSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
			selectionHeaderCleanup.Run(ResultsListControl.SelectedItems);

		// Right-click also injects the custom single-resource consolidation action into
		// the existing group/file context menu. This keeps the ordinary similarity view
		// useful for one-off "better copy in Misc -> organized series path" upgrades.
		void OnResultsPointerPressed(object? sender, PointerPressedEventArgs e) {
			if (!e.GetCurrentPoint(ResultsListControl).Properties.IsRightButtonPressed) return;
			if (e.Source is not Control source) return;
			var container = source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
			ResultsGroupHeader? group = container?.DataContext switch {
				ResultsItemRow row => row.Group,
				ResultsGroupHeader header => header,
				_ => null,
			};
			if (group != null)
				EnsureConsolidateMenu(source, group);

			if (container?.DataContext is not ResultsItemRow fileRow) return;
			if (ResultsListControl.SelectedItems?.Contains(fileRow) == true) return;
			ResultsListControl.SelectedItems?.Clear();
			ResultsListControl.SelectedItem = fileRow;
		}

		void EnsureConsolidateMenu(Control source, ResultsGroupHeader group) {
			if (ViewModel is not MainWindowVM vm) return;
			ContextMenu? menu = null;
			Control? current = source;
			while (current != null) {
				if (current.ContextMenu is ContextMenu found) {
					menu = found;
					break;
				}
				current = current.GetVisualParent() as Control;
			}
			if (menu == null) return;
			if (menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, ConsolidateMenuTag)))
				return;
			menu.Items.Insert(0, new MenuItem {
				Header = "整合到系列…",
				Tag = ConsolidateMenuTag,
				Command = vm.ConsolidateGroupToSeriesCommand,
				CommandParameter = group,
			});
			menu.Items.Insert(1, new Separator());
		}

		void OnThumbnailDoubleTapped(object? sender, TappedEventArgs e) {
			ViewModel?.ThumbnailDoubleClickCommand.Execute().Subscribe();
			e.Handled = true;
		}

		async void OnPathPointerPressed(object? sender, PointerPressedEventArgs e) {
			if ((sender as Control)?.DataContext is not ResultsItemRow row) return;
			bool rowWasAlreadySelected = ResultsListControl.SelectedItems?.Contains(row) == true;
			if (!ResultsInteractionRules.ShouldCopyPathOnPointerPress(
					e.GetCurrentPoint(this).Properties.IsLeftButtonPressed, rowWasAlreadySelected))
				return;
			if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) {
				await clipboard.SetTextAsync(row.Item.ItemInfo.Path);
				await row.Item.FlashPathCopiedAsync();
			}
		}

		void OnPreviewGripDragDelta(object? sender, VectorEventArgs e) {
			SettingsFile.Instance.ResultsPreviewWidth += e.Vector.X;
		}

		ScrollViewer? resultsScrollViewer;
		bool headerInsetHooked;

		void OnResultsListTemplateApplied(object? sender, TemplateAppliedEventArgs e) {
			resultsScrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
			if (resultsScrollViewer == null) return;
			resultsScrollViewer.PropertyChanged += (_, args) => {
				if (args.Property == ScrollViewer.ViewportProperty)
					SyncHeaderInset();
			};
			if (!headerInsetHooked && this.FindControl<Border>("ColumnHeaderStrip") is { } header) {
				headerInsetHooked = true;
				header.PropertyChanged += (_, args) => {
					if (args.Property == BoundsProperty)
						SyncHeaderInset();
				};
			}
			SyncHeaderInset();
		}

		void SyncHeaderInset() {
			if (resultsScrollViewer == null) return;
			var header = this.FindControl<Border>("ColumnHeaderStrip");
			var columns = this.FindControl<DockPanel>("HeaderColumns");
			if (header == null || columns == null) return;
			double viewport = resultsScrollViewer.Viewport.Width;
			if (viewport <= 0) return;
			double inset = Math.Max(0, header.Bounds.Width - viewport);
			if (Math.Abs(columns.Margin.Right - inset) > 0.5)
				columns.Margin = new Thickness(0, 0, inset, 0);
		}

		static readonly TimeSpan HoverActivateDelay = TimeSpan.FromMilliseconds(160);
		static readonly TimeSpan HoverClearGrace = TimeSpan.FromMilliseconds(120);
		Avalonia.Threading.DispatcherTimer? metricHoverTimer;
		Avalonia.Threading.DispatcherTimer? metricClearTimer;
		DuplicateItemVM? activeDiffItem;
		string? activeDiffMetrics;

		void OnMetricPointerEntered(object? sender, PointerEventArgs e) {
			if (sender is not Border { Tag: string metrics, DataContext: ResultsItemRow row }) return;
			metricHoverTimer?.Stop();
			if (activeDiffItem != null && activeDiffMetrics == metrics &&
				activeDiffItem.ItemInfo.GroupId == row.Item.ItemInfo.GroupId) {
				metricClearTimer?.Stop();
				return;
			}
			metricHoverTimer = RunOnce(HoverActivateDelay, () => {
				if (ViewModel is not { } vm) return;
				metricClearTimer?.Stop();
				if (activeDiffItem != null)
					vm.ClearHoveredMetric(activeDiffItem);
				foreach (var metric in metrics.Split(','))
					vm.SetHoveredMetric(row.Item, metric);
				activeDiffItem = row.Item;
				activeDiffMetrics = metrics;
			});
		}

		void OnMetricPointerExited(object? sender, PointerEventArgs e) {
			metricHoverTimer?.Stop();
			if (activeDiffItem == null) return;
			metricClearTimer?.Stop();
			metricClearTimer = RunOnce(HoverClearGrace, () => {
				if (activeDiffItem != null)
					ViewModel?.ClearHoveredMetric(activeDiffItem);
				activeDiffItem = null;
				activeDiffMetrics = null;
			});
		}

		static Avalonia.Threading.DispatcherTimer RunOnce(TimeSpan delay, Action action) {
			var timer = new Avalonia.Threading.DispatcherTimer { Interval = delay };
			timer.Tick += (_, _) => { timer.Stop(); action(); };
			timer.Start();
			return timer;
		}
	}
}
