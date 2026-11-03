// Manual, one-group-only RGB watermark comparison. Never walks ScannedFiles.db.
using System.Reactive;
using System.Linq;
using System.Threading;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
    public partial class MainWindowVM {
        readonly Dictionary<Guid, string> groupWatermarkStatuses = new();
        CancellationTokenSource? groupWatermarkCancellation;
        Guid? activeWatermarkGroupId;

        void ResetGroupWatermarkAnalysis() {
            groupWatermarkCancellation?.Cancel();
            groupWatermarkStatuses.Clear();
            GroupWatermarkOnDemand.ClearForNewResults();
        }

        void RestoreGroupWatermarkStatuses(IReadOnlyList<ResultsGroupHeader> groups) {
            if (groupWatermarkStatuses.Count == 0) return;
            foreach (var group in groups)
                if (groupWatermarkStatuses.TryGetValue(group.GroupId, out string? text))
                    group.WatermarkStatusText = text;
        }

        static bool IsWatermarkComparableGroup(ResultsGroupHeader header, out string reason) {
            reason = string.Empty;
            if (header.Rows.Count is < 2 or > GroupWatermarkOnDemand.MaxGroupMembers) {
                reason = "水印对比每组支持 2–8 个视频，较大组请缩小范围。";
                return false;
            }
            if (header.Rows.Any(row => row.Item.ItemInfo.IsImage)) {
                reason = "当前水印对比仅支持视频，不支持图片组。";
                return false;
            }
            const DuplicateFlags unsupported =
                DuplicateFlags.PartialClip | DuplicateFlags.Flipped | DuplicateFlags.AiMatched;
            if (header.Rows.Any(row => (row.Item.ItemInfo.Flags & unsupported) != 0)) {
                reason = "局部片段、翻转或 AI 匹配的视频可能无法按时间轴对齐，暂不支持本组水印对比。";
                return false;
            }
            double[] times = header.Rows.Select(row => row.Item.ItemInfo.Duration.TotalSeconds).ToArray();
            if (times.Any(t => t < 5) || times.Max() - times.Min() > Math.Max(1, times.Max() * 0.01)) {
                reason = "视频时长不匹配，无法可靠按相同时间位置分析固定水印。";
                return false;
            }
            return true;
        }

        // Only allow the watermark heuristic to influence BEST when the rest of the
        // quality metadata is equivalent. 4K/HDR/60FPS or better audio can matter more.
        internal static bool HasComparableWatermarkBestMetadata(IReadOnlyList<DuplicateItemVM> members) {
            if (members.Count < 2) return false;
            var first = members[0].ItemInfo;
            return members.Skip(1).All(m => {
                var d = m.ItemInfo;
                return Math.Abs(d.Fps - first.Fps) <= 0.5f &&
                    string.Equals(d.FrameSize, first.FrameSize, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.HdrFormat, first.HdrFormat, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.AudioFormat, first.AudioFormat, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.AudioChannel, first.AudioChannel, StringComparison.OrdinalIgnoreCase) &&
                    d.AudioSampleRate == first.AudioSampleRate &&
                    Math.Abs(d.AudioBitRateKbs - first.AudioBitRateKbs) <=
                        Math.Max(10m, first.AudioBitRateKbs * 0.05m);
            });
        }

        // Synchronous command dispatch permits a second click to cancel the active check.
        // ReactiveCommand.CreateFromTask disables re-execution, so it cannot implement
        // the cancel-on-repeat affordance correctly.
        public ReactiveCommand<ResultsGroupHeader, Unit> AnalyzeGroupWatermarkCommand =>
            ReactiveCommand.Create<ResultsGroupHeader>(header => {
                _ = AnalyzeGroupWatermarkAsync(header);
            });

        async Task AnalyzeGroupWatermarkAsync(ResultsGroupHeader? group) {
            if (group == null) return;
            if (groupWatermarkCancellation != null) {
                if (activeWatermarkGroupId == group.GroupId)
                    groupWatermarkCancellation.Cancel();
                else
                    group.WatermarkStatusText = "另一个分组正在检测水印，请完成或取消后再检测本组。";
                return;
            }
            if (IsBusy || IsScanning || IsResultsFilterRefreshing) {
                group.WatermarkStatusText = "扫描、数据库操作或结果刷新期间不可检测。";
                return;
            }
            if (!IsWatermarkComparableGroup(group, out string reason)) {
                group.WatermarkStatusText = reason;
                return;
            }
            var members = group.Rows.Select(row => row.Item).ToArray();
            if (members.Select(item => item.ItemInfo.Path)
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != members.Length) {
                group.WatermarkStatusText = "本组包含相同路径的重复记录，无法作为不同视频比较。";
                return;
            }
            var sources = new List<WatermarkVideoSource>(members.Length);
            foreach (var member in members) {
                if (!TryParseFrameSize(member.ItemInfo.FrameSize, out int width, out int height)) {
                    group.WatermarkStatusText = "视频分辨率元数据不足，请重新扫描本组。";
                    return;
                }
                sources.Add(new WatermarkVideoSource(member.ItemInfo.Path,
                    member.ItemInfo.SizeLong, member.ItemInfo.Duration, width, height));
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            groupWatermarkCancellation = cancellation;
            activeWatermarkGroupId = group.GroupId;
            groupWatermarkStatuses[group.GroupId] = "正在抽取彩色帧检测水印…";
            group.WatermarkStatusText = "正在检测水印…（再次点击可取消）";
            var progress = new Progress<string>(message => {
                if (!ReferenceEquals(groupWatermarkCancellation, cancellation) ||
                    cancellation.IsCancellationRequested) return;
                group.WatermarkStatusText = message + "（再次点击可取消）";
            });

            try {
                WatermarkReport report = await Task.Run(async () => {
                    string executable = ScanEngine.GetFfmpegPathForGroupQuality();
                    return await GroupWatermarkOnDemand.ProbeAsync(
                        sources, executable, cancellation.Token, progress);
                }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (IsScanning || !resultsGroups.Any(g => g.GroupId == group.GroupId &&
                    g.Rows.Select(row => row.Item).SequenceEqual(members))) {
                    group.WatermarkStatusText = "检测期间本组文件发生变化，已丢弃旧结果。";
                    return;
                }

                // Show all comparative evidence, but only influence BEST if track
                // metadata matches. No auto-check, auto-delete or disk mutation.
                bool canRerank = HasComparableWatermarkBestMetadata(members);
                foreach (var item in members) GroupWatermarkOnDemand.RemoveFinding(item);
                var byPath = report.Findings.ToDictionary(f => f.Path,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                // Always show findings in the member's detail view, regardless of
                // audio/FPS/HDR differences. Only the BEST penalty is gated.
                foreach (var item in members)
                    if (byPath.TryGetValue(item.ItemInfo.Path, out WatermarkFinding? finding))
                        GroupWatermarkOnDemand.SetFinding(item, finding, canAffectBest: canRerank);
                string perCopy = report.Findings.Count == 0 ? "" :
                    " · " + string.Join("；", report.Findings
                        .OrderByDescending(f => f.RegionCount)
                        .Select(f => $"{Path.GetFileName(f.Path)} 疑似+{f.RegionCount}处"));
                string summary = report.Summary + perCopy +
                    (canRerank ? " · 仅辅助 BEST（仍需复核）" : " · 视频参数不一致，不影响 BEST");
                if (report.SkippedPairs > 0)
                    summary += $" · {report.SkippedPairs} 对无法可靠对齐";
                if (report.Errors.Count > 0)
                    summary += $" · {report.Errors.Count} 个文件取帧失败";
                groupWatermarkStatuses[group.GroupId] = summary;
                group.WatermarkStatusText = summary;

                if (!TryRefreshSingleGroupPresentation(group.GroupId))
                    RebuildResultsList();

                string details = report.Findings.Count == 0
                    ? "未识别出可确定属于某份副本的额外水印。"
                    : string.Join(Environment.NewLine + Environment.NewLine,
                        report.Findings.Select(f => $"{Path.GetFileName(f.Path)}：\n{f.Reason}"));
                string errors = report.Errors.Count == 0 ? "" :
                    "\n\n读取失败：\n" + string.Join("\n", report.Errors.Take(8));
                await MessageBoxService.Show(
                    summary + "\n\n" + details + errors + "\n\n" + report.Limitations,
                    title: "本组水印检测");
            }
            catch (OperationCanceledException) {
                groupWatermarkStatuses[group.GroupId] = "已取消水印检测";
                group.WatermarkStatusText = "已取消水印检测";
            }
            catch (Exception ex) {
                groupWatermarkStatuses[group.GroupId] = $"水印检测失败：{ex.Message}";
                group.WatermarkStatusText = $"水印检测失败：{ex.Message}";
                Logger.Instance.Warn($"Manual group watermark check failed: {ex}");
            }
            finally {
                if (ReferenceEquals(groupWatermarkCancellation, cancellation)) {
                    groupWatermarkCancellation = null;
                    activeWatermarkGroupId = null;
                }
            }
        }
    }
}
