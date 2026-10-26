using Avalonia.Controls;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class UnifiedExclusionManagerTests {
    [Fact]
    public async Task ManagerView_PresentsAllFourExistingExclusionMechanisms() {
        using var session = Avalonia.Headless.HeadlessUnitTestSession.StartNew(typeof(VDF.GUI.App));
        await session.Dispatch(() => {
            var scanFolders = new System.Collections.ObjectModel.ObservableCollection<string> {
                Path.Combine(Path.GetTempPath(), "existing-scan-exclusion")
            };
            var manager = new BlacklistManagerVM(new(), () => Task.CompletedTask,
                scanDirectories: scanFolders);
            var window = new VDF.GUI.Views.BlacklistManagerView(manager);
            try {
                window.Show();
                window.UpdateLayout();
                var tabs = window.FindControl<Avalonia.Controls.TabControl>("ExclusionTabs")!;
                Assert.NotNull(tabs);
                Assert.Equal(new[] {
                    "结果目录排除", "扫描目录排除（原版）",
                    "单文件排除（原版）", "误判重复组"
                }, tabs.Items.OfType<Avalonia.Controls.TabItem>()
                    .Select(item => item.Header?.ToString()));
                Assert.Same(scanFolders, manager.ScanDirectories);
                manager.ScanDirectoryInput = Path.Combine(Path.GetTempPath(), "new-scan-exclusion");
                ((System.Windows.Input.ICommand)manager.AddScanDirectoryCommand).Execute(null);
                Assert.Equal(2, scanFolders.Count);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public void SingleFileSearch_IsCaseInsensitive_AndCapsDisplayedRows() {
        var history = Enumerable.Range(0, 1000)
            .Select(i => $@"Y:\Media\VIDEO_{i:D5}.jpg").ToArray();
        var initial = BlacklistManagerVM.SearchExcludedPaths(history, string.Empty);
        Assert.Equal(500, initial.Count);
        Assert.Same(history[0], initial[0]);

        var selected = BlacklistManagerVM.SearchExcludedPaths(history, "video_0099");
        Assert.Equal(10, selected.Count);
        Assert.All(selected, s => Assert.Contains("VIDEO_0099", s));
        Assert.Empty(BlacklistManagerVM.SearchExcludedPaths(history, "not-in-history"));
        Assert.Equal(1000, history.Length);
    }
}
