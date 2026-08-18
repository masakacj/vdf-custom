// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.GUI.Data;

namespace VDF.GUI.Tests;

public class LightweightQualityDiagnosticsTests {
    [Fact]
    public void HigherResolutionAndBitrate_WithLessRealDetail_IsFlaggedAsSuspectedSecondEncode() {
        var clean = new CachedVideoQualitySample(
            "clean", 1920, 1080, 8_000_000, 1_000_000_000,
            RepeatedFrames(CheckerFrame(32, 58), 8));
        var bloated = new CachedVideoQualitySample(
            "bloated", 3840, 2160, 18_000_000, 2_500_000_000,
            RepeatedFrames(SmoothFrame(32), 8));

        var findings = LightweightQualityDiagnostics.AnalyzeGroup(new[] { clean, bloated });

        var suspect = Assert.Single(findings);
        Assert.Equal("bloated", suspect.Key);
        Assert.True(suspect.Warning.HasFlag(LightweightQualityWarning.SuspectedTranscodeOrUpscale));
        Assert.False(suspect.Warning.HasFlag(LightweightQualityWarning.SuspectedWatermark));
        Assert.Contains("二次转码", suspect.Reason);
    }

    [Fact]
    public void PersistentLocalizedCornerOverlay_IsFlaggedAsSuspectedWatermark() {
        var cleanFrames = DynamicTexturedFrames(32, 9);
        var markedFrames = cleanFrames.Select(AddTopRightWatermark).ToList();
        var clean = new CachedVideoQualitySample(
            "clean", 1920, 1080, 8_000_000, 1_000_000_000, cleanFrames);
        var marked = new CachedVideoQualitySample(
            "marked", 1920, 1080, 8_500_000, 1_100_000_000, markedFrames);

        var findings = LightweightQualityDiagnostics.AnalyzeGroup(new[] { clean, marked });

        var suspect = Assert.Single(findings);
        Assert.Equal("marked", suspect.Key);
        Assert.True(suspect.Warning.HasFlag(LightweightQualityWarning.SuspectedWatermark));
        Assert.Contains("水印", suspect.Reason);
    }

    [Fact]
    public void GenuineHigherDetailEncode_IsNotFlaggedJustBecauseItIsLarger() {
        var lower = new CachedVideoQualitySample(
            "lower", 1920, 1080, 6_000_000, 800_000_000,
            RepeatedFrames(CheckerFrame(32, 28), 8));
        var higher = new CachedVideoQualitySample(
            "higher", 3840, 2160, 18_000_000, 2_400_000_000,
            RepeatedFrames(CheckerFrame(32, 72), 8));

        var findings = LightweightQualityDiagnostics.AnalyzeGroup(new[] { lower, higher });

        Assert.Empty(findings);
    }

    [Fact]
    public void GlobalDetailDifferenceAcrossAllCorners_IsNotCalledAWatermark() {
        var softer = RepeatedFrames(CheckerFrame(32, 22), 8);
        var sharper = RepeatedFrames(CheckerFrame(32, 70), 8);
        var a = new CachedVideoQualitySample("a", 1920, 1080, 8_000_000, 1_000_000_000, softer);
        var b = new CachedVideoQualitySample("b", 1920, 1080, 9_000_000, 1_100_000_000, sharper);

        var findings = LightweightQualityDiagnostics.AnalyzeGroup(new[] { a, b });

        Assert.DoesNotContain(findings, f => f.Warning.HasFlag(LightweightQualityWarning.SuspectedWatermark));
    }

    static IReadOnlyList<byte[]> RepeatedFrames(byte[] frame, int count) =>
        Enumerable.Range(0, count).Select(_ => frame.ToArray()).ToList();

    static byte[] CheckerFrame(int n, int amplitude) {
        var frame = new byte[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                frame[y * n + x] = (byte)(110 + (((x + y) & 1) == 0 ? -amplitude : amplitude));
        return frame;
    }

    static byte[] SmoothFrame(int n) {
        var frame = new byte[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                frame[y * n + x] = (byte)(90 + x * 2 + y);
        return frame;
    }

    static IReadOnlyList<byte[]> DynamicTexturedFrames(int n, int count) {
        var frames = new List<byte[]>(count);
        for (int t = 0; t < count; t++) {
            var frame = new byte[n * n];
            for (int y = 0; y < n; y++) {
                for (int x = 0; x < n; x++) {
                    double wave = 18 * Math.Sin((x * 0.8 + y * 0.6 + t) / 3.0);
                    int texture = ((x * 17 + y * 11 + t * 7) % 23) - 11;
                    int value = (int)Math.Round(90 + x * 1.5 + y + wave + texture);
                    frame[y * n + x] = (byte)Math.Clamp(value, 0, 255);
                }
            }
            frames.Add(frame);
        }
        return frames;
    }

    static byte[] AddTopRightWatermark(byte[] source) {
        int n = (int)Math.Sqrt(source.Length);
        var output = source.ToArray();
        int x0 = n - 9;
        int x1 = n - 1;
        int y0 = 2;
        int y1 = 8;
        for (int y = y0; y < y1; y++) {
            for (int x = x0; x < x1; x++) {
                int mark = ((x + y) & 1) == 0 ? 30 : 210;
                int original = output[y * n + x];
                output[y * n + x] = (byte)Math.Clamp((int)Math.Round(original * 0.35 + mark * 0.65), 0, 255);
            }
        }
        return output;
    }
}
