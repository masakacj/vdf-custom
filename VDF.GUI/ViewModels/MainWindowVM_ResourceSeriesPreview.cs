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
		string Tree);

	public partial class MainWindowVM : ReactiveObject {
		internal ResourceSeriesConsolidationPreview BuildResourceSeriesConsolidationPreview(
			IReadOnlyList<ResourceSeriesConsolidationPlan> plans) {
			var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var original = new Dictionary<string, long>(comparer);
			var final = new Dictionary<string, (long Bytes, string Marker)>(comparer);
			var loserPaths = new HashSet<string>(comparer);
			var relationLines = new List<string>();
			var treeSections = new List<string>();

			foreach (ResourceSeriesConsolidationPlan plan in plans) {
				var roots = new[] { plan.Header.TargetFolder }.Concat(plan.Header.SourceFolders)
					.Select(NormalizePikPakPath)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();
				foreach (string root in roots) {
					foreach (var file in Scanner.GetRecursiveFolderMediaFiles(root)) {
						string full = FullPreviewPath(file.Path);
						if (!original.ContainsKey(full)) original[full] = Math.Max(0, file.SizeBytes);
					}
				}

				var section = new Dictionary<string, (long Bytes, string Marker)>(comparer);
				foreach (var file in Scanner.GetRecursiveFolderMediaFiles(plan.DestinationRoot)) {
					string full = FullPreviewPath(file.Path);
					section[full] = (Math.Max(0, file.SizeBytes), "＝");
				}

				foreach (ResourceSeriesGroupPlan group in plan.Groups) {
					foreach (DuplicateItemVM loser in group.Losers) {
						string loserPath = FullPreviewPath(loser.ItemInfo.Path);
						loserPaths.Add(loserPath);
						if (PreviewPathInside(loserPath, plan.DestinationRoot)) section.Remove(loserPath);
					}
					string destination = FullPreviewPath(group.DestinationPath);
					bool replaces = group.KeeperNeedsMove && group.Losers.Any(loser =>
						comparer.Equals(FullPreviewPath(loser.ItemInfo.Path), destination));
					string marker = replaces ? "↑ BEST替换" : group.KeeperNeedsMove ? "＋ BEST" : "＝ BEST";
					section[destination] = (Math.Max(0, group.Keeper.ItemInfo.SizeLong), marker);
				}

				foreach (ResourceSeriesFileMovePlan file in plan.UniqueFiles) {
					string destination = FullPreviewPath(file.DestinationPath);
					long size = original.TryGetValue(FullPreviewPath(file.SourcePath), out long known) ? known : 0;
					section[destination] = (size, "＋ 新增");
				}

				foreach (var entry in section)
					final[entry.Key] = entry.Value;

				relationLines.Add(
					$"【目标】{plan.Header.TargetFolder}
" +
					$"  {plan.Header.TargetFiles:N0} 文件 · {plan.Header.TargetBytes.BytesToString()}
" +
					$"【来源】{plan.Header.SourceFolder}
" +
					$"  {plan.Header.SourceFiles:N0} 文件 · {plan.Header.SourceBytes.BytesToString()}
" +
					$"重叠率 {plan.Header.MinimumFolderMatchPercent:0.#}% · 资源组 {plan.Header.DisplayedResourceGroups:N0} · " +
					$"明确 BEST {plan.Header.ConfirmedMatches:N0} · 人工复核 {plan.Header.ReviewOnlyMatches:N0}
" +
					$"最终目标：{plan.DestinationRoot}");
				treeSections.Add(BuildOneTreeSection(plan.DestinationRoot, section));
			}

			long originalBytes = original.Values.Sum();
			long finalBytes = final.Values.Sum(item => item.Bytes);
			int keeperMoves = plans.Sum(plan => plan.KeeperMoves);
			int uniqueMoves = plans.Sum(plan => plan.UniqueFiles.Count);
			int replacements = plans.Sum(plan => plan.Groups.Count(group =>
				group.KeeperNeedsMove && group.Losers.Any(loser => comparer.Equals(
					FullPreviewPath(loser.ItemInfo.Path), FullPreviewPath(group.DestinationPath)))));
			int manual = plans.Sum(plan => plan.ManualReviewGroupIds.Count);
			int conflicts = plans.Sum(plan => plan.PathConflictCount);
			int skipped = plans.Sum(plan => plan.UniqueFilesSkippedByCoverage);
			long loserBytes = loserPaths.Sum(path => original.TryGetValue(path, out long size) ? size : 0);
			// Moving a keeper does not free space; only confirmed loser copies do.
			long reclaim = loserBytes;

			string before =
				$"涉及 {original.Count:N0} 个已索引文件
" +
				$"合计 {originalBytes.BytesToString()}
" +
				$"系列 {plans.Count:N0} 个";
			string changes =
				$"＋ 独有资源迁入 {uniqueMoves:N0}
" +
				$"↑ BEST 移动 {keeperMoves:N0}（替换 {replacements:N0}）
" +
				$"－ 重复副本待安全清理 {loserPaths.Count:N0} · {loserBytes.BytesToString()}
" +
				$"⚠ 人工 {manual:N0} · 冲突 {conflicts:N0} · 覆盖不足跳过 {skipped:N0}";
			string after =
				$"目标树约 {final.Count:N0} 个文件
" +
				$"约 {finalBytes.BytesToString()}
" +
				$"完成重复副本清理后预计释放 {reclaim.BytesToString()}";
			string scope = plans.Count == 1
				? $"1 个系列 · {plans[0].Header.MinimumFolderMatchPercent:0.#}% 文件夹重叠 · 目标 {plans[0].DestinationRoot}"
				: $"{plans.Count:N0} 个系列 · 按各自系列根目录保留相对结构";
			string tree = string.Join("

", treeSections);
			if (manual + conflicts + skipped > 0)
				tree += $"

⚠ {manual + conflicts + skipped:N0} 项未纳入自动目标树，保持原位置供人工处理。";
			return new ResourceSeriesConsolidationPreview(
				scope, before, changes, after, string.Join("

", relationLines), tree);
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
