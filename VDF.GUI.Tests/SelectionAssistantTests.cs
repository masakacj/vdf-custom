// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Collections.ObjectModel;
using System.Text.Json;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class SelectionAssistantTests {
    static DuplicateItemVM Video(
        Guid group,
        string folder,
        string fileName,
        int resolution,
        decimal bitrate,
        long size,
        float fps = 30,
        DateTime? created = null,
        TimeSpan? duration = null) {
        string path = Path.Combine(folder, fileName);
        return new DuplicateItemVM {
            IsVisibleInFilter = true,
            ItemInfo = new DuplicateItem {
                GroupId = group,
                Path = path,
                Folder = folder,
                SizeLong = size,
                Similarity = 99,
                Duration = duration ?? TimeSpan.FromMinutes(10),
                FrameSize = resolution >= 3_000 ? "3840x2160" : "1920x1080",
                FrameSizeInt = resolution,
                BitRateKbs = bitrate,
                Fps = fps,
                DateCreated = created ?? new DateTime(2025, 1, 1),
                Format = "h264",
                AudioChannel = "stereo",
                AudioFormat = "aac",
                AudioBitRateKbs = 192,
                AudioSampleRate = 48_000,
                HdrFormat = string.Empty,
            }
        };
    }

    static SelectionAssistantData Data(
        SelectionAssistantMode mode,
        params SelectionAssistantRuleData[] rules) => new() {
        Mode = mode,
        Rules = new ObservableCollection<SelectionAssistantRuleData>(rules),
    };

    static SelectionAssistantRuleData Rule(SelectionAssistantRuleKind kind, string value = "") => new() {
        Kind = kind,
        Value = value,
        Enabled = true,
    };

    [Fact]
    public void KeepPathRuleAboveNonBest_CanProtectMasterEvenWhenTempCopyIsHigherQuality() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-path-first");
        var master = Video(group, Path.Combine(root, "Master"), "episode.mkv", 2_000, 8_000, 4_000_000);
        var temp4k = Video(group, Path.Combine(root, "temp"), "episode-4k.mkv", 4_000, 20_000, 10_000_000);
        var data = Data(SelectionAssistantMode.AllButOne,
            Rule(SelectionAssistantRuleKind.KeepPathContaining, "Master"),
            Rule(SelectionAssistantRuleKind.NonBest));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(
            new[] { temp4k, master }, _ => true, data,
            new[] { "Resolution", "Bitrate", "FPS", "Size" });

        Assert.Single(plan.Keepers);
        Assert.Same(master, plan.Keepers[0]);
        Assert.Single(plan.ToCheck);
        Assert.Same(temp4k, plan.ToCheck[0]);
    }

    [Fact]
    public void RuleOrderIsLexicographic_NonBestAbovePathKeepsQualityWinner() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-best-first");
        var master = Video(group, Path.Combine(root, "Master"), "episode.mkv", 2_000, 8_000, 4_000_000);
        var temp4k = Video(group, Path.Combine(root, "temp"), "episode-4k.mkv", 4_000, 20_000, 10_000_000);
        var data = Data(SelectionAssistantMode.AllButOne,
            Rule(SelectionAssistantRuleKind.NonBest),
            Rule(SelectionAssistantRuleKind.KeepPathContaining, "Master"));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(
            new[] { master, temp4k }, _ => true, data,
            new[] { "Resolution", "Bitrate", "FPS", "Size" });

        Assert.Same(temp4k, Assert.Single(plan.Keepers));
        Assert.Same(master, Assert.Single(plan.ToCheck));
    }

    [Fact]
    public void AllButOneMode_UsesDeterministicPathTieBreakWhenRulesCannotDifferentiate() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-tie");
        var c = Video(group, root, "c.mkv", 2_000, 8_000, 1_000_000);
        var a = Video(group, root, "a.mkv", 2_000, 8_000, 1_000_000);
        var b = Video(group, root, "b.mkv", 2_000, 8_000, 1_000_000);
        var data = Data(SelectionAssistantMode.AllButOne,
            Rule(SelectionAssistantRuleKind.SmallerFile));

        SelectionAssistantPlan forward = MainWindowVM.ComputeSelectionAssistant(new[] { c, a, b }, _ => true, data);
        SelectionAssistantPlan reversed = MainWindowVM.ComputeSelectionAssistant(new[] { b, a, c }, _ => true, data);

        Assert.Same(a, Assert.Single(forward.Keepers));
        Assert.Same(a, Assert.Single(reversed.Keepers));
        Assert.Equal(2, forward.ToCheck.Count);
        Assert.Equal(2, forward.TieBreakSelections);
        Assert.Equal(1, forward.TiedGroups);
    }

    [Fact]
    public void RulesOnlyMode_LeavesRuleTiesUnchecked() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-rules-only");
        var a = Video(group, root, "a.mkv", 2_000, 8_000, 1_000_000);
        var b = Video(group, root, "b.mkv", 2_000, 8_000, 1_000_000);
        var data = Data(SelectionAssistantMode.RulesOnly,
            Rule(SelectionAssistantRuleKind.LowerResolution),
            Rule(SelectionAssistantRuleKind.LowerBitrate));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(new[] { b, a }, _ => true, data);

        Assert.Single(plan.Keepers);
        Assert.Empty(plan.ToCheck);
        Assert.Equal(0, plan.GroupsWithMarks);
        Assert.Equal(0, plan.TieBreakSelections);
    }

    [Fact]
    public void RulesOnlyMode_SelectsOnlyCandidatesThatAreWorseThanKeeper() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-conservative");
        var best = Video(group, root, "best.mkv", 4_000, 20_000, 10_000_000);
        var low = Video(group, root, "low.mkv", 2_000, 6_000, 3_000_000);
        var tiedWithBest = Video(group, root, "best-copy.mkv", 4_000, 20_000, 10_000_000);
        var data = Data(SelectionAssistantMode.RulesOnly,
            Rule(SelectionAssistantRuleKind.LowerResolution),
            Rule(SelectionAssistantRuleKind.LowerBitrate),
            Rule(SelectionAssistantRuleKind.SmallerFile));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(
            new[] { tiedWithBest, low, best }, _ => true, data);

        Assert.Same(tiedWithBest, Assert.Single(plan.Keepers)); // canonical path: best-copy < best
        Assert.Single(plan.ToCheck);
        Assert.Same(low, plan.ToCheck[0]);
        Assert.DoesNotContain(best, plan.ToCheck);
    }

    [Fact]
    public void EligibilityFilter_RecomputesBestFromEligibleMembersOnly() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-visible-best");
        var hiddenBest = Video(group, Path.Combine(root, "hidden"), "master.mkv", 5_000, 30_000, 20_000_000);
        var visibleBest = Video(group, Path.Combine(root, "visible"), "best.mkv", 3_000, 12_000, 8_000_000);
        var visibleLow = Video(group, Path.Combine(root, "visible"), "low.mkv", 2_000, 6_000, 4_000_000);
        var data = Data(SelectionAssistantMode.AllButOne,
            Rule(SelectionAssistantRuleKind.NonBest));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(
            new[] { hiddenBest, visibleBest, visibleLow },
            item => !ReferenceEquals(item, hiddenBest),
            data,
            new[] { "Resolution", "Bitrate", "FPS" });

        Assert.Same(visibleBest, Assert.Single(plan.Keepers));
        Assert.Same(visibleLow, Assert.Single(plan.ToCheck));
        Assert.DoesNotContain(hiddenBest, plan.TouchedItems);
    }

    [Fact]
    public void DeleteKeywordRule_IsCaseInsensitiveAndSupportsMultipleSeparators() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-keywords");
        var library = Video(group, Path.Combine(root, "Library"), "movie.mkv", 2_000, 8_000, 5_000_000);
        var temp = Video(group, Path.Combine(root, "TEMP"), "movie.mkv", 2_000, 8_000, 5_000_000);
        var data = Data(SelectionAssistantMode.AllButOne,
            Rule(SelectionAssistantRuleKind.DeletePathContaining, "cache，temp;backup"));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(new[] { temp, library }, _ => true, data);

        Assert.Same(library, Assert.Single(plan.Keepers));
        Assert.Same(temp, Assert.Single(plan.ToCheck));
        string[] parsed = MainWindowVM.ParseSelectionAssistantKeywords("copy, low；预览\n临时，archive;copy");
        Assert.Equal(5, parsed.Length);
    }

    [Fact]
    public void BlankKeywordRule_IsIgnoredAndDoesNotTriggerFallbackSelectionByItself() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-empty-rule");
        var a = Video(group, root, "a.mkv", 2_000, 8_000, 1_000_000);
        var b = Video(group, root, "b.mkv", 2_000, 8_000, 1_000_000);
        var data = Data(SelectionAssistantMode.AllButOne,
            Rule(SelectionAssistantRuleKind.DeletePathContaining, "   \r\n ; "));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(new[] { a, b }, _ => true, data);

        Assert.Equal(0, plan.ActiveRules);
        Assert.Empty(plan.TouchedItems);
        Assert.Empty(plan.ToCheck);
    }

    [Fact]
    public void PreserveExistingSelection_StillForcesOneUncheckedKeeperPerProcessedGroup() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-preserve-safety");
        var large = Video(group, root, "large.mkv", 2_000, 8_000, 9_000_000);
        var small = Video(group, root, "small.mkv", 2_000, 8_000, 1_000_000);
        large.Checked = true;
        small.Checked = true;
        var data = Data(SelectionAssistantMode.AllButOne,
            Rule(SelectionAssistantRuleKind.SmallerFile));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(new[] { large, small }, _ => true, data);
        MainWindowVM.ApplySelectionAssistantPlan(plan, preserveExistingSelection: true);

        Assert.False(large.Checked);
        Assert.True(small.Checked);
        Assert.False(new[] { large, small }.All(item => item.Checked));
    }

    [Fact]
    public void ReplaceSelection_ClearsTouchedRuleTiesInConservativeMode() {
        var group = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "vdf-selection-assistant-replace");
        var a = Video(group, root, "a-best.mkv", 4_000, 20_000, 10_000_000);
        var b = Video(group, root, "b-best-copy.mkv", 4_000, 20_000, 10_000_000);
        var low = Video(group, root, "low.mkv", 2_000, 6_000, 3_000_000);
        a.Checked = true;
        b.Checked = true;
        low.Checked = false;
        var data = Data(SelectionAssistantMode.RulesOnly,
            Rule(SelectionAssistantRuleKind.LowerResolution),
            Rule(SelectionAssistantRuleKind.LowerBitrate),
            Rule(SelectionAssistantRuleKind.SmallerFile));

        SelectionAssistantPlan plan = MainWindowVM.ComputeSelectionAssistant(new[] { a, b, low }, _ => true, data);
        MainWindowVM.ApplySelectionAssistantPlan(plan, preserveExistingSelection: false);

        Assert.False(a.Checked);
        Assert.False(b.Checked);
        Assert.True(low.Checked);
        Assert.Equal(1, new[] { a, b, low }.Count(item => item.Checked));
    }

    [Fact]
    public void SettingsJson_RoundTripsSelectionAssistantRuleOrderAndValues() {
        var settings = new SettingsFile {
            SelectionAssistant = Data(SelectionAssistantMode.RulesOnly,
                Rule(SelectionAssistantRuleKind.KeepPathContaining, @"Y:\Master"),
                Rule(SelectionAssistantRuleKind.NonBest),
                Rule(SelectionAssistantRuleKind.LowerResolution)),
        };
        settings.SelectionAssistant.CurrentFilterOnly = false;
        settings.SelectionAssistant.PreserveExistingSelection = true;

        string json = JsonSerializer.Serialize(settings, GuiJsonContext.Default.SettingsFile);
        SettingsFile copy = JsonSerializer.Deserialize(json, GuiJsonContext.Default.SettingsFile)!;

        Assert.Equal(SelectionAssistantMode.RulesOnly, copy.SelectionAssistant.Mode);
        Assert.False(copy.SelectionAssistant.CurrentFilterOnly);
        Assert.True(copy.SelectionAssistant.PreserveExistingSelection);
        Assert.Equal(3, copy.SelectionAssistant.Rules.Count);
        Assert.Equal(SelectionAssistantRuleKind.KeepPathContaining, copy.SelectionAssistant.Rules[0].Kind);
        Assert.Equal(@"Y:\Master", copy.SelectionAssistant.Rules[0].Value);
        Assert.Equal(SelectionAssistantRuleKind.NonBest, copy.SelectionAssistant.Rules[1].Kind);
        Assert.Equal(SelectionAssistantRuleKind.LowerResolution, copy.SelectionAssistant.Rules[2].Kind);
    }
}
