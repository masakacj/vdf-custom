// "Same-folder duplicates" must act on the displayed groups and their BEST badges.
using Avalonia.Controls;
using Avalonia.Headless;
using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI.Tests;

[Collection("ResultsContextUiGlobalState")]
public class SameFolderAutoSelectionTests {
    static DuplicateItemVM Item(Guid id, string folder, string file, decimal bitrate = 8000) => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = id,
            Path = Path.Combine(folder, file),
            Folder = folder,
            Similarity = 99,
            SizeLong = 2_000_000,
            Duration = TimeSpan.FromMinutes(10),
            FrameSize = "1920x1080", FrameSizeInt = 3000,
            BitRateKbs = bitrate, Fps = 30, Format = "h264",
            AudioChannel = "stereo", AudioFormat = "aac",
            AudioBitRateKbs = 192, AudioSampleRate = 48_000,
            HdrFormat = string.Empty,
        },
    };

    static ResultsGroupHeader Group(Guid id, DuplicateItemVM best, params DuplicateItemVM[] items) => new() {
        GroupId = id,
        Rows = items.Select(item => new ResultsItemRow(item) {
            IsBest = ReferenceEquals(item, best),
        }).ToList(),
    };

    [Fact]
    public void Plan_OnlySelectsDistinctSameFolderCopies_AndProtectsBest() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-auto-same-folder");
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
        var outsideBest = Item(a, Path.Combine(root, "elsewhere"), "best.mp4");
        var same1 = Item(a, Path.Combine(root, "folder"), "one.mp4");
        var same2 = Item(a, Path.Combine(root, "folder"), "two.mp4");
        var singleton = Item(a, Path.Combine(root, "another"), "single.mp4");
        var insideBest = Item(b, Path.Combine(root, "same"), "best.jpg");
        var insideOther = Item(b, Path.Combine(root, "same"), "copy.jpg");
        var separated1 = Item(c, Path.Combine(root, "one"), "c1.jpg");
        var separated2 = Item(c, Path.Combine(root, "two"), "c2.jpg");
        var duplicateRow1 = Item(d, Path.Combine(root, "duplicated-record"), "one.jpg");
        var duplicateRow2 = Item(d, Path.Combine(root, "duplicated-record"), "one.jpg");

        var (keepers, marked) = MainWindowVM.PlanSameFolderDuplicatesKeepBest([
            Group(a, outsideBest, outsideBest, same1, same2, singleton),
            Group(b, insideBest, insideBest, insideOther),
            Group(c, separated1, separated1, separated2),
            Group(d, duplicateRow1, duplicateRow1, duplicateRow2),
        ]);

        Assert.Equal(2, keepers.Count);
        Assert.Contains(outsideBest, keepers);
        Assert.Contains(insideBest, keepers);
        Assert.Equal(3, marked.Count);
        Assert.Contains(same1, marked);
        Assert.Contains(same2, marked);
        Assert.Contains(insideOther, marked);
        foreach (var untouched in new[] { outsideBest, insideBest, singleton, separated1, separated2, duplicateRow1, duplicateRow2 })
            Assert.DoesNotContain(untouched, marked);
    }

    [Fact]
    public async Task AutoSelectMenu_MarksOnlyCurrentMatches_AndOneUndoRestores() {
        using var session = HeadlessUnitTestSession.StartNew(typeof(VDF.GUI.App));
        await session.Dispatch(() => {
            var vm = new MainWindowVM();
            string root = Path.Combine(Path.GetTempPath(), "vdf-auto-same-folder-ui");
            Guid visible = Guid.NewGuid(), hidden = Guid.NewGuid();
            var a = Item(visible, Path.Combine(root, "visible"), "a.jpg", 16000);
            var b = Item(visible, Path.Combine(root, "visible"), "b.jpg", 6000);
            var c = Item(hidden, Path.Combine(root, "hidden"), "c.jpg", 16000);
            var d = Item(hidden, Path.Combine(root, "hidden"), "d.jpg", 6000);
            vm.Duplicates.AddRange([a, b, c, d]);
            var view = new DuplicateResultsView { DataContext = vm };
            var window = new Window { Width = 1500, Height = 900, Content = view };
            try {
                window.Show();
                vm.RebuildResultsList();
                vm.ApplyPathFilter("visible");
                var current = vm.ResultsRows.OfType<ResultsItemRow>()
                    .Where(row => row.Item.ItemInfo.GroupId == visible).ToList();
                Assert.Equal(2, current.Count);
                var best = Assert.Single(current, row => row.IsBest).Item;
                var other = Assert.Single(current, row => !row.IsBest).Item;

                var button = view.FindControl<Button>("AutoSelectButton")!;
                var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
                flyout.ShowAt(button);
                try {
                    var action = Assert.Single(flyout.Items.OfType<MenuItem>(),
                        item => Equals(item.Header, "同夹重复（保留 BEST）"));
                    Assert.NotNull(action.Command);
                    Assert.True(action.IsEnabled);
                    action.Command!.Execute(null);
                    Assert.True(other.Checked);
                    Assert.False(best.Checked);
                    Assert.False(c.Checked);
                    Assert.False(d.Checked);

                    ((System.Windows.Input.ICommand)vm.UndoSelectionCommand).Execute(null);
                    Assert.False(other.Checked);
                }
                finally { flyout.Hide(); }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
