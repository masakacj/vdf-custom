// Explicit, bounded machine-vision watermark comparison for a SINGLE duplicate group.
// No full-library scan and no model downloads.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Linq;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Data {
    internal sealed record WatermarkVideoSource(
        string Path, long ExpectedBytes, TimeSpan Duration, int Width, int Height);

    internal sealed record WatermarkVideoSamples(
        string Path, int Width, int Height, IReadOnlyList<byte[]> Frames);

    internal sealed record WatermarkRegion(
        string Position, int Left, int Top, int Width, int Height,
        int Cells, int SupportingPairs) {
        internal double CoveragePercent => Cells * 100.0 / GroupWatermarkOnDemand.CellCount;
    }

    internal sealed record WatermarkFinding(string Path, IReadOnlyList<WatermarkRegion> Regions) {
        internal int RegionCount => Regions.Count;
        internal double CoveragePercent => Regions.Sum(r => r.CoveragePercent);
        internal int Penalty => Math.Min(12, RegionCount * 2 + (CoveragePercent >= 2.0 ? 1 : 0));
        internal string Reason =>
            $"相比组内其他版本，疑似额外固定覆盖 {RegionCount} 处：" +
            string.Join("、", Regions.Take(6).Select(r =>
                $"{r.Position}（约 {r.CoveragePercent:0.#}% 画面）")) +
            "；不等于确认来源或识别文字内容";
    }

    internal sealed record WatermarkReport(
        int DecodedFiles, int ComparedPairs, int SkippedPairs,
        IReadOnlyList<WatermarkFinding> Findings,
        IReadOnlyList<string> Errors) {
        internal string Summary => ComparedPairs == 0
            ? "抽样画面未能可靠对齐，无法判断水印位置（未修改 BEST）"
            : Findings.Count == 0
                ? $"已比较 {DecodedFiles} 个视频、{ComparedPairs} 对 · 未检出相对其他版本额外的固定覆盖；不能据此证明视频无水印"
                : $"已比较 {DecodedFiles} 个视频、{ComparedPairs} 对 · {Findings.Count} 个版本疑似有额外固定水印/角标" +
                  $"（最多 {Findings.Max(f => f.RegionCount)} 处，需人工复核）";
        internal string Limitations =>
            "仅对同内容、同时间轴的抽样帧进行相对比较。所有副本共有的水印、移动水印、" +
            "逐帧变化的遮挡、不同时间轴/裁剪或很淡的小水印可能无法识别；" +
            "字幕、游戏 HUD、台标也可能被当成固定覆盖。不会自动删除或勾选文件。";
    }

    /// <summary>
    /// Decode 8 fixed points (320x320 RGB, padded to preserve aspect ratio) per file.
    /// Compare pairwise cell residuals across time, then attribute persistent, localized
    /// differences using stable spatial edges and lower temporal variance. No pretrained
    /// classifier is claimed; this is conservative comparative evidence, not OCR.
    /// </summary>
    internal static class GroupWatermarkOnDemand {
        internal const int Side = 320;
        internal const int Channels = 3;
        internal const int TileSide = 16;
        internal const int TilesPerSide = Side / TileSide;
        internal const int CellCount = TilesPerSide * TilesPerSide;
        internal const int MaxGroupMembers = 8;
        internal const int FramesPerVideo = 8;
        internal const int FrameBytes = Side * Side * Channels;
        static readonly double[] SampleFractions =
            [0.08, 0.19, 0.30, 0.41, 0.54, 0.65, 0.77, 0.89];

        static ConditionalWeakTable<DuplicateItemVM, WatermarkFinding> findings = new();
        internal static bool HasFindings { get; private set; }
        internal static WatermarkFinding? Finding(DuplicateItemVM item) =>
            findings.TryGetValue(item, out WatermarkFinding? result) ? result : null;
        internal static int Penalty(DuplicateItemVM item) => Finding(item)?.Penalty ?? 0;
        internal static void SetFinding(DuplicateItemVM item, WatermarkFinding finding) {
            findings.Remove(item);
            findings.Add(item, finding);
            HasFindings = true;
        }
        internal static void RemoveFinding(DuplicateItemVM item) => findings.Remove(item);
        internal static void ClearForNewResults() {
            findings = new();
            HasFindings = false;
        }

        internal static async Task<WatermarkReport> ProbeAsync(
            IReadOnlyList<WatermarkVideoSource> sources, string executable,
            CancellationToken cancellationToken, IProgress<string>? progress = null) {
            if (sources.Count is < 2 or > MaxGroupMembers)
                throw new ArgumentOutOfRangeException(nameof(sources), "每组只支持 2–8 个视频。");
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("没有找到 FFmpeg。请先在 VDF 中配置 FFmpeg。");
            var samples = new List<WatermarkVideoSamples>(sources.Count);
            var errors = new List<string>();
            for (int i = 0; i < sources.Count; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                WatermarkVideoSource source = sources[i];
                try {
                    if (source.Duration.TotalSeconds < 5 || source.Width < 16 || source.Height < 16)
                        throw new InvalidDataException("缺少有效的视频时长或分辨率");
                    var info = new FileInfo(source.Path);
                    if (!info.Exists) throw new FileNotFoundException("文件不存在或磁盘离线");
                    long length = info.Length;
                    DateTime changed = info.LastWriteTimeUtc;
                    if (source.ExpectedBytes > 0 && length != source.ExpectedBytes)
                        throw new InvalidDataException("视频大小已经改变，请重新扫描");
                    var frames = new List<byte[]>(FramesPerVideo);
                    for (int f = 0; f < FramesPerVideo; f++) {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report($"检测水印 {i + 1}/{sources.Count} · 抽帧 {f + 1}/{FramesPerVideo}");
                        frames.Add(await ReadFrameAsync(executable, source.Path,
                            source.Duration.TotalSeconds * SampleFractions[f], cancellationToken));
                    }
                    info.Refresh();
                    if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != changed)
                        throw new IOException("读取期间视频被修改，已丢弃这份采样");
                    samples.Add(new WatermarkVideoSamples(
                        source.Path, source.Width, source.Height, frames));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(source.Path)}：{ex.Message}"); }
            }
            return Analyze(samples, errors, cancellationToken);
        }

        static async Task<byte[]> ReadFrameAsync(
            string executable, string path, double seconds, CancellationToken cancel) {
            var psi = new ProcessStartInfo(executable) {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            string[] args = [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "2",
                "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", $"scale={Side}:{Side}:force_original_aspect_ratio=decrease:flags=bicubic," +
                       $"pad={Side}:{Side}:(ow-iw)/2:(oh-ih)/2:color=black,format=rgb24",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
            ];
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try {
                if (!process.Start()) throw new IOException("FFmpeg 启动失败");
                using var output = new MemoryStream(FrameBytes);
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
                Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(copy, stderr, process.WaitForExitAsync(timeout.Token));
                if (process.ExitCode != 0 || output.Length != FrameBytes)
                    throw new InvalidDataException(
                        $"FFmpeg 未输出完整彩色帧 ({process.ExitCode}): {stderr.Result[..Math.Min(160, stderr.Result.Length)]}");
                return output.ToArray();
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) {
                throw new TimeoutException("单帧解码超过 15 秒");
            }
            finally {
                try {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                } catch { }
            }
        }

        internal static WatermarkReport Analyze(
            IReadOnlyList<WatermarkVideoSamples> samples,
            IReadOnlyList<string>? errors = null, CancellationToken cancellationToken = default) {
            var valid = samples
                .Where(s => s.Width > 0 && s.Height > 0 &&
                    s.Frames.Count >= FramesPerVideo &&
                    s.Frames.All(f => f.Length == FrameBytes))
                .Take(MaxGroupMembers).ToArray();
            if (valid.Length < 2)
                return new WatermarkReport(valid.Length, 0, 0, [], errors ?? []);

            var signatures = valid.Select(s => GetTemporalSignature(s, cancellationToken)).ToArray();
            var votes = valid.Select(_ => new int[CellCount]).ToArray();
            int compared = 0, skipped = 0;
            for (int i = 0; i < valid.Length - 1; i++) {
                for (int j = i + 1; j < valid.Length; j++) {
                    cancellationToken.ThrowIfCancellationRequested();
                    double aspectA = (double)valid[i].Width / valid[i].Height;
                    double aspectB = (double)valid[j].Width / valid[j].Height;
                    if (Math.Abs(aspectA / aspectB - 1) > 0.02) { skipped++; continue; }
                    var distances = CellDistances(valid[i], valid[j], cancellationToken);
                    double global = distances.Average();
                    // When images are shifted/cropped/graded the residual is not a logo.
                    if (global > 19.0) { skipped++; continue; }
                    double[] mean = new double[CellCount];
                    for (int f = 0; f < FramesPerVideo; f++)
                        for (int cell = 0; cell < CellCount; cell++)
                            mean[cell] += distances[f * CellCount + cell] / FramesPerVideo;
                    double[] ordered = mean.OrderBy(v => v).ToArray();
                    double baseline = ordered[CellCount / 2];
                    double threshold = Math.Max(5.0, baseline * 2.7 + 3);
                    double frameThreshold = Math.Max(3.8, baseline * 1.9 + 1.6);
                    var marksA = new bool[CellCount];
                    var marksB = new bool[CellCount];
                    int changedCells = 0;
                    for (int cell = 0; cell < CellCount; cell++) {
                        if (mean[cell] < threshold) continue;
                        int present = 0;
                        for (int f = 0; f < FramesPerVideo; f++)
                            if (distances[f * CellCount + cell] >= frameThreshold) present++;
                        if (present < 6) continue;
                        changedCells++;
                        double ea = signatures[i].StableEdges[cell],
                            eb = signatures[j].StableEdges[cell];
                        double va = signatures[i].MotionVariance[cell],
                            vb = signatures[j].MotionVariance[cell];

                        // Directional attribution: a true static mark can add fixed
                        // high-frequency edges and/or suppress underlying motion.
                        bool edgeA = ea > eb + 1.5 && ea > eb * 1.23;
                        bool edgeB = eb > ea + 1.5 && eb > ea * 1.23;
                        bool motionA = vb > 20 && va < vb * 0.62 && vb - va > 10;
                        bool motionB = va > 20 && vb < va * 0.62 && va - vb > 10;
                        bool evidenceA = edgeA || motionA;
                        bool evidenceB = edgeB || motionB;
                        if (evidenceA && !evidenceB) marksA[cell] = true;
                        else if (evidenceB && !evidenceA) marksB[cell] = true;
                    }
                    // A diffuse whole-video difference is compression/color/crop,
                    // not a localized watermark.
                    if (changedCells > CellCount * 0.19) { skipped++; continue; }
                    compared++;
                    for (int c = 0; c < CellCount; c++) {
                        if (marksA[c]) votes[i][c]++;
                        if (marksB[c]) votes[j][c]++;
                    }
                }
            }

            var findings = new List<WatermarkFinding>();
            for (int i = 0; i < valid.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                int activeCells = votes[i].Count(n => n > 0);
                if (activeCells > CellCount * 0.17) continue;
                var regions = ClusterRegions(votes[i]);
                if (regions.Count > 0 && regions.Count <= 10)
                    findings.Add(new WatermarkFinding(valid[i].Path, regions));
            }
            return new WatermarkReport(valid.Length, compared, skipped, findings, errors ?? []);
        }

        // Average RGB abs-difference for each 16x16 image cell and each sample time.
        // No reference image is assumed to be watermark-free.
        static double[] CellDistances(WatermarkVideoSamples a, WatermarkVideoSamples b,
            CancellationToken cancellationToken) {
            var result = new double[FramesPerVideo * CellCount];
            for (int f = 0; f < FramesPerVideo; f++) {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] pixelsA = a.Frames[f], pixelsB = b.Frames[f];
                for (int y = 0; y < Side; y++) {
                    int cellRow = (y / TileSide) * TilesPerSide;
                    for (int x = 0; x < Side; x++) {
                        int offset = (y * Side + x) * Channels;
                        int cell = f * CellCount + cellRow + x / TileSide;
                        result[cell] += (Math.Abs(pixelsA[offset] - pixelsB[offset]) +
                            Math.Abs(pixelsA[offset + 1] - pixelsB[offset + 1]) +
                            Math.Abs(pixelsA[offset + 2] - pixelsB[offset + 2])) /
                            (3d * TileSide * TileSide);
                    }
                }
            }
            return result;
        }

        internal sealed record TemporalSignature(double[] StableEdges, double[] MotionVariance);

        // Use *average signed RGB gradients* to measure spatial features that stay
        // fixed at the same pixel throughout the clip. Moving scene edges average out.
        static TemporalSignature GetTemporalSignature(WatermarkVideoSamples sample,
            CancellationToken cancellationToken) {
            var sums = new int[FrameBytes];
            var squareSums = new int[FrameBytes];
            foreach (byte[] frame in sample.Frames.Take(FramesPerVideo)) {
                cancellationToken.ThrowIfCancellationRequested();
                for (int i = 0; i < FrameBytes; i++) {
                    int value = frame[i];
                    sums[i] += value;
                    squareSums[i] += value * value;
                }
            }
            var edges = new double[CellCount];
            var motion = new double[CellCount];
            for (int ty = 0; ty < TilesPerSide; ty++) {
                cancellationToken.ThrowIfCancellationRequested();
                for (int tx = 0; tx < TilesPerSide; tx++) {
                    int cell = ty * TilesPerSide + tx;
                    double edgeSum = 0, motionSum = 0;
                    for (int y = ty * TileSide; y < (ty + 1) * TileSide; y++)
                        for (int x = tx * TileSide; x < (tx + 1) * TileSide; x++)
                            for (int c = 0; c < Channels; c++) {
                                int offset = (y * Side + x) * Channels + c;
                                double avg = (double)sums[offset] / FramesPerVideo;
                                motionSum += Math.Max(0,
                                    (double)squareSums[offset] / FramesPerVideo - avg * avg);
                                if (x + 1 < (tx + 1) * TileSide)
                                    edgeSum += Math.Abs(sums[offset + Channels] - sums[offset]) /
                                        (double)FramesPerVideo;
                                if (y + 1 < (ty + 1) * TileSide)
                                    edgeSum += Math.Abs(sums[offset + Side * Channels] - sums[offset]) /
                                        (double)FramesPerVideo;
                            }
                    edges[cell] = edgeSum / (TileSide * (TileSide - 1) * Channels * 2);
                    motion[cell] = motionSum / (TileSide * TileSide * Channels);
                }
            }
            return new TemporalSignature(edges, motion);
        }

        internal static IReadOnlyList<WatermarkRegion> ClusterRegions(int[] votes) {
            if (votes.Length != CellCount) throw new ArgumentException("Unexpected watermark mask length");
            var visited = new bool[CellCount];
            var regions = new List<WatermarkRegion>();
            for (int seed = 0; seed < CellCount; seed++) {
                if (votes[seed] <= 0 || visited[seed]) continue;
                var queue = new Queue<int>(); queue.Enqueue(seed); visited[seed] = true;
                int minX = TilesPerSide, minY = TilesPerSide, maxX = 0, maxY = 0;
                int cells = 0, supportingPairs = 0;
                while (queue.Count > 0) {
                    int cell = queue.Dequeue(), x = cell % TilesPerSide, y = cell / TilesPerSide;
                    cells++;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                    supportingPairs = Math.Max(supportingPairs, votes[cell]);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++) {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= TilesPerSide || ny >= TilesPerSide) continue;
                            int next = ny * TilesPerSide + nx;
                            if (votes[next] > 0 && !visited[next]) {
                                visited[next] = true;
                                queue.Enqueue(next);
                            }
                        }
                }
                // One hot block is usually noise; a whole-screen mask usually means
                // videos were not well aligned. Neither justifies a watermark claim.
                if (cells < 2 || cells > CellCount * 0.12) continue;
                int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
                string horizontal = cx < TilesPerSide / 3 ? "左" :
                    cx >= TilesPerSide * 2 / 3 ? "右" : "中";
                string vertical = cy < TilesPerSide / 3 ? "上" :
                    cy >= TilesPerSide * 2 / 3 ? "下" : "中";
                string position = horizontal == "中" && vertical == "中" ? "中央" :
                    horizontal == "中" ? vertical + "部" :
                    vertical == "中" ? horizontal + "侧" : horizontal + vertical;
                regions.Add(new WatermarkRegion(position,
                    minX * TileSide, minY * TileSide,
                    (maxX - minX + 1) * TileSide, (maxY - minY + 1) * TileSide,
                    cells, supportingPairs));
            }
            return regions.OrderByDescending(r => r.Cells).ToArray();
        }
    }
}
