using VDF.Core;

namespace VDF.Core.Tests;

public sealed class FolderMergeDatabaseTests {
    static FileEntry Entry(string path) => new() { Path = path };

    [Fact]
    public void BatchMove_UpdatesOnlyMatchingEntries_AndPreservesMetadata() {
        string folder = Path.Combine(Path.GetTempPath(), "vdf-folder-db-test");
        string oldA = Path.Combine(folder, "source", "a.mp4");
        string oldB = Path.Combine(folder, "source", "b.jpg");
        string newA = Path.Combine(folder, "target", "a.mp4");
        string newB = Path.Combine(folder, "target", "b.jpg");
        var a = Entry(oldA); a.FileSize = 123;
        var b = Entry(oldB); b.FileSize = 456;
        var untouched = Entry(Path.Combine(folder, "other", "c.bin"));
        var entries = new HashSet<FileEntry> { a, b, untouched };
        bool persisted = false;
        bool result = ScanEngine.TryApplyFolderMergeMoves(entries,
            [(oldA, newA), (oldB, newB)],
            moves => {
                Assert.Equal(2, moves.Count);
                Assert.Contains(entries, e => e.Path == newA);
                persisted = true;
                return true;
            }, out string error);
        Assert.True(result, error);
        Assert.True(persisted);
        Assert.Equal(3, entries.Count);
        Assert.True(entries.Contains(Entry(newA)));
        Assert.True(entries.Contains(Entry(newB)));
        Assert.True(entries.Contains(untouched));
        Assert.Equal(123, a.FileSize);
        Assert.Equal(456, b.FileSize);
    }

    [Fact]
    public void JournalFailure_RestoresOriginalHashSetEntries() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-db-rollback");
        string oldPath = Path.Combine(root, "old.mp4"), newPath = Path.Combine(root, "new.mp4");
        var entry = Entry(oldPath);
        var items = new HashSet<FileEntry> { entry };
        bool result = ScanEngine.TryApplyFolderMergeMoves(items,
            [(oldPath, newPath)], _ => false, out var error);
        Assert.False(result);
        Assert.NotEmpty(error);
        Assert.True(items.Contains(Entry(oldPath)));
        Assert.False(items.Contains(Entry(newPath)));
        Assert.Equal(oldPath, entry.Path);
    }

    [Fact]
    public void TargetAlreadyTrackedAsAnotherEntry_IsRejectedWithoutMutation() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-db-collision");
        string oldPath = Path.Combine(root, "old.mp4"), newPath = Path.Combine(root, "new.mp4");
        var old = Entry(oldPath);
        var occupied = Entry(newPath);
        var items = new HashSet<FileEntry> { old, occupied };
        bool result = ScanEngine.TryApplyFolderMergeMoves(items,
            [(oldPath, newPath)], _ => throw new Exception("must not commit"), out var error);
        Assert.False(result);
        Assert.Contains("占用", error);
        Assert.True(items.Contains(old));
        Assert.True(items.Contains(occupied));
    }

    [Fact]
    public void UntrackedFiles_DoNotRequireFullDatabaseRewrite() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-db-untracked");
        var items = new HashSet<FileEntry>();
        bool result = ScanEngine.TryApplyFolderMergeMoves(items,
            [(Path.Combine(root, "old.bin"), Path.Combine(root, "new.bin"))],
            _ => throw new Exception("should not persist"), out var error);
        Assert.True(result, error);
        Assert.Empty(items);
    }
}
