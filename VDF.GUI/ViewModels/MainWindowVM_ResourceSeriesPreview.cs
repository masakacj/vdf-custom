// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Text;
using ReactiveUI;
using VDF.Core.Utils;

namespace VDF.GUI.ViewModels {
	internal sealed record ResourceSeriesConsolidationPreview(
		string Scope,
		string Before,
		string Changes,
		string After,
		string Relations,
		string Tree,
		string DeletionDetails);

	public partial class MainWindowVM : ReactiveObject {
		internal ResourceSeriesConsolidationPreview BuildResourceSeriesConsolidationPreview(
			IReadOnlyList<ResourceSeriesConsolidationPlan> plans,
			IReadOnlyList<ResourceSeriesManualReview>? manualReviews = null,
			IReadOnlyDictionary<Guid, DuplicateItemVM>? keeperOverrides = null) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var comparison = CoreUtils.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			var original = new Dictionary<string, long>(comparer);
			var final = new Dictionary<string, (long Bytes, string Marker)>(comparer);
			var removable = new Dictionary<string, (long Bytes, string Keeper, bool ImmediateReplace, bool HumanConfirmed)>(comparer);
			var relationLines = new List<string>();
			var treeSections = new List<string>();
			var scopedGroupIds = new HashSet<Guid>();

			var manualReviewIds = manualReviews == null
				? new HashSet<Guid>()
				: manualReviews.Select(review => review.GroupId).ToHashSet();
			var acceptedManualIds = keeperOverrides == null
				? new HashSet<Guid>()
				: keeperOverrides.Keys.ToHashSet();

			foreach (ResourceSeriesConsolidationPlan plan in plans) {
				foreach (Guid id in plan.Header.DisplayedGroupIds)
					scopedGroupIds.Add(id);

				var roots = new[] { plan.Header.TargetFolder }.Concat(plan.Header.SourceFolders)
					.Select(NormalizePikPakPath)
					.Where(root => root.Length > 0)
					.Distinct(comparer)
					.ToList();

				// The merge preview is deliberately scoped to the exact A/B series roots shown
				// to the user. These snapshots come from the VDF database (not live directory
				// enumeration), so unrelated files from an ancestor/library root cannot leak into
				// the before/after totals.
				var filesByRoot = new Dictionary<string, IReadOnlyList<VDF.Core.FolderMediaFile>>(comparer);
				foreach (string root in roots) {
					IReadOnlyList<VDF.Core.FolderMediaFile> files = Scanner.GetRecursiveFolderMediaFiles(root);
					filesByRoot[root] = files;
					foreach (var file in files)
						PutLargest(original, FullPreviewPath(file.Path), Math.Max(0, file.SizeBytes));
				}

				// The execution plan is authoritative for duplicate members. Fold those rows in
				// even if an older database snapshot did not enumerate one of the paths above.
				foreach (ResourceSeriesGroupPlan group in plan.Groups) {
					PutLargest(original, FullPreviewPath(group.Keeper.ItemInfo.Path), Math.Max(0, group.Keeper.ItemInfo.SizeLong));
					foreach (DuplicateItemVM loser in group.Losers)
						PutLargest(original, FullPreviewPath(loser.ItemInfo.Path), Math.Max(0, loser.ItemInfo.SizeLong));
				}
				foreach (ResourceSeriesFileMovePlan file in plan.UniqueFiles) {
					string source = FullPreviewPath(file.SourcePath);
					if (!original.ContainsKey(source)) original[source] = TryGetPreviewFileSize(source);
				}

				var section = new Dictionary<string, (long Bytes, string Marker)>(comparer);
				foreach (var file in Scanner.GetRecursiveFolderMediaFiles(plan.DestinationRoot)) {
					string full = FullPreviewPath(file.Path);
					section[full] = (Math.Max(0, file.SizeBytes), "＝ 保留");
				}

				foreach (ResourceSeriesGroupPlan group in plan.Groups) {
					string keeperDestination = FullPreviewPath(group.DestinationPath);
					bool humanConfirmed = manualReviewIds.Contains(group.GroupId) && acceptedManualIds.Contains(group.GroupId);
					foreach (DuplicateItemVM loser in group.Losers) {
						string loserPath = FullPreviewPath(loser.ItemInfo.Path);
						bool immediate = comparer.Equals(loserPath, keeperDestination);
						long loserBytes = Math.Max(0, loser.ItemInfo.SizeLong);
						if (!removable.TryGetValue(loserPath, out var existing) || loserBytes > existing.Bytes)
							removable[loserPath] = (loserBytes, keeperDestination, immediate, humanConfirmed);
						if (PreviewPathInside(loserPath, plan.DestinationRoot))
							section.Remove(loserPath);
					}

					bool replaces = group.KeeperNeedsMove && group.Losers.Any(loser =>
						comparer.Equals(FullPreviewPath(loser.ItemInfo.Path), keeperDestination));
					string marker = replaces ? "↑ BEST替换" : group.KeeperNeedsMove ? "＋ BEST迁入" : "＝ BEST保留";
					section[keeperDestination] = (Math.Max(0, group.Keeper.ItemInfo.SizeLong), marker);
				}

				foreach (ResourceSeriesFileMovePlan file in plan.UniqueFiles) {
					string destination = FullPreviewPath(file.DestinationPath);
					string source = FullPreviewPath(file.SourcePath);
					long size = original.TryGetValue(source, out long known) ? known : TryGetPreviewFileSize(source);
					section[destination] = (size, "＋ 新增");
				}

				foreach (var entry in section)
					final[entry.Key] = entry.Value;

				int planReplacements = plan.Groups.Count(group => group.KeeperNeedsMove && group.Losers.Any(loser =>
					comparer.Equals(FullPreviewPath(loser.ItemInfo.Path), FullPreviewPath(group.DestinationPath))));
				int planKeepersInPlace = plan.Groups.Count(group => !group.KeeperNeedsMove);
				long planLoserBytes = ComputeConfirmedReclaimBytes(plan.Groups.SelectMany(group => group.Losers));
				int planLoserCount = DistinctPathCount(plan.Groups.SelectMany(group => group.Losers), comparer);

				string targetRoot = NormalizePikPakPath(plan.Header.TargetFolder);
				IReadOnlyList<VDF.Core.FolderMediaFile> targetFiles = filesByRoot.TryGetValue(targetRoot, out var tf)
					? tf : Scanner.GetRecursiveFolderMediaFiles(targetRoot);
				long targetBytes = SumFolderBytes(targetFiles);

				var sourceFiles = new Dictionary<string, long>(comparer);
				foreach (string sourceRoot in plan.Header.SourceFolders.Select(NormalizePikPakPath).Distinct(comparer)) {
					IReadOnlyList<VDF.Core.FolderMediaFile> sourceSnapshot = filesByRoot.TryGetValue(sourceRoot, out var sf)
						? sf : Scanner.GetRecursiveFolderMediaFiles(sourceRoot);
					foreach (var file in sourceSnapshot)
						PutLargest(sourceFiles, FullPreviewPath(file.Path), Math.Max(0, file.SizeBytes));
				}

				var relation = new StringBuilder();
				relation.AppendLine("A  目标系列根目录（合并后保留）")
					.AppendLine(plan.Header.TargetFolder)
					.Append(targetFiles.Count.ToString("N0")).Append(" 个已索引媒体 · ")
					.Append(targetBytes.BytesToString()).AppendLine()
					.AppendLine();

				if (plan.Header.SourceFolderCount == 1) {
					relation.AppendLine("B  来源系列根目录（并入 A）")
						.AppendLine(plan.Header.SourceFolders[0])
						.Append(sourceFiles.Count.ToString("N0")).Append(" 个已索引媒体 · ")
						.Append(sourceFiles.Values.Sum().BytesToString()).AppendLine();
				}
				else {
					relation.Append("B  ").Append(plan.Header.SourceFolderCount.ToString("N0"))
						.AppendLine(" 个来源系列根目录（并入 A）");
					foreach (ResourceDirectedRelation sourceRelation in plan.Header.SourceRelations) {
						string sourceRoot = NormalizePikPakPath(sourceRelation.SourceFolder);
						IReadOnlyList<VDF.Core.FolderMediaFile> sourceSnapshot = filesByRoot.TryGetValue(sourceRoot, out var sf)
							? sf : Scanner.GetRecursiveFolderMediaFiles(sourceRoot);
						relation.Append("• ").AppendLine(sourceRelation.SourceFolder)
							.Append("  ").Append(sourceSnapshot.Count.ToString("N0")).Append(" 个已索引媒体 · ")
							.Append(SumFolderBytes(sourceSnapshot).BytesToString())
							.Append(" · 重叠 ").Append(sourceRelation.MatchPercent.ToString("0.#")).Append('%')
							.Append(" · 来源覆盖 ").Append(sourceRelation.SourceCoverage.ToString("0.#")).AppendLine("%");
					}
				}

				relation.AppendLine()
					.Append("逻辑资源双向重叠 ").Append(plan.Header.MinimumFolderMatchPercent.ToString("0.#")).AppendLine("%")
					.Append("目标覆盖 ").Append(plan.Header.TargetCoverage.ToString("0.#")).Append("% · 来源覆盖 ")
					.Append(plan.Header.SourceCoverage.ToString("0.#")).Append("% · 匹配资源组 ")
					.Append(plan.Header.DisplayedResourceGroups.ToString("N0")).AppendLine()
					.Append("系统确认 BEST ").Append(plan.Header.ConfirmedMatches.ToString("N0"))
					.Append(" · 推荐 BEST 待复核 ").Append(plan.Header.ReviewOnlyMatches.ToString("N0")).AppendLine()
					.AppendLine()
					.Append("本对目录当前计划：＝BEST原位保留 ").Append(planKeepersInPlace.ToString("N0"))
					.Append(" · ＋来源独有迁入 ").Append(plan.UniqueFiles.Count.ToString("N0"))
					.Append(" · ↑BEST移动 ").Append(plan.KeeperMoves.ToString("N0"))
					.Append("（替换 ").Append(planReplacements.ToString("N0")).Append("） · －可清理副本 ")
					.Append(planLoserCount.ToString("N0")).Append(" / ").AppendLine(planLoserBytes.BytesToString())
					.Append("仍需处理：人工/未确认 ").Append(plan.ManualReviewGroupIds.Count.ToString("N0"))
					.Append(" · 真正路径冲突 ").Append(plan.PathConflictCount.ToString("N0")).AppendLine();
				if (plan.UniqueFilesSkippedByCoverage > 0) {
					relation.Append("来源独有保持原位 ").Append(plan.UniqueFilesSkippedByCoverage.ToString("N0"))
						.AppendLine("（整目录条件未满足；不计为冲突，也不要求人工逐项处理）");
				}
				relation.Append("最终目标 → ").Append(plan.DestinationRoot);
				relationLines.Add(relation.ToString());
				treeSections.Add(BuildOneTreeSection(plan.DestinationRoot, section));
			}

			long beforeBytes = original.Values.Sum();
			long finalBytes = final.Values.Sum(item => item.Bytes);
			int keepersInPlace = plans.Sum(plan => plan.Groups.Count(group => !group.KeeperNeedsMove));
			int keeperMoves = plans.Sum(plan => plan.KeeperMoves);
			int uniqueMoves = plans.Sum(plan => plan.UniqueFiles.Count);
			int replacements = plans.Sum(plan => plan.Groups.Count(group =>
				group.KeeperNeedsMove && group.Losers.Any(loser => comparer.Equals(
					FullPreviewPath(loser.ItemInfo.Path), FullPreviewPath(group.DestinationPath)))));
			int manual = plans.Sum(plan => plan.ManualReviewGroupIds.Count);
			int conflicts = plans.Sum(plan => plan.PathConflictCount);
			int skippedUnique = plans.Sum(plan => plan.UniqueFilesSkippedByCoverage);
			int executableGroups = plans.Sum(plan => plan.Groups.Count);
			long reclaim = ComputeConfirmedReclaimBytes(
				plans.SelectMany(plan => plan.Groups).SelectMany(group => group.Losers));
			int confirmedDeleteCount = DistinctPathCount(
				plans.SelectMany(plan => plan.Groups).SelectMany(group => group.Losers), comparer);

			var unresolvedReviews = (manualReviews ?? Array.Empty<ResourceSeriesManualReview>())
				.Where(review => keeperOverrides == null || !keeperOverrides.ContainsKey(review.GroupId))
				.ToList();
			var pendingRecommendedLosers = unresolvedReviews
				.SelectMany(review => review.Candidates.Where(candidate => !ReferenceEquals(candidate, review.RecommendedKeeper)))
				.ToList();
			var confirmedLosers = plans.SelectMany(plan => plan.Groups).SelectMany(group => group.Losers).ToList();
			var potentialAllLosers = confirmedLosers.Concat(pendingRecommendedLosers).ToList();
			long potentialReclaim = ComputeConfirmedReclaimBytes(potentialAllLosers);
			int potentialDeleteCount = DistinctPathCount(potentialAllLosers, comparer);
			int nonReviewManual = Math.Max(0, manual - unresolvedReviews.Count);

			string before =
				$"本次 A / B 范围 {original.Count:N0} 个已索引媒体\n" +
				$"合计 {beforeBytes.BytesToString()}\n" +
				$"匹配逻辑资源组 {scopedGroupIds.Count:N0}";

			var changes = new StringBuilder();
			changes.Append("✓ 已确认可执行 ").Append(executableGroups.ToString("N0"))
				.Append(" · ★ 推荐待确认 ").Append(unresolvedReviews.Count.ToString("N0")).AppendLine()
				.Append("＋ 来源独有迁入 ").Append(uniqueMoves.ToString("N0")).AppendLine()
				.Append("↑ BEST 迁入 ").Append(keeperMoves.ToString("N0")).Append("（原位替换 ")
				.Append(replacements.ToString("N0")).AppendLine("）")
				.Append("－ 当前已确认可清理 ").Append(confirmedDeleteCount.ToString("N0"))
				.Append(" · ").AppendLine(reclaim.BytesToString());
			if (unresolvedReviews.Count > 0) {
				changes.Append("若接受剩余推荐：预计总可清理 ").Append(potentialDeleteCount.ToString("N0"))
					.Append(" · ").AppendLine(potentialReclaim.BytesToString());
			}
			if (nonReviewManual > 0 || conflicts > 0) {
				changes.Append("⚠ 仍需人工/安全处理 ").Append(nonReviewManual.ToString("N0"))
					.Append(" · 真冲突 ").Append(conflicts.ToString("N0")).AppendLine();
			}
			if (skippedUnique > 0) {
				changes.Append("↪ 来源独有保持原位 ").Append(skippedUnique.ToString("N0"))
					.Append("（不计为人工冲突）");
			}

			var after = new StringBuilder();
			after.Append("当前确认方案最终目标树约 ").Append(final.Count.ToString("N0")).AppendLine(" 个文件")
				.Append("约 ").Append(finalBytes.BytesToString()).AppendLine()
				.Append("当前已确认可释放 ").Append(reclaim.BytesToString());
			if (unresolvedReviews.Count > 0)
				after.Append("\n全部采用剩余推荐后预计可释放 ").Append(potentialReclaim.BytesToString());

			string scope = plans.Count == 1
				? $"1 对系列根目录 · 逻辑资源双向重叠 {plans[0].Header.MinimumFolderMatchPercent:0.#}% · 目标 {plans[0].DestinationRoot}"
				: $"{plans.Count:N0} 对系列根目录 · 统计范围仅包含各自 A/B 根目录";
			string tree = string.Join("\n\n", treeSections);
			if (manual + conflicts > 0)
				tree += $"\n\n⚠ 另有 {manual + conflicts:N0} 个资源组尚未进入自动目标树；在人工审核中确认后会即时重算。";
			if (skippedUnique > 0)
				tree += $"\nℹ {skippedUnique:N0} 个来源独有媒体因整目录条件未达到门槛而保持原位，不属于冲突或人工复核项。";

			long automaticBytes = removable.Where(pair => !pair.Value.HumanConfirmed).Sum(pair => pair.Value.Bytes);
			int automaticCount = removable.Count(pair => !pair.Value.HumanConfirmed);
			long humanBytes = removable.Where(pair => pair.Value.HumanConfirmed).Sum(pair => pair.Value.Bytes);
			int humanCount = removable.Count(pair => pair.Value.HumanConfirmed);
			var deletion = new StringBuilder();
			deletion.Append("系统确认可清理：").Append(automaticCount.ToString("N0"))
				.Append(" 个副本 · ").AppendLine(automaticBytes.BytesToString())
				.Append("人工已确认可清理：").Append(humanCount.ToString("N0"))
				.Append(" 个副本 · ").AppendLine(humanBytes.BytesToString())
				.Append("当前合计：").Append(removable.Count.ToString("N0"))
				.Append(" 个副本 · ").AppendLine(reclaim.BytesToString());
			if (unresolvedReviews.Count > 0) {
				deletion.Append("待复核推荐：").Append(unresolvedReviews.Count.ToString("N0"))
					.Append(" 组；若全部接受系统推荐，预计总可清理 ")
					.Append(potentialDeleteCount.ToString("N0")).Append(" 个副本 · ")
					.AppendLine(potentialReclaim.BytesToString())
					.AppendLine("这些待复核副本尚未进入执行计划；请在“合并审核”页逐组确认。确认后本页会即时更新。");
			}
			deletion.AppendLine("说明：目标位低质量副本会在 BEST 校验成功时立即替换；其余副本在合并成功后标记为待删除，可在结果页统一删除。");
			if (removable.Count == 0) {
				deletion.AppendLine().Append("当前没有已确认可释放的副本；这不代表最终释放空间为 0，请参考上面的“待复核推荐”预计值。");
			}
			else {
				int index = 1;
				foreach (var entry in removable.OrderBy(pair => pair.Value.HumanConfirmed).ThenByDescending(pair => pair.Value.Bytes).ThenBy(pair => pair.Key, comparer)) {
					string source = entry.Value.HumanConfirmed ? "人工确认" : "系统确认";
					deletion.AppendLine().Append(index++).Append(". [").Append(source).Append(']')
						.Append(entry.Value.ImmediateReplace ? " [立即替换] " : " [待删除] ")
						.Append(entry.Value.Bytes.BytesToString()).Append("  ").AppendLine(entry.Key)
						.Append("   保留 → ").AppendLine(entry.Value.Keeper);
				}
			}

			return new ResourceSeriesConsolidationPreview(
				scope, before, changes.ToString().TrimEnd(), after.ToString(),
				string.Join("\n\n────────────────────\n\n", relationLines), tree, deletion.ToString().TrimEnd());
		}

		static int DistinctPathCount(IEnumerable<DuplicateItemVM> items, StringComparer comparer) =>
			items.Select(item => FullPreviewPath(item.ItemInfo.Path)).Distinct(comparer).Count();

		static long SumFolderBytes(IEnumerable<VDF.Core.FolderMediaFile> files) {
			long total = 0;
			foreach (var file in files) {
				long value = Math.Max(0, file.SizeBytes);
				total = long.MaxValue - total < value ? long.MaxValue : total + value;
			}
			return total;
		}

		static void PutLargest(Dictionary<string, long> target, string path, long bytes) {
			if (!target.TryGetValue(path, out long old) || bytes > old)
				target[path] = bytes;
		}

		static long TryGetPreviewFileSize(string path) {
			try { return File.Exists(path) ? Math.Max(0, new FileInfo(path).Length) : 0; }
			catch { return 0; }
		}

		internal static string BuildOneTreeSection(
			string destinationRoot,
			IReadOnlyDictionary<string, (long Bytes, string Marker)> files,
			int maxFiles = 500) {
			string root = FullPreviewPath(destinationRoot);
			string rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			if (string.IsNullOrWhiteSpace(rootName)) rootName = root;
			var sb = new StringBuilder();
			sb.Append("📁 ").AppendLine(rootName);
			var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int shown = 0;
			foreach (var entry in files
				.Select(pair => (Path: pair.Key, pair.Value.Bytes, pair.Value.Marker, Relative: SafePreviewRelative(root, pair.Key)))
				.Where(item => item.Relative != null)
				.OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)) {
				if (shown >= maxFiles) break;
				string relative = entry.Relative!;
				string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0) continue;
				string prefix = string.Empty;
				for (int i = 0; i < parts.Length - 1; i++) {
					prefix = prefix.Length == 0 ? parts[i] : Path.Combine(prefix, parts[i]);
					if (!emitted.Add(prefix)) continue;
					sb.Append(' ', i * 3 + 2).Append("├─ 📁 ").AppendLine(parts[i]);
				}
				sb.Append(' ', (parts.Length - 1) * 3 + 2)
					.Append("└─ ").Append(entry.Marker).Append(' ')
					.Append(parts[^1]);
				if (entry.Bytes > 0) sb.Append("  ·  ").Append(entry.Bytes.BytesToString());
				sb.AppendLine();
				shown++;
			}
			if (shown == 0 && files.Count > 0) {
				sb.AppendLine("   ⚠ 无法把计划路径换算成目标根目录下的相对路径，改列完整路径：");
				foreach (var entry in files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(maxFiles)) {
					sb.Append("   └─ ").Append(entry.Value.Marker).Append(' ').Append(entry.Key);
					if (entry.Value.Bytes > 0) sb.Append("  ·  ").Append(entry.Value.Bytes.BytesToString());
					sb.AppendLine();
					shown++;
				}
			}
			if (files.Count > shown)
				sb.Append("   … 还有 ").Append(files.Count - shown).AppendLine(" 个文件未展开");
			return sb.ToString().TrimEnd();
		}

		static string? SafePreviewRelative(string root, string path) {
			try {
				string relative = Path.GetRelativePath(root, FullPreviewPath(path));
				if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
					relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
					relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
					return null;
				return relative;
			}
			catch { return null; }
		}

		static bool PreviewPathInside(string path, string root) => SafePreviewRelative(FullPreviewPath(root), path) != null;
		static string FullPreviewPath(string path) {
			try { return Path.GetFullPath(path); }
			catch { return path; }
		}
	}
}
