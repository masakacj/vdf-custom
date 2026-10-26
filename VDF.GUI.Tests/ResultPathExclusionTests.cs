using System.Text.Json;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class ResultPathExclusionTests {
    [Fact]
    public void Normalize_RequiresAbsoluteNonRootFolder_AndRejectsWildcards() {
        Assert.True(ResultPathExclusion.TryNormalize(@"Y:\pipa\pipa2\..\Videos\", out string path, out _));
        Assert.Equal(@"Y:\pipa\Videos", path);
        Assert.False(ResultPathExclusion.TryNormalize(@"Y:\", out _, out _));
        Assert.False(ResultPathExclusion.TryNormalize(@"pipa\Videos", out _, out _));
        Assert.False(ResultPathExclusion.TryNormalize(@"Y:\pipa\*", out _, out _));
        Assert.False(ResultPathExclusion.TryNormalize("  ", out _, out _));
    }

    [Fact]
    public void Matches_DirectAndNestedFiles_NotSiblingPrefixes_OrOtherDrives() {
        var folders = new[] { @"Y:\pipa\pipa2\抖音-鹅鹅 [1022P419V]" };
        Assert.True(ResultPathExclusion.Matches(folders[0] + @"\a.jpg", folders));
        Assert.True(ResultPathExclusion.Matches(folders[0].ToLowerInvariant() + @"\sub\file.jpg", folders));
        Assert.True(ResultPathExclusion.Matches(folders[0].Replace('\\', '/') + "/sub/file.jpg", folders));
        Assert.False(ResultPathExclusion.Matches(folders[0] + @"备份\a.jpg", folders));
        Assert.False(ResultPathExclusion.Matches(@"Z:\Backup\a.jpg", folders));
        Assert.False(ResultPathExclusion.Matches(folders[0], folders));
        Assert.False(ResultPathExclusion.Matches(@"Y:\pipa\a.jpg", Array.Empty<string>()));
    }

    [Fact]
    public void RemovingFolderMembers_RemovesSingletonDuplicateGroupsWithoutChangingInputs() {
        Guid groupA = Guid.NewGuid(), groupB = Guid.NewGuid();
        var folder = @"Y:\pipa\exclude";
        var retained = Item(groupA, @"Z:\mirror\copy.jpg");
        var excluded = Item(groupA, folder + @"\original.jpg");
        var shown1 = Item(groupB, @"X:\clips\first.jpg");
        var shown2 = Item(groupB, @"Z:\clips\second.jpg");
        var inputs = new[] { retained, excluded, shown1, shown2 };
        var result = ResultsListBuilder.Build(new ResultsBuildRequest {
            Items = inputs,
            Filter = item => !ResultPathExclusion.Matches(item.ItemInfo.Path, [folder]),
            IsTombstone = _ => false,
            IsOffline = _ => false,
        });
        Assert.Single(result.Groups);
        Assert.Equal(groupB, result.Groups[0].GroupId);
        Assert.Equal(4, inputs.Length);
        Assert.Contains(excluded, inputs);
        Assert.Equal(2, result.Groups[0].Rows.Count);
    }

    [Fact]
    public void PersistedRule_RoundTripsSkipScanWithoutChangingLegacyScanBlacklist() {
        var settings = new SettingsFile();
        settings.Blacklists.Add(@"X:\existing-blacklist");
        settings.ResultPathExclusions.Add(new ResultPathExclusion {
            Path = @"Y:\pipa\hide-only", SkipScan = false,
        });
        settings.ResultPathExclusions.Add(new ResultPathExclusion {
            Path = @"Y:\pipa\skip-scan", SkipScan = true,
        });
        string json = JsonSerializer.Serialize(settings, GuiJsonContext.Default.SettingsFile);
        var roundTrip = JsonSerializer.Deserialize(json, GuiJsonContext.Default.SettingsFile)!;
        Assert.Equal(@"X:\existing-blacklist", Assert.Single(roundTrip.Blacklists));
        Assert.Equal(2, roundTrip.ResultPathExclusions.Count);
        Assert.False(roundTrip.ResultPathExclusions[0].SkipScan);
        Assert.True(roundTrip.ResultPathExclusions[1].SkipScan);
    }

    static DuplicateItemVM Item(Guid group, string path) => new() {
        ItemInfo = new DuplicateItem {
            Path = path,
            Folder = Path.GetDirectoryName(path) ?? "",
            GroupId = group,
            SizeLong = 1024,
            Similarity = 99,
        },
    };
}
