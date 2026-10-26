using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI.Tests;

// The real virtualized group header must expose working commands (not only VM methods).
[Collection("ResultsContextUiGlobalState")]
public class GroupHeaderActionsUiTests {
    [Fact]
    public async Task RenderedGroupHeader_ExposesQualityAllInvert_AndUndoAffectsOnlyItsRows() {
        using var session = HeadlessUnitTestSession.StartNew(typeof(VDF.GUI.App));
        await session.Dispatch(() => {
            var vm = new MainWindowVM();
            Guid id = Guid.NewGuid(), unrelatedId = Guid.NewGuid();
            string root = Path.Combine(Path.GetTempPath(), "group-header-actions-ui");
            DuplicateItemVM Make(Guid groupId, string name) => new() {
                IsVisibleInFilter = true,
                ItemInfo = new DuplicateItem {
                    GroupId = groupId, Path = Path.Combine(root, name),
                    Folder = root, SizeLong = 1_000_000,
                    Similarity = 99, Duration = TimeSpan.FromMinutes(1),
                    FrameSize = "1920x1080", FrameSizeInt = 3000,
                    BitRateKbs = 5_000, Fps = 30, Format = "h264",
                }
            };
            var a = Make(id, "a.mp4");
            var b = Make(id, "b.mp4");
            var other = Make(unrelatedId, "elsewhere.mp4");
            vm.Duplicates.AddRange([a, b, other]);
            var view = new DuplicateResultsView { DataContext = vm };
            var window = new Window { Width = 1500, Height = 900, Content = view };
            try {
                window.Show();
                vm.RebuildResultsList();
                window.UpdateLayout();
                var list = view.FindControl<ListBox>("ResultsList")!;
                var buttons = list.GetVisualDescendants().OfType<Button>().ToList();
                Button Find(string label) =>
                    Assert.Single(buttons, button => Equals(button.Content, label));
                var quality = Find("检测画质");
                var selectAll = Find("全选");
                var invert = Find("反选");
                Assert.NotNull(quality.Command);
                Assert.NotNull(selectAll.Command);
                Assert.NotNull(invert.Command);
                Assert.IsType<ResultsGroupHeader>(selectAll.CommandParameter);
                Assert.Same(selectAll.CommandParameter, invert.CommandParameter);

                selectAll.Command!.Execute(selectAll.CommandParameter);
                Assert.True(a.Checked);
                Assert.True(b.Checked);
                Assert.False(other.Checked);
                ((System.Windows.Input.ICommand)vm.UndoSelectionCommand).Execute(null);
                Assert.False(a.Checked);
                Assert.False(b.Checked);

                b.Checked = true;
                invert.Command!.Execute(invert.CommandParameter);
                Assert.True(a.Checked);
                Assert.False(b.Checked);
                Assert.False(other.Checked);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
