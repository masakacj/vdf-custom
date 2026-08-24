// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VDF.GUI.Data;

namespace VDF.GUI.Views {
	/// <summary>Lightweight live temperature chart; no external chart dependency.</summary>
	public sealed class TemperatureHistoryChart : Control {
		ScanDriveRow? row;
		int warnC = 50;
		int pauseC = 52;
		int resumeC = 48;

		public void Attach(ScanDriveRow source) {
			if (ReferenceEquals(row, source)) return;
			if (row != null)
				row.TemperatureHistory.CollectionChanged -= HistoryChanged;
			row = source;
			row.TemperatureHistory.CollectionChanged += HistoryChanged;
			InvalidateVisual();
		}

		public void SetThresholds(int warn, int pause, int resume) {
			warnC = warn;
			pauseC = pause;
			resumeC = resume;
			InvalidateVisual();
		}

		void HistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

		public void Detach() {
			if (row != null)
				row.TemperatureHistory.CollectionChanged -= HistoryChanged;
			row = null;
		}

		public override void Render(DrawingContext context) {
			base.Render(context);
			Rect bounds = Bounds;
			if (bounds.Width < 80 || bounds.Height < 80)
				return;

			var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)), 1);
			Rect plot = new(34, 12, Math.Max(1, bounds.Width - 46), Math.Max(1, bounds.Height - 36));
			context.DrawRectangle(null, borderPen, plot);
			if (row == null || row.TemperatureHistory.Count == 0)
				return;

			int observedMin = row.TemperatureHistory.Min(x => x.TemperatureC);
			int observedMax = row.TemperatureHistory.Max(x => x.TemperatureC);
			int minC = Math.Min(observedMin, resumeC) - 2;
			int maxC = Math.Max(observedMax, pauseC) + 2;
			if (maxC - minC < 8) {
				int center = (maxC + minC) / 2;
				minC = center - 4;
				maxC = center + 4;
			}

			double Y(int temp) => plot.Bottom - (temp - minC) / (double)(maxC - minC) * plot.Height;
			void Threshold(int temp, Color color) {
				if (temp < minC || temp > maxC) return;
				var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, color.R, color.G, color.B)), 1);
				context.DrawLine(pen, new Point(plot.Left, Y(temp)), new Point(plot.Right, Y(temp)));
			}

			Threshold(resumeC, Colors.DodgerBlue);
			Threshold(warnC, Colors.Orange);
			Threshold(pauseC, Colors.Red);

			// Subtle horizontal guides every 2°C make small thermal changes readable.
			var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(38, 128, 128, 128)), 1);
			for (int temp = minC + 1; temp < maxC; temp++) {
				if (temp % 2 != 0 || temp == resumeC || temp == warnC || temp == pauseC) continue;
				context.DrawLine(guidePen, new Point(plot.Left, Y(temp)), new Point(plot.Right, Y(temp)));
			}

			IReadOnlyList<DriveTemperatureSample> samples = row.TemperatureHistory;
			DateTime firstUtc = samples[0].Utc;
			DateTime lastUtc = samples[^1].Utc;
			double spanSeconds = Math.Max(1, (lastUtc - firstUtc).TotalSeconds);
			double X(DriveTemperatureSample sample) =>
				plot.Left + Math.Clamp((sample.Utc - firstUtc).TotalSeconds / spanSeconds, 0, 1) * plot.Width;

			var curvePen = new Pen(new SolidColorBrush(Colors.LimeGreen), 2);
			Point? previous = null;
			foreach (DriveTemperatureSample sample in samples) {
				var point = new Point(X(sample), Y(sample.TemperatureC));
				if (previous != null)
					context.DrawLine(curvePen, previous.Value, point);
				previous = point;
			}
			if (previous != null)
				context.DrawEllipse(new SolidColorBrush(Colors.LimeGreen), null, previous.Value, 3, 3);
		}
	}
}
