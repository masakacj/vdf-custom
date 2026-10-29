using VDF.GUI.Utils;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public sealed class FolderMergeTransferTests : IDisposable {
    readonly string root = Directory.CreateTempSubdirectory("vdf-foldermerge-tests-").FullName;
    public void Dispose() {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    string Source() {
        string path = Path.Combine(root, "source");
        Directory.CreateDirectory(path);
        return path;
    }
    string Target() {
        string path = Path.Combine(root, "target");
        Directory.CreateDirectory(path);
        return path;
    }
    static void Write(string path, string content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task WholeFolderMerge_CopiesVerifiesThenDeletes_AndPreservesTargetConflicts() {
        string source = Source(), target = Target();
        string oldA = Path.Combine(source, "A", "one.mkv");
        string oldOther = Path.Combine(source, "notes.txt");
        string oldEmpty = Path.Combine(source, "empty", "nested");
        Directory.CreateDirectory(oldEmpty);
        Write(oldA, "new video version");
        Write(oldOther, "metadata and notes");
        Write(Path.Combine(target, "A", "one.mkv"), "existing target video");
        var plan = FolderMergeTransfer.BuildPlan(source, target);
        Assert.Equal(2, plan.Files.Count);
        Assert.Equal(1, plan.CollisionRenames);
        Assert.Equal(1, plan.Files.Count(f => f.Renamed));
        Assert.Contains(plan.RelativeDirectories, path => path.EndsWith(Path.Combine("empty", "nested")));
        var copied = new List<(string OldPath, string NewPath)>();
        bool committed = false;
        var outcome = await FolderMergeTransfer.ExecuteAsync(plan, moves => {
            copied.AddRange(moves);
            Assert.All(moves, pair => {
                Assert.True(File.Exists(pair.OldPath)); // no deletion before DB commit
                Assert.True(File.Exists(pair.NewPath)); // destination installed and verified
            });
            committed = true;
            return (true, "");
        });
        Assert.True(committed);
        Assert.True(outcome.Committed);
        Assert.Equal(2, outcome.RemovedSources);
        Assert.Empty(outcome.RemainingSources);
        Assert.False(Directory.Exists(source));
        Assert.Equal("existing target video", File.ReadAllText(Path.Combine(target, "A", "one.mkv")));
        Assert.Equal("new video version", File.ReadAllText(copied.Single(p => p.OldPath == oldA).NewPath));
        Assert.Equal("metadata and notes", File.ReadAllText(copied.Single(p => p.OldPath == oldOther).NewPath));
        Assert.True(Directory.Exists(Path.Combine(target, "empty", "nested")));
    }

    [Fact]
    public async Task DatabaseCommitFailure_PreservesOriginals_AndRemovesOnlyNewCopies() {
        string source = Source(), target = Target();
        string old = Path.Combine(source, "video.mp4");
        Write(old, "only copy");
        string protectedTarget = Path.Combine(target, "keep.txt");
        Write(protectedTarget, "keep unchanged");
        var plan = FolderMergeTransfer.BuildPlan(source, target);
        var outcome = await FolderMergeTransfer.ExecuteAsync(plan, moves => {
            Assert.True(File.Exists(moves[0].NewPath));
            Assert.True(File.Exists(moves[0].OldPath));
            return (false, "journal cannot write");
        });
        Assert.False(outcome.Committed);
        Assert.True(File.Exists(old));
        Assert.Equal("only copy", File.ReadAllText(old));
        Assert.False(File.Exists(Path.Combine(target, "video.mp4")));
        Assert.Equal("keep unchanged", File.ReadAllText(protectedTarget));
        Assert.Contains(outcome.Errors, e => e.Contains("journal cannot write"));
    }

    [Fact]
    public async Task ModifiedSourceAfterPreview_DoesNotMoveOrDeleteAnything() {
        string source = Source(), target = Target();
        string old = Path.Combine(source, "a.txt");
        Write(old, "initial");
        var plan = FolderMergeTransfer.BuildPlan(source, target);
        Write(old, "altered-content");
        var outcome = await FolderMergeTransfer.ExecuteAsync(plan, _ => (true, ""));
        Assert.False(outcome.Committed);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public void PrefixNestedOrRootDirectory_IsRejected() {
        string source = Source(), target = Target();
        Assert.Throws<InvalidOperationException>(() =>
            FolderMergeTransfer.BuildPlan(source, Path.Combine(source, "subfolder")));
        Assert.Throws<InvalidOperationException>(() =>
            FolderMergeTransfer.BuildPlan(source, root));
        Assert.Throws<InvalidOperationException>(() =>
            FolderMergeTransfer.BuildPlan(source, source));
        string diskRoot = Path.GetPathRoot(root)!;
        Assert.Throws<InvalidOperationException>(() =>
            FolderMergeTransfer.BuildPlan(diskRoot, target));
    }

    [Fact]
    public void SameNameInTarget_IsRenamedRatherThanOverwritten() {
        string source = Source(), target = Target();
        Write(Path.Combine(source, "same.mp4"), "new contents");
        Write(Path.Combine(target, "same.mp4"), "old contents");
        Write(Path.Combine(target, "same (VDF合并1).mp4"), "a different collision");
        var plan = FolderMergeTransfer.BuildPlan(source, target);
        Assert.Equal("same (VDF合并2).mp4", Path.GetFileName(Assert.Single(plan.Files).DestinationPath));
    }

    [Fact]
    public void SourceSubfolderWithSameNameAsTargetFile_IsRejected() {
        string source = Source(), target = Target();
        Write(Path.Combine(source, "Series", "video.mkv"), "source");
        Write(Path.Combine(target, "Series"), "not a directory");
        Assert.Throws<IOException>(() => FolderMergeTransfer.BuildPlan(source, target));
        Assert.True(File.Exists(Path.Combine(source, "Series", "video.mkv")));
    }

    [Fact]
    public void SwapChangesBothFieldsWithoutLosingEditedPaths() {
        var vm = new FolderMergeDialogVM("source", "target");
        vm.SourceFolder = "edited-source";
        ((System.Windows.Input.ICommand)vm.SwapCommand).Execute(null);
        Assert.Equal("target", vm.SourceFolder);
        Assert.Equal("edited-source", vm.TargetFolder);
    }

    [Fact]
    public void VisibleResultsHaveDedicatedTwoCheckedFileEntry() {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Directory.Build.props")))
            dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        string view = File.ReadAllText(Path.Combine(dir!, "VDF.GUI", "Views", "DuplicateResultsView.xaml"));
        Assert.Contains("CheckedFolderMergeButton", view);
        Assert.Contains("Command=\"{Binding MergeCheckedFoldersCommand}\"", view);
        Assert.Contains("IsEnabled=\"{Binding CanMergeCheckedFolders}\"", view);
    }
}
