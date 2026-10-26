using VDF.GUI.Data;

namespace VDF.GUI.Tests;

public class GroupQualityOnDemandTests {
    static byte[] Textured(int shift = 0, int amplitude = 40) {
        var frame = new byte[GroupQualityOnDemand.Side * GroupQualityOnDemand.Side];
        for (int y = 0; y < GroupQualityOnDemand.Side; y++)
            for (int x = 0; x < GroupQualityOnDemand.Side; x++)
                frame[y * GroupQualityOnDemand.Side + x] =
                    (byte)(110 + (((x * 19 + y * 13) % 17) - 8) * amplitude / 12 + shift);
        return frame;
    }

    static OnDemandVideoSamples Sample(string name, long bytes, byte[] frame,
        int width = 1920, int height = 1080) =>
        new(name, bytes, width, height,
            Enumerable.Range(0, GroupQualityOnDemand.FramesPerVideo)
                .Select(_ => frame.ToArray()).ToArray());

    [Fact]
    public void SameFramesButMuchLargerFile_IsFlaggedAsInefficientEncode() {
        byte[] frame = Textured();
        var report = GroupQualityOnDemand.Analyze([
            Sample("small", 100_000_000, frame),
            Sample("bloated", 250_000_000, frame)
        ]);
        Assert.Equal(2, report.DecodedFiles);
        Assert.Equal(1, report.ComparedPairs);
        var suspect = Assert.Single(report.Findings);
        Assert.Equal("bloated", suspect.Path);
        Assert.Equal(1, suspect.Penalty);
        Assert.True(suspect.Similarity > 0.99);
        Assert.Contains("体积", suspect.Reason);
    }

    [Fact]
    public void BiggerButOnlyMildSizeDifference_IsNotCalledAWorseCopy() {
        var frame = Textured();
        var report = GroupQualityOnDemand.Analyze([
            Sample("small", 100_000_000, frame),
            Sample("slightly-big", 110_000_000, frame)
        ]);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void MismatchedContent_IsNotCalledAnInefficientEncode() {
        var report = GroupQualityOnDemand.Analyze([
            Sample("small", 100_000_000, Textured()),
            Sample("different", 300_000_000, Textured(70))
        ]);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void HigherResolutionWithSameReducedSamples_IsNotAutoPenalized() {
        var frame = Textured();
        var report = GroupQualityOnDemand.Analyze([
            Sample("1080p", 100_000_000, frame),
            Sample("4k", 600_000_000, frame, 3840, 2160)
        ]);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void IdenticalFramesSsimIsUnity_AndCorruptDimensionsAreRejected() {
        var frame = Textured();
        Assert.InRange(GroupQualityOnDemand.StructuralSimilarity(frame, frame), 0.999999, 1);
        Assert.Throws<ArgumentException>(() =>
            GroupQualityOnDemand.StructuralSimilarity(frame, [2, 3]));
    }

    [Fact]
    public void IncompleteFrames_AreNotTreatedAsProofOfEquality() {
        var sample = Sample("good", 100, Textured());
        var incomplete = new OnDemandVideoSamples("bad", 1000, 1920, 1080, [
            Textured(), Textured()
        ]);
        var report = GroupQualityOnDemand.Analyze([sample, incomplete]);
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.ComparedPairs);
    }
    [Fact]
    public void VideoOnlyEvidence_DoesNotOverrideBetterAudioFpsOrHdr() {
        VDF.GUI.ViewModels.DuplicateItemVM Item(float fps, decimal audioKbps, string hdr) => new() {
            ItemInfo = new VDF.Core.ViewModels.DuplicateItem {
                Fps = fps, AudioBitRateKbs = audioKbps, HdrFormat = hdr,
                AudioFormat = "aac", AudioChannel = "stereo", AudioSampleRate = 48000
            }
        };
        var baseVideo = Item(30, 192, "");
        var comparable = Item(29.97f, 190, "");
        Assert.True(VDF.GUI.ViewModels.MainWindowVM.HasEquivalentTrackMetadata([baseVideo, comparable]));
        Assert.False(VDF.GUI.ViewModels.MainWindowVM.HasEquivalentTrackMetadata([
            baseVideo, Item(60, 192, "")
        ]));
        Assert.False(VDF.GUI.ViewModels.MainWindowVM.HasEquivalentTrackMetadata([
            baseVideo, Item(30, 320, "")
        ]));
        Assert.False(VDF.GUI.ViewModels.MainWindowVM.HasEquivalentTrackMetadata([
            baseVideo, Item(30, 192, "HDR10")
        ]));
    }

}
