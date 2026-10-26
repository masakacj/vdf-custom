// Explicit, one-group-on-demand video quality analysis. Never traverses the scan database.
using System.Reactive;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
    public partial class MainWindowVM {
        readonly Dictionary<Guid, string> checkedGroupQualityStatuses = new();
        CancellationTokenSource? groupQualityCancellation;
        Guid? analyzingQualityGroupId;

        void ResetGroupQualityAnalysis() {
            groupQualityCancellation?.Cancel();
            checkedGroupQualityStatuses.Clear();
            GroupQualityOnDemand.ClearForNewResults();
        }

        void RestoreGroupQualityStatuses(IReadOnlyList<ResultsGroupHeader> groups) {
            if (checkedGroupQualityStatuses.Count == 0) return;
            foreach (var group in groups)
                if (checkedGroupQualityStatuses.TryGetValue(group.GroupId, out string? text))
                    group.QualityStatusText = text;
        }

        static bool IsAnalyzableGroup(ResultsGroupHeader header, out string reason) {
            reason = "";
            if (header.Rows.Count is < 2 or > GroupQualityOnDemand.MaxGroupMembers) {
                reason = "每次仅支持一个组的 2–8 个视频。较大组请缩小结果范围后重试。";
                return false;
            }
            if (header.Rows.Any(row => row.Item.ItemInfo.IsImage)) {
                reason = "当前检测只支持视频，不支持图片组。";
                return false;
            }
            const DuplicateFlags unsupported =
                DuplicateFlags.PartialClip | DuplicateFlags.Flipped | DuplicateFlags.AiMatched;
            if (header.Rows.Any(row => (row.Item.ItemInfo.Flags & unsupported) != 0)) {
                reason = "局部片段、翻转或 AI 匹配的时间轴/画面可能不一致，本功能不对该组作画质推断。";
                return false;
            }
            double[] times = header.Rows.Select(row => row.Item.ItemInfo.Duration.TotalSeconds).ToArray();
            if (times.Any(t => t < 5) || times.Max() - times.Min() > Math.Max(1, times.Max() * 0.01)) {
                reason = "视频时长不同或元数据不足，无法安全按对应时间点对齐比较。";
                return false;
            }
            return true;
        }

        public ReactiveCommand<ResultsGroupHeader, Unit> AnalyzeGroupQualityCommand =>
            ReactiveCommand.CreateFromTask<ResultsGroupHeader>(AnalyzeGroupQualityAsync);

        async Task AnalyzeGroupQualityAsync(ResultsGroupHeader? group) {
            if (group == null) return;
            if (groupQualityCancellation != null) {
                if (analyzingQualityGroupId == group.GroupId)
                    groupQualityCancellation.Cancel();
                else
                    group.QualityStatusText = "其他分组检测进行中，请完成后再运行。";
                return;
            }
            if (IsBusy || IsScanning || IsResultsFilterRefreshing) {
                group.QualityStatusText = "扫描、数据库操作或结果刷新期间不可检测。";
                return;
            }
            if (!IsAnalyzableGroup(group, out string reason)) {
                group.QualityStatusText = reason;
                return;
            }
            // Snapshot only the group header's currently visible members: no scan DB walk.
            var members = group.Rows.Select(row => row.Item).ToArray();
            var sources = new List<OnDemandVideoSource>(members.Length);
            foreach (var item in members) {
                if (!TryParseFrameSize(item.ItemInfo.FrameSize, out int width, out int height)) {
                    group.QualityStatusText = "分辨率元数据缺失，请重新扫描本组。";
                    return;
                }
                sources.Add(new OnDemandVideoSource(item.ItemInfo.Path,
                    item.ItemInfo.SizeLong, item.ItemInfo.Duration, width, height));
            }
            // Clicking 'check quality' again cancels rather than launching parallel FFmpeg work.
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            groupQualityCancellation = cancellation;
            analyzingQualityGroupId = group.GroupId;
            checkedGroupQualityStatuses[group.GroupId] = "正在按需读取本组视频…";
            group.QualityStatusText = "正在按需读取本组视频…（再点一次可取消）";
            var progress = new Progress<string>(message => {
                // Progress is created on the UI thread; only the clicked header is updated,
                // not the 200k-group flattened list.
                group.QualityStatusText = message + "（再点一次可取消）";
            });
            try {
                var report = await Task.Run(async () => {
                    string executable = ScanEngine.GetFfmpegPathForGroupQuality();
                    return await GroupQualityOnDemand.ProbeAsync(
                        sources, executable, cancellation.Token, progress);
                }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (IsScanning || !resultsGroups.Any(g => g.GroupId == group.GroupId &&
                    g.Rows.Select(row => row.Item).SequenceEqual(members))) {
                    group.QualityStatusText = "检测期间结果组已变化，已丢弃旧报告。";
                    return;
                }

                foreach (var item in members) GroupQualityOnDemand.RemoveFinding(item);
                var foundByPath = report.Findings.ToDictionary(f => f.Path,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var item in members)
                    if (foundByPath.TryGetValue(item.ItemInfo.Path, out var finding))
                        GroupQualityOnDemand.SetFinding(item, finding);

                checkedGroupQualityStatuses[group.GroupId] = report.Summary;
                group.QualityStatusText = report.Summary;

                // Only rerank the clicked group. No file is checked or deleted by analysis.
                if (!TryRefreshSingleGroupPresentation(group.GroupId))
                    RebuildResultsList();

                string details = string.Join(Environment.NewLine,
                    report.Findings.Select(f =>
                        $"{Path.GetFileName(f.Path)}：{f.Reason}"));
                string errors = string.Join(Environment.NewLine, report.Errors);
                await MessageBoxService.Show(report.Summary +
                    (details.Length == 0 ? "" : Environment.NewLine + Environment.NewLine + details) +
                    (errors.Length == 0 ? "" : Environment.NewLine + Environment.NewLine + errors) +
                    Environment.NewLine + Environment.NewLine +
                    "提醒：只能确认抽样画面相似，无法证明原始来源、音轨优劣或整段视频质量。未自动勾选/删除任何文件。",
                    title: "本组画质检测");
            }
            catch (OperationCanceledException) {
                checkedGroupQualityStatuses[group.GroupId] = "已取消画质检测";
                group.QualityStatusText = "已取消画质检测";
            }
            catch (Exception ex) {
                checkedGroupQualityStatuses[group.GroupId] = $"检测失败：{ex.Message}";
                group.QualityStatusText = $"检测失败：{ex.Message}";
                Logger.Instance.Warn($"Manual group quality check failed: {ex}");
            }
            finally {
                groupQualityCancellation = null;
                analyzingQualityGroupId = null;
            }
        }
    }
}
