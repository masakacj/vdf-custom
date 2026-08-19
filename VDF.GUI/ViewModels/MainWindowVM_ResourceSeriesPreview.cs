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
			IReadOnlyList<ResourceSeriesConsolidationPlan> plans) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var original = new Dictionary<string, long>(comparer);
			var final = new Dictionary<string, (long Bytes, string Marker)>(comparer);
			var explicitlyPlanned = new Dictionary<string, (long Bytes, string Marker)>(comparer);
			var removable = new Dictionary<string, (long Bytes, string Keeper, bool ImmediateReplace)>(comparer);
			var relationLines = new List<string>();
			var treeSections = new List<string>();
			bool inputEnumerationIncomplete = false;
			bool destinationEnumerationIncomplete = false;

			foreach (ResourceSeriesConsolidationPlan plan in plans) {
				var roots = new[] { plan.Header.TargetFolder }.Concat(plan.Header.SourceFolders)
					.Select(NormalizePikPakPath)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();

				int enumeratedInputFiles = 0;
				foreach (string root in roots) {
					var files = Scanner.GetRecursiveFolderMediaFiles(root);
					enumeratedInputFiles += files.Count;
					foreach (var file in files) {
						string full = FullPreviewPath(file.Path);
						PutLargest(original, full, Math.Max(0, file.SizeBytes));
					}
				}
				if (enumeratedInputFiles == 0 && plan.Header.TargetFiles + plan.Header.SourceFiles > 0)
					inputEnumerationIncomplete = true;

				// The execution plan already contains authoritative duplicate rows. Always fold
				// them into the preview snapshot so a NAS/UNC enumeration failure can never turn
				// known files/bytes into a misleading 0 files / 0 B summary.
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
				var destinationFiles = Scanner.GetRecursiveFolderMediaFiles(plan.DestinationRoot);
				foreach (var file in destinationFiles) {
					string full = FullPreviewPath(file.Path);
					section[full] = (Math.Max(0, file.SizeBytes), "＝ 保留");
				}
				bool destinationIsIndexedTarget = comparer.Equals(
					FullPreviewPath(plan.DestinationRoot), FullPreviewPath(plan.Header.TargetFolder));
				if (destinationIsIndexedTarget && destinationFiles.Count == 0 && plan.Header.TargetFiles > 0)
					destinationEnumerationIncomplete = true;

				var planExplicit = new Dictionary<string, (long Bytes, string Marker)>(comparer);
				foreach (ResourceSeriesGroupPlan group in plan.Groups) {
					string keeperDestination = FullPreviewPath(group.DestinationPath);
					foreach (DuplicateItemVM loser in group.Losers) {
						string loserPath = FullPreviewPath(loser.ItemInfo.Path);
						bool immediate = ReferenceEquals(loser, group.DestinationMember);
						long loserBytes = Math.Max(0, loser.ItemInfo.SizeLong);
						if (!removable.TryGetValue(loserPath, out var existing) || loserBytes > existing.Bytes)
							removable[loserPath] = (loserBytes, keeperDestination, immediate);
						if (PreviewPathInside(loserPath, plan.DestinationRoot)) section.Remove(loserPath);
					}
					bool replaces = group.KeeperNeedsMove && group.Losers.Any(loser =>
						comparer.Equals(FullPreviewPath(loser.ItemInfo.Path), keeperDestination));
					string marker = replaces ? "↑ BEST替换" : group.KeeperNeedsMove ? "＋ BEST迁入" : "＝ BEST保留";
					var entry = (Math.Max(0, group.Keeper.ItemInfo.SizeLong), marker);
					section[keeperDestination] = entry;
					planExplicit[keeperDestination] = entry;
					explicitlyPlanned[keeperDestination] = entry;
				}

				foreach (ResourceSeriesFileMovePlan file in plan.UniqueFiles) {
					string destination = FullPreviewPath(file.DestinationPath);
					string source = FullPreviewPath(file.SourcePath);
					long size = original.TryGetValue(source, out long known) ? known : TryGetPreviewFileSize(source);
					var entry = (size, "＋ 新增");
					section[destination] = entry;
					planExplicit[destination] = entry;
					explicitlyPlanned[destination] = entry;
				}

				// Defensive invariant: an executable group must always appear in the final
				// preview, even if the destination directory could not be enumerated.
				foreach (var entry in planExplicit)
					section[entry.Key] = entry.Value;
				foreach (var entry in section)
					final[entry.Key] = entry.Value;

				int planReplacements = plan.Groups.Count(group => group.KeeperNeedsMove && group.Losers.Any(loser =>
					comparer.Equals(FullPreviewPath(loser.ItemInfo.Path), FullPreviewPath(group.DestinationPath))));
				long planLoserBytes = ComputeConfirmedReclaimBytes(plan.Groups.SelectMany(group => group.Losers));
				int planLoserCount = plan.Groups.SelectMany(group => group.Losers)
					.Select(loser => FullPreviewPath(loser.ItemInfo.Path)).Distinct(comparer).Count();

				relationLines.Add(
					$"A  目标文件夹（合并后保留）\n{plan.Header.TargetFolder}\n" +
					$"{plan.Header.TargetFiles:N0} 文件 · {plan.Header.TargetBytes.BytesToString()} · 约 {plan.Header.TargetResources:N0} 资源\n\n" +
					$"B  来源文件夹（并入 A）\n{plan.Header.SourceFolder}\n" +
					$"{plan.Header.SourceFiles:N0} 文件 · {plan.Header.SourceBytes.BytesToString()} · 约 {plan.Header.SourceResources:N0} 资源\n\n" +
					$"A ⇄ B  双向重叠 {plan.Header.MinimumFolderMatchPercent:0.#}%\n" +
					$"目标覆盖 {plan.Header.TargetCoverage:0.#}% · 来源覆盖 {plan.Header.SourceCoverage:0.#}% · 匹配资源组 {plan.Header.DisplayedResourceGroups:N0}\n" +
					$"确认 BEST {plan.Header.ConfirmedMatches:N0} · 推荐 BEST 待复核 {plan.Header.ReviewOnlyMatches:N0}\n\n" +
					$"本对目录计划：＋新增 {plan.UniqueFiles.Count:N0} · ↑BEST移动 {plan.KeeperMoves:N0}（替换 {planReplacements:N0}） · " +
					$"－可清理副本 {planLoserCount:N0} / {planLoserBytes.BytesToString()}\n" +
					$"保持原位：人工 {plan.ManualReviewGroupIds.Count:N0} · 路径冲突 {plan.PathConflictCount:N0} · 覆盖不足 {plan.UniqueFilesSkippedByCoverage:N0}\n" +
					$"最终目标 → {plan.DestinationRoot}");
				treeSections.Add(BuildOneTreeSection(plan.DestinationRoot, section));
			}

			// A second defensive invariant. This should normally be redundant with section,
			// but guarantees the summary can never report 0 final files while the plan has
			// explicit BEST/unique destinations.
			foreach (var entry in explicitlyPlanned)
				final[entry.Key] = entry.Value;

			long liveOriginalBytes = original.Values.Sum();
			long indexedFiles = plans.Sum(plan => (long)plan.Header.TargetFiles + plan.Header.SourceFiles);
			long indexedBytes = plans.Sum(plan => plan.Header.TargetBytes + plan.Header.SourceBytes);
			long beforeFiles = indexedFiles > 0 ? indexedFiles : original.Count;
			long beforeBytes = indexedBytes > 0 ? indexedBytes : liveOriginalBytes;
			long finalBytes = final.Values.Sum(item => item.Bytes);
			int keeperMoves = plans.Sum(plan => plan.KeeperMoves);
			int uniqueMoves = plans.Sum(plan => plan.UniqueFiles.Count);
			int replacements = plans.Sum(plan => plan.Groups.Count(group =>
				group.KeeperNeedsMove && group.Losers.Any(loser => comparer.Equals(
					FullPreviewPath(loser.ItemInfo.Path), FullPreviewPath(group.DestinationPath)))));
			int manual = plans.Sum(plan => plan.ManualReviewGroupIds.Count);
			int conflicts = plans.Sum(plan => plan.PathConflictCount);
			int skipped = plans.Sum(plan => plan.UniqueFilesSkippedByCoverage);
			long reclaim = ComputeConfirmedReclaimBytes(
				plans.SelectMany(plan => plan.Groups).SelectMany(group => group.Losers));

			string enumerationNote = inputEnumerationIncomplete
				? "\n⚠ NAS/UNC 实时目录枚举未返回完整结果；范围数字改用 VDF 索引，文件级计划仍来自当前重复组。"
				: string.Empty;
			string before =
				$"VDF 索引范围 {beforeFiles:N0} 个文件\n" +
				$"合计 {beforeBytes.BytesToString()}\n" +
				$"对比系列 {plans.Count:N0} 个" + enumerationNote;
			string changes =
				$"＋ 新增到目标 {uniqueMoves:N0}\n" +
				$"↑ BEST 迁入 {keeperMoves:N0}（原位替换 {replacements:N0}）\n" +
				$"－ 确认可清理副本 {removable.Count:N0} · {reclaim.BytesToString()}\n" +
				$"⚠ 保持原位：人工 {manual:N0} · 冲突 {conflicts:N0} · 覆盖不足 {skipped:N0}";

			string after;
			if (destinationEnumerationIncomplete) {
				long explicitBytes = explicitlyPlanned.Values.Sum(item => item.Bytes);
				after =
					$"计划已明确 {explicitlyPlanned.Count:N0} 个目标文件\n" +
					$"已知计划体积 {explicitBytes.BytesToString()}\n" +
					$"预计释放 {reclaim.BytesToString()}\n" +
					"⚠ 原目标是已索引目录，但实时枚举未返回文件；因此不把最终总数错误显示为 0。";
			}
			else {
				after =
					$"最终目标树约 {final.Count:N0} 文件\n" +
					$"约 {finalBytes.BytesToString()}\n" +
					$"确认副本全部清理后预计释放 {reclaim.BytesToString()}";
			}

			string scope = plans.Count == 1
				? $"1 对系列文件夹 · 双向重叠 {plans[0].Header.MinimumFolderMatchPercent:0.#}% · 目标 {plans[0].DestinationRoot}"
				: $"{plans.Count:N0} 对系列文件夹 · 分别保留各自相对目录结构";
			string tree = string.Join("\n\n", treeSections);
			if (manual + conflicts + skipped > 0)
				tree += $"\n\n⚠ 另有 {manual + conflicts + skipped:N0} 项未进入自动目标树，保持原位置供人工处理。";

			var deletion = new StringBuilder();
			deletion.Append("确认可清理：").Append(removable.Count.ToString("N0"))
				.Append(" 个副本 · ").AppendLine(reclaim.BytesToString());
			deletion.AppendLine("说明：目标位低质量副本会在 BEST 校验成功时立即替换；其余副本在合并成功后标记为待删除，可在结果页统一删除。");
			if (inputEnumerationIncomplete || destinationEnumerationIncomplete)
				deletion.AppendLine("⚠ 网络目录实时枚举不完整不会降低文件级安全校验；这里的删除项和大小直接来自重复组 ItemInfo，而不是目录扫描回查。");
			if (removable.Count == 0) {
				deletion.AppendLine().Append("当前没有已确认可释放的副本。人工复核完成后，此列表会即时更新。");
			}
			else {
				int index = 1;
				foreach (var entry in removable.OrderByDescending(pair => pair.Value.Bytes).ThenBy(pair => pair.Key, comparer)) {
					deletion.AppendLine().Append(index++).Append(". ")
						.Append(entry.Value.ImmediateReplace ? "[立即替换] " : "[待删除] ")
						.Append(entry.Value.Bytes.BytesToString()).Append("  ").AppendLine(entry.Key)
						.Append("   保留 → ").AppendLine(entry.Value.Keeper);
				}
			}

			return new ResourceSeriesConsolidationPreview(
				scope, before, changes, after,
				string.Join("\n\n────────────────────\n\n", relationLines), tree, deletion.ToString().TrimEnd());
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
