// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class ResultsRowReconcilerScaleTests {
    [Fact]
    public void LargeGlobalReorder_ChoosesBulkPathBeforeRowReuseIndexing() {
        var current = Enumerable.Range(0, 9_000).Select(_ => (object)new object()).ToList();
        var desired = current.AsEnumerable().Reverse().ToList();

        Assert.True(ResultsRowReconciler.RequiresBulkRebuild(current, desired));
    }

    [Fact]
    public void TinyMiddleChange_KeepsIncrementalPath() {
        var current = Enumerable.Range(0, 9_000).Select(_ => (object)new object()).ToList();
        var desired = current.ToList();
        desired[4_500] = new object();

        Assert.False(ResultsRowReconciler.RequiresBulkRebuild(current, desired));
    }
}
