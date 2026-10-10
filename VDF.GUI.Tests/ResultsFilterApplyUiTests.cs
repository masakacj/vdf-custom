// Regression: both result toolbar filters must stay inert while editing,
// apply the *current* control value on click, and clear via the × action.
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using VDF.Core;
using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;
using VDF.GUI.Views;

namespace VDF.GUI.Tests;

public class ResultsFilterApplyUiTests {
    static DuplicateItemVM Video(Guid group, string path, int similarity) => new() {
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = group,
            Path = path,
            Folder = Path.GetDirectoryName(path) ?? string.Empty,
            SizeLong = 2_000_000,
            Similarity = similarity,
            Duration = TimeSpan.FromMinutes(10),
            FrameSize = "1920x1080",
            FrameSizeInt = 3000,
            BitRateKbs = 8000,
            Fps = 30,
            Format = "h264",
            AudioChannel = "stereo",
            AudioFormat = "aac",
            AudioBitRateKbs = 192,
            AudioSampleRate = 48_000,
            HdrFormat = string.Empty,
        },
    };

    [Fact]
    public async Task Toolbar_PathAndSimilarity_AppliesOnlyWhenClicked_AndClearsCorrectly() {
        using var session = HeadlessUnitTestSession.StartNew(typeof(VDF.GUI.App));
        await session.Dispatch(() => {
            var vm = new MainWindowVM();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            vm.Duplicates.AddRange(new[] {
                Video(first, @"D:\Series\Season 01\ep01.mkv", 98),
                Video(first, @"E:\Mirror\ep01.mkv", 98),
                Video(second, @"D:\Series\Season 02\ep02.mkv", 99),
                Video(second, @"E:\Mirror\ep02.mkv", 99),
            });
            var view = new DuplicateResultsView { DataContext = vm };
            var window = new Window { Width = 1600, Height = 900, Content = view };
            try {
                window.Show();
                vm.RebuildResultsList();
                int GroupCount() => vm.ResultsRows.OfType<ResultsGroupHeader>().Count();
                Assert.Equal(2, GroupCount());

                var path = view.FindControl<TextBox>("ResultsPathFilterInput")!;
                var applyPath = view.FindControl<Button>("ResultsPathApplyButton")!;
                var clearPath = view.FindControl<Button>("ResultsPathClearButton")!;
                path.Text = "Season 01";
                Assert.Equal(string.Empty, vm.FilterByPath);
                Assert.Equal(2, GroupCount()); // editing is inert
                applyPath.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, applyPath));
                Assert.Equal("Season 01", vm.FilterByPath);
                Assert.Equal(1, GroupCount());
                path.Text = "Season 02";
                Assert.Equal(1, GroupCount()); // still using committed Season 01
                clearPath.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, clearPath));
                Assert.Equal(string.Empty, vm.FilterByPath);
                Assert.Equal(string.Empty, path.Text);
                Assert.Equal(2, GroupCount());

                var from = view.FindControl<NumericUpDown>("ResultsSimilarityFromInput")!;
                var to = view.FindControl<NumericUpDown>("ResultsSimilarityToInput")!;
                var applySimilarity = view.FindControl<Button>("ResultsSimilarityApplyButton")!;
                var clearSimilarity = view.FindControl<Button>("ResultsSimilarityClearButton")!;
                from.Value = 97m;
                to.Value = 98m;
                Assert.Equal(0, vm.FilterSimilarityFrom);
                Assert.Equal(100, vm.FilterSimilarityTo);
                Assert.Equal(2, GroupCount()); // editing is inert
                applySimilarity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, applySimilarity));
                Assert.Equal(97, vm.FilterSimilarityFrom);
                Assert.Equal(98, vm.FilterSimilarityTo);
                Assert.Equal(1, GroupCount());
                clearSimilarity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, clearSimilarity));
                Assert.Equal(0, vm.FilterSimilarityFrom);
                Assert.Equal(100, vm.FilterSimilarityTo);
                Assert.Equal(0m, from.Value);
                Assert.Equal(100m, to.Value);
                Assert.Equal(2, GroupCount());
            }
            finally {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
