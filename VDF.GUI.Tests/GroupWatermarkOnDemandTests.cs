using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public sealed class GroupWatermarkOnDemandTests {
    const int Side = GroupWatermarkOnDemand.Side;
    const int Frames = GroupWatermarkOnDemand.FramesPerVideo;
    const int Bytes = GroupWatermarkOnDemand.FrameBytes;

    // Dynamic background with content motion so a stationary logo can be distinguished
    // from a scene feature or a transient compression artifact. No real media needed.
    static IReadOnlyList<byte[]> Background(int phase = 0) {
        var frames = new List<byte[]>(Frames);
        for (int f = 0; f < Frames; f++) {
            var image = new byte[Bytes];
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++) {
                    double wave = 28 * Math.Sin((x + 13 * f + phase) * 0.17) +
                                  23 * Math.Cos((y + 17 * f + phase) * 0.13);
                    int texture = ((x * 19 + y * 11 + f * 31) % 43) - 21;
                    int value = Math.Clamp((int)Math.Round(108 + wave + texture * 0.6), 0, 255);
                    int index = (y * Side + x) * 3;
                    image[index] = (byte)value;
                    image[index + 1] = (byte)Math.Clamp(value - 7 + x / 36, 0, 255);
                    image[index + 2] = (byte)Math.Clamp(value + y / 42, 0, 255);
                }
            frames.Add(image);
        }
        return frames;
    }

    static IReadOnlyList<byte[]> AddFixedLogo(IReadOnlyList<byte[]> input,
        int x0, int y0, int width = 52, int height = 25, double opacity = 0.72) {
        var frames = input.Select(f => f.ToArray()).ToArray();
        foreach (var frame in frames) {
            for (int y = y0; y < y0 + height; y++)
                for (int x = x0; x < x0 + width; x++) {
                    // Blocky word-like strokes, not one full-screen flat rectangle.
                    if (((x - x0) / 4 + (y - y0) / 5) % 4 == 0) continue;
                    int index = (y * Side + x) * 3;
                    for (int c = 0; c < 3; c++)
                        frame[index + c] = (byte)Math.Clamp(
                            (int)Math.Round(frame[index + c] * (1 - opacity) +
                                (c == 2 ? 230 : 238) * opacity), 0, 255);
                }
        }
        return frames;
    }

    static WatermarkVideoSamples Sample(string name, IReadOnlyList<byte[]> frames,
        int width = 1920, int height = 1080) => new(name, width, height, frames);

    [Fact]
    public void TopRightFixedLogo_IsAttributedToOnlyMarkedCopy() {
        var clean = Background();
        var marked = AddFixedLogo(clean, 248, 24);
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("original.mp4", clean), Sample("watermarked.mp4", marked)
        ]);
        Assert.Equal(2, report.DecodedFiles);
        Assert.Equal(1, report.ComparedPairs);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("watermarked.mp4", finding.Path);
        Assert.InRange(finding.RegionCount, 1, 4);
        Assert.Contains(finding.Regions, r => r.Position == "右上");
        Assert.True(finding.Penalty > 0);
        Assert.DoesNotContain("无水印", report.Summary);
    }

    [Fact]
    public void ThreeVersions_IdentifiesWhichHasMoreDistinctExtraOverlays() {
        var clean = Background();
        var single = AddFixedLogo(clean, 245, 20);
        var doubleMarked = AddFixedLogo(single, 18, 256);
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("none.mp4", clean), Sample("one.mp4", single), Sample("two.mp4", doubleMarked)
        ]);
        Assert.True(report.ComparedPairs >= 2);
        Assert.DoesNotContain(report.Findings, f => f.Path == "none.mp4");
        WatermarkFinding one = Assert.Single(report.Findings, f => f.Path == "one.mp4");
        WatermarkFinding two = Assert.Single(report.Findings, f => f.Path == "two.mp4");
        Assert.True(two.RegionCount > one.RegionCount,
            $"one={one.RegionCount}; two={two.RegionCount}");
        Assert.Contains(two.Regions, r => r.Position == "左下");
        Assert.Contains(two.Regions, r => r.Position == "右上");
    }

    [Fact]
    public void SameWatermarkInBothVersions_IsNotEvidenceOfAnExtraWatermark() {
        var logo = AddFixedLogo(Background(), 246, 23);
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("a.mp4", logo), Sample("b.mp4", logo.Select(f => f.ToArray()).ToArray())
        ]);
        Assert.Equal(1, report.ComparedPairs);
        Assert.Empty(report.Findings);
        Assert.Contains("不能据此证明", report.Summary);
    }

    [Fact]
    public void FullFrameColorCorrection_IsNotMisclassifiedAsWatermark() {
        var clean = Background();
        var graded = clean.Select(f => f.Select(c => (byte)Math.Min(255, c + 10)).ToArray()).ToArray();
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("original", clean), Sample("graded", graded)
        ]);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void DifferentSceneOrBadAlignment_IsSkippedRatherThanClaimedAsLogo() {
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("a", Background()), Sample("b", Background(113))
        ]);
        Assert.Empty(report.Findings);
        Assert.True(report.SkippedPairs > 0 || report.ComparedPairs == 0);
    }

    [Fact]
    public void MovingSmallOverlay_IsNotAStationaryWatermark() {
        var clean = Background();
        var moving = clean.Select((frame, index) => {
            var temp = AddFixedLogo([frame], 20 + index * 30, 24, 20, 15);
            return temp[0];
        }).ToArray();
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("clean", clean), Sample("moving", moving)
        ]);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void AspectRatioDifference_IsUncomparable() {
        var frames = Background();
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("landscape", frames), Sample("portrait", frames, 1080, 1920)
        ]);
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.ComparedPairs);
        Assert.Equal(1, report.SkippedPairs);
    }

    [Fact]
    public void IncompleteOrCorruptFrames_CannotYieldFalseWatermarkEvidence() {
        var frames = Background();
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("good", frames), Sample("bad", [frames[0]])
        ]);
        Assert.Equal(1, report.DecodedFiles);
        Assert.Equal(0, report.ComparedPairs);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void RegionClustering_CountsSeparatedOverlays_AndDropsOneCellNoise() {
        var votes = new int[GroupWatermarkOnDemand.CellCount];
        int t = GroupWatermarkOnDemand.TilesPerSide;
        votes[1 * t + 1] = 2;
        votes[1 * t + 2] = 1;
        votes[16 * t + 16] = 3;
        votes[17 * t + 16] = 2;
        votes[10 * t + 10] = 1; // isolated transient artifact
        var regions = GroupWatermarkOnDemand.ClusterRegions(votes);
        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, r => r.Position == "左上");
        Assert.Contains(regions, r => r.Position == "右下");
    }

    [Fact]
    public void WatermarkComparisonDoesNotUseFileSizeAsDetectionCriterion() {
        var clean = Background();
        var marked = AddFixedLogo(clean, 242, 16);
        var report = GroupWatermarkOnDemand.Analyze([
            Sample("bigger-original", clean), Sample("smaller-marked", marked)
        ]);
        Assert.Equal("smaller-marked", Assert.Single(report.Findings).Path);
    }

    [Fact]
    public void BestRerankRequiresMatchingFrameRateAudioAndHdr() {
        DuplicateItemVM Item(float fps, decimal audioKbps, string hdr) => new() {
            ItemInfo = new VDF.Core.ViewModels.DuplicateItem {
                Fps = fps, AudioBitRateKbs = audioKbps, HdrFormat = hdr,
                AudioFormat = "aac", AudioChannel = "stereo", AudioSampleRate = 48000,
            }
        };
        var baseline = Item(30, 192, "");
        Assert.True(MainWindowVM.HasComparableWatermarkBestMetadata([
            baseline, Item(29.97f, 190, "")
        ]));
        Assert.False(MainWindowVM.HasComparableWatermarkBestMetadata([
            baseline, Item(60, 192, "")
        ]));
        Assert.False(MainWindowVM.HasComparableWatermarkBestMetadata([
            baseline, Item(30, 320, "")
        ]));
        Assert.False(MainWindowVM.HasComparableWatermarkBestMetadata([
            baseline, Item(30, 192, "HDR10")
        ]));
    }
}
