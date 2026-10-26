// On-demand, bounded full-video sampling for one duplicate group. No database traversal.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Linq;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Data {
    internal sealed record OnDemandVideoSource(
        string Path, long ExpectedBytes, TimeSpan Duration, int Width, int Height);

    internal sealed record OnDemandVideoSamples(
        string Path, long Bytes, int Width, int Height, IReadOnlyList<byte[]> Frames);

    internal sealed record OnDemandQualityFinding(
        string Path, int Penalty, string Reason, double Similarity);

    internal sealed record OnDemandQualityReport(
        int DecodedFiles, int ComparedPairs,
        IReadOnlyList<OnDemandQualityFinding> Findings,
        IReadOnlyList<string> Errors) {
        internal string Summary =>
            $"已检测 {DecodedFiles} 个视频、{ComparedPairs} 对 · " +
            (ComparedPairs == 0 ? "有效可比样本不足，无法判断优劣" :
            Findings.Count == 0 ? "未发现明确的体积膨胀/劣化证据" :
                $"{Findings.Count} 个副本疑似体积膨胀或画质劣化") +
            (Errors.Count == 0 ? "" : $" · {Errors.Count} 个文件读取失败") +
            "（仅建议，仍需人工复核）";
    }

    /// <summary>
    /// Only called by the explicit per-group button: reads four points per video at 256x256,
    /// with per-process and total time limits. SSIM-like scores are low-res evidence, NOT
    /// a calibrated VMAF score, original-source proof or audio-quality comparison.
    /// </summary>
    internal static class GroupQualityOnDemand {
        internal const int Side = 256;
        internal const int MaxGroupMembers = 8;
        internal const int FramesPerVideo = 4;
        const int FrameBytes = Side * Side;
        static readonly double[] SampleFractions = [0.15, 0.36, 0.60, 0.82];
        static ConditionalWeakTable<DuplicateItemVM, OnDemandQualityFinding> findings = new();
        internal static bool HasFindings { get; private set; }
        internal static int Penalty(DuplicateItemVM item) =>
            findings.TryGetValue(item, out OnDemandQualityFinding? finding) ? finding.Penalty : 0;
        internal static OnDemandQualityFinding? Finding(DuplicateItemVM item) =>
            findings.TryGetValue(item, out OnDemandQualityFinding? finding) ? finding : null;
        internal static void SetFinding(DuplicateItemVM item, OnDemandQualityFinding finding) {
            findings.Remove(item);
            findings.Add(item, finding);
            HasFindings = true;
        }
        internal static void RemoveFinding(DuplicateItemVM item) => findings.Remove(item);
        internal static void ClearForNewResults() {
            findings = new();
            HasFindings = false;
        }

        internal static async Task<OnDemandQualityReport> ProbeAsync(
            IReadOnlyList<OnDemandVideoSource> sources, string executable,
            CancellationToken cancellationToken, IProgress<string>? progress = null) {
            if (sources.Count < 2 || sources.Count > MaxGroupMembers)
                throw new ArgumentOutOfRangeException(nameof(sources), "仅支持每组 2–8 个视频。");
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("没有找到 FFmpeg，请先在 VDF 中配置或下载 FFmpeg。");
            var samples = new List<OnDemandVideoSamples>();
            var errors = new List<string>();
            for (int i = 0; i < sources.Count; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                OnDemandVideoSource src = sources[i];
                try {
                    if (src.Duration.TotalSeconds < 5 || src.Width <= 0 || src.Height <= 0)
                        throw new InvalidDataException("缺少有效时长或分辨率");
                    var file = new FileInfo(src.Path);
                    if (!file.Exists) throw new FileNotFoundException("文件不存在或离线");
                    long bytes = file.Length;
                    DateTime lastWrite = file.LastWriteTimeUtc;
                    if (src.ExpectedBytes > 0 && src.ExpectedBytes != bytes)
                        throw new InvalidDataException("文件大小与扫描记录不同，请重新扫描后再分析");
                    var frames = new List<byte[]>(FramesPerVideo);
                    for (int frame = 0; frame < FramesPerVideo; frame++) {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report($"检测本组 {i + 1}/{sources.Count} · 采样 {frame + 1}/{FramesPerVideo}…");
                        frames.Add(await ReadFrameAsync(executable, src.Path,
                            src.Duration.TotalSeconds * SampleFractions[frame], cancellationToken));
                    }
                    file.Refresh();
                    if (!file.Exists || bytes != file.Length || lastWrite != file.LastWriteTimeUtc)
                        throw new IOException("检测期间文件发生变化，本次采样结果已舍弃");
                    samples.Add(new OnDemandVideoSamples(src.Path, bytes, src.Width, src.Height, frames));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) {
                    errors.Add($"{Path.GetFileName(src.Path)}：{ex.Message}");
                }
            }
            return Analyze(samples, errors);
        }

        static async Task<byte[]> ReadFrameAsync(string executable, string path, double seconds,
            CancellationToken cancel) {
            var psi = new ProcessStartInfo(executable) {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            string[] args = [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "2",
                "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", $"scale={Side}:{Side}:flags=bicubic,format=gray",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"
            ];
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try {
                if (!process.Start()) throw new IOException("FFmpeg 启动失败");
                using var memory = new MemoryStream(FrameBytes);
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(memory, timeout.Token);
                Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(copy, stderr, process.WaitForExitAsync(timeout.Token));
                if (process.ExitCode != 0 || memory.Length != FrameBytes)
                    throw new InvalidDataException(
                        $"FFmpeg 未返回完整画面（{process.ExitCode}）：{stderr.Result[..Math.Min(200, stderr.Result.Length)]}");
                return memory.ToArray();
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) {
                throw new TimeoutException("视频取帧超过 15 秒");
            }
            finally {
                try {
                    if (process.StartInfo.RedirectStandardOutput && !process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        internal static OnDemandQualityReport Analyze(
            IReadOnlyList<OnDemandVideoSamples> samples,
            IReadOnlyList<string>? errors = null) {
            var suspects = new Dictionary<string, OnDemandQualityFinding>(StringComparer.OrdinalIgnoreCase);
            int comparisons = 0;
            for (int i = 0; i < samples.Count; i++) {
                for (int j = i + 1; j < samples.Count; j++) {
                    OnDemandVideoSamples a = samples[i], b = samples[j];
                    if (a.Frames.Count < FramesPerVideo || b.Frames.Count < FramesPerVideo ||
                        a.Frames.Any(f => f.Length != FrameBytes) || b.Frames.Any(f => f.Length != FrameBytes))
                        continue;
                    // Display aspect must match; downsampling a 4K master to a small gray frame
                    // cannot prove it offers no higher-resolution detail.
                    double aspectA = (double)a.Width / a.Height;
                    double aspectB = (double)b.Width / b.Height;
                    if (Math.Abs(aspectA / aspectB - 1) > 0.03) continue;
                    comparisons++;
                    OnDemandVideoSamples large = a.Bytes >= b.Bytes ? a : b;
                    OnDemandVideoSamples small = ReferenceEquals(large, a) ? b : a;
                    if (small.Bytes <= 0 || large.Bytes < small.Bytes * 1.35) continue;
                    double pixelRatio = (double)(long)large.Width * large.Height /
                        ((long)small.Width * small.Height);
                    if (pixelRatio is < 0.90 or > 1.10) continue;

                    double ssim = 0, detailLarge = 0, detailSmall = 0;
                    int similarFrames = 0;
                    for (int k = 0; k < FramesPerVideo; k++) {
                        byte[] lf = large.Frames[k], sf = small.Frames[k];
                        double score = StructuralSimilarity(lf, sf);
                        ssim += score;
                        if (score >= 0.975) similarFrames++;
                        detailLarge += Detail(lf);
                        detailSmall += Detail(sf);
                    }
                    ssim /= FramesPerVideo;
                    if (detailSmall < 1) continue;
                    double relativeDetail = detailLarge / detailSmall;
                    int penalty = 0;
                    string reason = "";
                    if (ssim >= 0.987 && similarFrames >= 3 && relativeDetail <= 1.08) {
                        penalty = 1;
                        reason = $"抽样画面近似（结构相似度 {ssim:P1}），体积较另一副本增加 {large.Bytes / (double)small.Bytes:0.0} 倍，无明显新增细节";
                    }
                    else if (ssim >= 0.93 && relativeDetail <= 0.82 && similarFrames >= 2) {
                        penalty = 2;
                        reason = $"文件更大但采样细节明显减少（结构相似度 {ssim:P1}），疑似二次编码劣化";
                    }
                    if (penalty == 0) continue;
                    var finding = new OnDemandQualityFinding(large.Path, penalty, reason, ssim);
                    if (!suspects.TryGetValue(large.Path, out var prior) || prior.Penalty < penalty)
                        suspects[large.Path] = finding;
                }
            }
            return new OnDemandQualityReport(samples.Count, comparisons,
                suspects.Values.ToArray(), errors ?? []);
        }

        // Spatially averaged 8x8 SSIM. Used only for near-identical frames, not as
        // a stand-in for a reference-free perceptual quality score.
        internal static double StructuralSimilarity(byte[] a, byte[] b) {
            if (a.Length != FrameBytes || b.Length != FrameBytes)
                throw new ArgumentException("Requires 256x256 grayscale frames.");
            const double c1 = 6.5025, c2 = 58.5225;
            double total = 0;
            for (int y = 0; y < Side; y += 8)
                for (int x = 0; x < Side; x += 8) {
                    double sa = 0, sb = 0, aa = 0, bb = 0, ab = 0;
                    for (int dy = 0; dy < 8; dy++)
                        for (int dx = 0; dx < 8; dx++) {
                            int at = (y + dy) * Side + x + dx;
                            int av = a[at], bv = b[at];
                            sa += av; sb += bv;
                            aa += av * av; bb += bv * bv; ab += av * bv;
                        }
                    double ma = sa / 64, mb = sb / 64;
                    double va = aa / 64 - ma * ma, vb = bb / 64 - mb * mb;
                    double cov = ab / 64 - ma * mb;
                    total += (2 * ma * mb + c1) * (2 * cov + c2) /
                        ((ma * ma + mb * mb + c1) * (va + vb + c2));
                }
            return Math.Clamp(total / ((Side / 8) * (Side / 8)), -1, 1);
        }

        static double Detail(byte[] f) {
            double sum = 0;
            for (int y = 0; y < Side - 1; y++)
                for (int x = 0; x < Side - 1; x++) {
                    int pos = y * Side + x;
                    sum += Math.Abs(f[pos] - f[pos + 1]) +
                        Math.Abs(f[pos] - f[pos + Side]);
                }
            return sum / (2 * (Side - 1) * (Side - 1));
        }
    }
}
