using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class UnifiedExclusionManagerTests {
    [Fact]
    public void OriginalScanFolders_UseSameSettingsCollection() {
        var scanFolders = new System.Collections.ObjectModel.ObservableCollection<string> {
            Path.Combine(Path.GetTempPath(), "existing-scan-exclusion")
        };
        var manager = new BlacklistManagerVM(new(), () => Task.CompletedTask,
            scanDirectories: scanFolders);
        Assert.Same(scanFolders, manager.ScanDirectories);
        manager.ScanDirectoryInput = Path.Combine(Path.GetTempPath(), "new-scan-exclusion");
        ((System.Windows.Input.ICommand)manager.AddScanDirectoryCommand).Execute(null);
        Assert.Equal(2, scanFolders.Count);
    }

    [Fact]
    public void ScanDirectoryChanges_PersistImmediately_AndRollbackIfSaveFails() {
        var folders = new System.Collections.ObjectModel.ObservableCollection<string>();
        string folder = Path.Combine(Path.GetTempPath(), "vdf-scan-exclusion-test");
        int saves = 0;
        var manager = new BlacklistManagerVM(new(), () => Task.CompletedTask,
            scanDirectories: folders, scanDirectoriesChanged: () => saves++);
        manager.ScanDirectoryInput = folder;
        ((System.Windows.Input.ICommand)manager.AddScanDirectoryCommand).Execute(null);
        Assert.Equal(folder, Assert.Single(folders));
        Assert.Equal(1, saves);
        ((System.Windows.Input.ICommand)manager.RemoveScanDirectoriesCommand)
            .Execute(new System.Collections.ArrayList { folder });
        Assert.Empty(folders);
        Assert.Equal(2, saves);

        var failed = new BlacklistManagerVM(new(), () => Task.CompletedTask,
            scanDirectories: folders, scanDirectoriesChanged: () => throw new IOException("disk unavailable"));
        failed.ScanDirectoryInput = folder;
        ((System.Windows.Input.ICommand)failed.AddScanDirectoryCommand).Execute(null);
        Assert.Empty(folders);
        Assert.Contains("保存扫描目录排除失败", failed.StatusMessage);
        folders.Add(folder);
        ((System.Windows.Input.ICommand)failed.RemoveScanDirectoriesCommand)
            .Execute(new System.Collections.ArrayList { folder });
        Assert.Equal(folder, Assert.Single(folders));
        Assert.Contains("保存扫描目录排除失败", failed.StatusMessage);
    }

    [Fact]
    public void SingleFileSearch_IsCaseInsensitive_AndBoundedByPageSize() {
        var history = Enumerable.Range(0, 1000)
            .Select(i => $@"Y:\Media\VIDEO_{i:D5}.jpg").ToArray();
        var initial = BlacklistManagerVM.PageOf(history, "", x => x, 0, 200);
        Assert.Equal(200, initial.Rows.Count);
        Assert.True(initial.HasNext);
        Assert.Same(history[0], initial.Rows[0]);

        var selected = BlacklistManagerVM.PageOf(history, "video_0099", x => x, 0, 200);
        Assert.Equal(10, selected.Rows.Count);
        Assert.All(selected.Rows, path => Assert.Contains("VIDEO_0099", path));
        Assert.Empty(BlacklistManagerVM.PageOf(history, "not-in-history", x => x, 0, 200).Rows);
        Assert.Equal(1000, history.Length);
    }
}
