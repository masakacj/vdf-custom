// Explicit folder merge from exactly two checked file rows. No automatic keeper/deletion.
using System.Reactive;
using System.Linq;
using Avalonia.Threading;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;
using VDF.GUI.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
    public partial class MainWindowVM {
        public bool CanMergeCheckedFolders => DuplicatesCheckedCounter == 2;

        public ReactiveCommand<Unit, Unit> MergeCheckedFoldersCommand =>
            ReactiveCommand.CreateFromTask(MergeCheckedFoldersAsync);

        async Task MergeCheckedFoldersAsync() {
            if (IsBusy || IsScanning || IsResultsFilterRefreshing) return;
            // These are checkboxes, not the ListBox highlight selection. Take only three,
            // not all checked rows, so an invalid huge selection allocates no extra list.
            var selected = Duplicates.Where(item => item.Checked).Take(3).ToArray();
            if (selected.Length != 2) {
                await MessageBoxService.Show("请恰好勾选 2 个文件，再选择合并所在文件夹。",
                    title: "合并文件夹");
                return;
            }
            string firstFile = selected[0].ItemInfo.Path, secondFile = selected[1].ItemInfo.Path;
            string? firstFolder = Path.GetDirectoryName(firstFile), secondFolder = Path.GetDirectoryName(secondFile);
            if (string.IsNullOrEmpty(firstFolder) || string.IsNullOrEmpty(secondFolder)) {
                await MessageBoxService.Show("无法确定勾选文件的所在目录。", title: "合并文件夹");
                return;
            }
            if (Path.GetFullPath(firstFolder).Equals(Path.GetFullPath(secondFolder),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                await MessageBoxService.Show("两份勾选文件已经位于同一个文件夹，无需合并。",
                    title: "合并文件夹");
                return;
            }

            var dialog = new FolderMergeDialog(firstFolder, secondFolder);
            FolderMergeDialogChoice? choice =
                await dialog.ShowDialog<FolderMergeDialogChoice?>(ApplicationHelpers.MainWindow);
            if (choice == null) return;

            FolderMergePlan? prepared = null;
            string? preparationError = null;
            IsBusy = true;
            IsBusyOverlayText = "正在核对源目录的全部文件、子目录和同名冲突…";
            try {
                prepared = await Task.Run(() =>
                    FolderMergeTransfer.BuildPlan(choice.SourceFolder, choice.TargetFolder));
            }
            catch (Exception ex) { preparationError = ex.Message; }
            finally { IsBusy = false; }
            if (prepared == null) {
                await MessageBoxService.Show($"无法生成安全合并计划，未改动文件：\n\n{preparationError}",
                    title: "合并文件夹");
                return;
            }
            FolderMergePlan plan = prepared;

            if (!selected.Any(item => FolderMergeTransfer.ContainsPath(plan.SourceRoot, item.ItemInfo.Path))) {
                await MessageBoxService.Show(
                    "编辑后的源目录必须包含当前勾选的两份文件中的至少一份，避免误操作其他目录。未开始移动。",
                    title: "合并文件夹");
                return;
            }
            if (plan.Files.Count == 0) {
                await MessageBoxService.Show("源目录没有可移动文件，未执行。", title: "合并文件夹");
                return;
            }
            if (!ScanEngine.ValidateFolderMergeDatabaseMoves(plan.Moves, out string dbError)) {
                await MessageBoxService.Show($"数据库记录存在路径冲突，未开始复制：\n\n{dbError}",
                    title: "合并文件夹");
                return;
            }

            if (!Directory.Exists(plan.TargetRoot)) {
                var create = await MessageBoxService.Show(
                    $"目标文件夹不存在：\n{plan.TargetRoot}\n\n是否在确认合并后创建此目录？",
                    MessageBoxButtons.Yes | MessageBoxButtons.No,
                    title: "创建目标文件夹", defaultButton: MessageBoxButtons.No);
                if (create != MessageBoxButtons.Yes) return;
            }

            string conflicts = plan.CollisionRenames == 0 ? "无" :
                $"{plan.CollisionRenames:N0} 个（保留目标同名文件，源文件将以“(VDF合并N)”重命名）";
            string examples = plan.CollisionRenames == 0 ? string.Empty :
                "\n\n同名冲突示例：\n" +
                string.Join("\n", plan.Files.Where(file => file.Renamed).Take(4)
                    .Select(file => $"{Path.GetFileName(file.SourcePath)} → {Path.GetFileName(file.DestinationPath)}"));
            var confirmation = await MessageBoxService.Show(
                $"源文件夹（被合并）：\n{plan.SourceRoot}\n\n" +
                $"目标文件夹（保留）：\n{plan.TargetRoot}\n\n" +
                $"源文件：{plan.Files.Count:N0} 个，容量 {plan.TotalBytes.BytesToString()}，" +
                $"子文件夹 {plan.SourceFolderCount:N0} 个。\n同名冲突：{conflicts}" +
                examples + "\n\n将先复制源文件及子目录、校验每个文件的 SHA-256，" +
                "全部成功后写入 VDF 数据库增量日志，再删除源文件。源文件删除失败时保留原件并报告。" +
                "\n\n本操作移动整个源目录内容，不只是勾选的两份文件。确定执行吗？",
                MessageBoxButtons.Yes | MessageBoxButtons.No,
                title: "确认整目录合并", defaultButton: MessageBoxButtons.No);
            if (confirmation != MessageBoxButtons.Yes) return;
            if (IsBusy || IsScanning || IsResultsFilterRefreshing) return;

            FolderMergeOutcome? outcome = null;
            string? executionError = null;
            IsBusy = true;
            IsBusyOverlayText = "准备安全合并文件夹…";
            var progress = new Progress<string>(message => IsBusyOverlayText = message);
            try {
                outcome = await Task.Run(() => FolderMergeTransfer.ExecuteAsync(plan,
                    moves => {
                        bool success = ScanEngine.CommitFolderMergeDatabaseMoves(moves, out string error);
                        return (success, error);
                    }, progress));
            }
            catch (Exception ex) { executionError = ex.Message; }
            finally { IsBusy = false; }
            if (outcome == null) {
                await MessageBoxService.Show(
                    $"文件夹合并发生异常，源文件安全状态需要人工核对：\n\n{executionError}",
                    title: "合并文件夹");
                return;
            }

            if (!outcome.Committed) {
                await MessageBoxService.Show(
                    "合并未完成：已停止删除源文件，并尝试清理新复制的副本。\n\n" +
                    string.Join("\n", outcome.Errors.Take(8)),
                    title: "合并文件夹");
                return;
            }

            // Refresh ALL matching duplicate rows, not just the two checkmarks. The DB
            // already tracks all indexed source paths via its durable incremental journal.
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var remap = plan.Moves.ToDictionary(move => move.OldPath, move => move.NewPath, comparer);
            int updated = 0;
            var changedGroups = new HashSet<Guid>();
            foreach (DuplicateItemVM item in Duplicates) {
                string original;
                try { original = Path.GetFullPath(item.ItemInfo.Path); }
                catch { continue; }
                if (!remap.TryGetValue(original, out string? destination)) continue;
                item.ItemInfo.Path = destination;
                item.ItemInfo.Folder = Path.GetDirectoryName(destination) ?? string.Empty;
                changedGroups.Add(item.ItemInfo.GroupId);
                updated++;
            }
            // Paths mutate in place, not via CollectionChanged. Invalidate the async
            // result/filter and resource-coverage snapshots before rerendering.
            foreach (Guid id in changedGroups) MarkResultItemMutation(id);
            resultsFolderStatsCache = null;
            resultsFolderMatchCountsCache = null;
            lastCompletedPathFilter = null;
            ResetGroupQualityAnalysis();
            RequestFilterResultsRefresh();
            RefreshResultsView();
            RefreshGroupStatsFast();
            string backupWarning = string.Empty;
            if (SettingsFile.Instance.BackupAfterListChanged) {
                try { await ExportScanResults(BackupScanResultsFile); }
                catch (Exception ex) { backupWarning = $"\n扫描结果备份未能同步：{ex.Message}"; }
            }

            string remaining = outcome.RemainingSources.Count == 0 ? string.Empty :
                $"\n\n⚠ {outcome.RemainingSources.Count:N0} 个源文件未能删除，原件仍保留在源目录：" +
                "\n" + string.Join("\n", outcome.RemainingSources.Take(6));
            string otherWarnings = outcome.Errors.Count == 0 ? string.Empty :
                "\n\n提示：\n" + string.Join("\n", outcome.Errors.Take(6));
            await MessageBoxService.Show(
                $"合并文件已经复制并验证完成。\n\n目标：{plan.TargetRoot}\n" +
                $"已复制：{outcome.Copied:N0} 个文件\n" +
                $"已删除源文件：{outcome.RemovedSources:N0} 个\n" +
                $"已更新当前列表地址：{updated:N0} 项；数据库路径已提交" +
                remaining + otherWarnings + backupWarning,
                title: "文件夹合并结果");
        }
    }
}
