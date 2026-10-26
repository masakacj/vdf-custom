using VDF.Core.ViewModels;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class GroupHeaderBatchSelectionTests {
    static DuplicateItemVM Make(Guid groupId, string name, bool isChecked = false) => new() {
        Checked = isChecked,
        IsVisibleInFilter = true,
        ItemInfo = new DuplicateItem {
            GroupId = groupId,
            Path = Path.Combine(Path.GetTempPath(), name),
        }
    };

    [Fact]
    public void SelectAllAndInvert_AffectOnlyVisibleMembers_WithoutMutatingThemDuringPlanning() {
        Guid groupId = Guid.NewGuid(), otherId = Guid.NewGuid();
        var a = Make(groupId, "one.mp4");
        var b = Make(groupId, "two.mp4", true);
        var outsider = Make(otherId, "other.mp4", true);
        var header = new ResultsGroupHeader {
            GroupId = groupId,
            Rows = new[] { a, b, outsider }
                .Select(item => new ResultsItemRow(item)).ToList(),
        };

        var all = MainWindowVM.PlanGroupHeaderCheck(header, false);
        Assert.Single(all);
        Assert.Same(a, all[0].Item);
        Assert.True(all[0].Checked);
        Assert.False(a.Checked);
        Assert.True(b.Checked);
        Assert.True(outsider.Checked);

        var invert = MainWindowVM.PlanGroupHeaderCheck(header, true);
        Assert.Equal(2, invert.Count);
        Assert.Contains(invert, change => ReferenceEquals(change.Item, a) && change.Checked);
        Assert.Contains(invert, change => ReferenceEquals(change.Item, b) && !change.Checked);
        Assert.DoesNotContain(invert, change => ReferenceEquals(change.Item, outsider));
    }

    [Fact]
    public void InvertSameLogicalPath_StillUpdatesBothDistinctRows() {
        Guid id = Guid.NewGuid();
        var first = Make(id, "same.mp4", true);
        var second = Make(id, "same.mp4", false);
        var header = new ResultsGroupHeader {
            GroupId = id, Rows = new[] { new ResultsItemRow(first), new ResultsItemRow(second) }
        };
        var changes = MainWindowVM.PlanGroupHeaderCheck(header, true);
        Assert.Equal(2, changes.Count);
        Assert.False(changes[0].Checked);
        Assert.True(changes[1].Checked);
    }
}
