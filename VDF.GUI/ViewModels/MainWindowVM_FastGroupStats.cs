// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Collections.Specialized;
using VDF.Core.Utils;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		sealed class FastGroupStat {
			internal readonly List<long> Sizes = new();
			internal long Total;
			internal long Largest;
			internal long Savings => Total - Largest;
		}

		readonly Dictionary<Guid, FastGroupStat> fastGroupStats = new();
		long fastGroupStatsTotalBytes;
		long fastGroupStatsSavingsBytes;
		bool fastGroupStatsValid;
		bool fastGroupStatsTrackingInstalled;

		void EnsureFastGroupStatsTracking() {
			if (!fastGroupStatsTrackingInstalled) {
				Duplicates.CollectionChanged += FastGroupStatsCollectionChanged;
				fastGroupStatsTrackingInstalled = true;
			}
			if (!fastGroupStatsValid)
				RebuildFastGroupStatsBaseline();
		}

		internal void PrimeFastGroupStats() => EnsureFastGroupStatsTracking();

		void RebuildFastGroupStatsBaseline() {
			fastGroupStats.Clear();
			fastGroupStatsTotalBytes = 0;
			fastGroupStatsSavingsBytes = 0;
			foreach (DuplicateItemVM item in Duplicates)
				FastGroupStatsAdd(item);
			fastGroupStatsValid = true;
			PublishFastGroupStats();
		}

		void FastGroupStatsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
			if (e.Action == NotifyCollectionChangedAction.Reset) {
				fastGroupStatsValid = false;
				fastGroupStats.Clear();
				fastGroupStatsTotalBytes = 0;
				fastGroupStatsSavingsBytes = 0;
				return;
			}
			// Reset/import can add hundreds of thousands of rows after Reset. Ignore those
			// notifications until the next intentional baseline instead of doing the same work twice.
			if (!fastGroupStatsValid) return;

			if (e.OldItems != null)
				foreach (object? value in e.OldItems)
					if (value is DuplicateItemVM item)
						FastGroupStatsRemove(item);
			if (e.NewItems != null)
				foreach (object? value in e.NewItems)
					if (value is DuplicateItemVM item)
						FastGroupStatsAdd(item);
		}

		void FastGroupStatsAdd(DuplicateItemVM item) {
			Guid groupId = item.ItemInfo.GroupId;
			if (!fastGroupStats.TryGetValue(groupId, out FastGroupStat? state))
				fastGroupStats[groupId] = state = new FastGroupStat();
			else {
				fastGroupStatsTotalBytes -= state.Total;
				fastGroupStatsSavingsBytes -= state.Savings;
			}
			long size = Math.Max(0, item.ItemInfo.SizeLong);
			state.Sizes.Add(size);
			state.Total += size;
			if (size > state.Largest) state.Largest = size;
			fastGroupStatsTotalBytes += state.Total;
			fastGroupStatsSavingsBytes += state.Savings;
		}

		void FastGroupStatsRemove(DuplicateItemVM item) {
			Guid groupId = item.ItemInfo.GroupId;
			if (!fastGroupStats.TryGetValue(groupId, out FastGroupStat? state)) {
				fastGroupStatsValid = false; // defensive: rebuild on the next publish
				return;
			}
			fastGroupStatsTotalBytes -= state.Total;
			fastGroupStatsSavingsBytes -= state.Savings;

			long size = Math.Max(0, item.ItemInfo.SizeLong);
			if (!state.Sizes.Remove(size)) {
				fastGroupStatsValid = false;
				return;
			}
			state.Total -= size;
			if (state.Sizes.Count == 0) {
				fastGroupStats.Remove(groupId);
				return;
			}
			if (size == state.Largest)
				state.Largest = state.Sizes.Max();
			fastGroupStatsTotalBytes += state.Total;
			fastGroupStatsSavingsBytes += state.Savings;
		}

		internal void RefreshGroupStatsFast() {
			EnsureFastGroupStatsTracking();
			PublishFastGroupStats();
		}

		void PublishFastGroupStats() {
			if (!fastGroupStatsValid) return;
			TotalDuplicates = Duplicates.Count;
			TotalDuplicatesSize = fastGroupStatsTotalBytes.BytesToString();
			TotalDuplicateGroups = fastGroupStats.Count;
			PotentialSavings = fastGroupStatsSavingsBytes.BytesToString();
		}
	}
}
