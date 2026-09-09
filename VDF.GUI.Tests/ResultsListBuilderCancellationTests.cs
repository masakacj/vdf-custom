// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.GUI.ViewModels;

namespace VDF.GUI.Tests;

public class ResultsListBuilderCancellationTests {
    [Fact]
    public void Build_PreCanceledRequest_StopsBeforeDoingResultWork() {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ResultsListBuilder.Build(new ResultsBuildRequest {
            Items = Array.Empty<DuplicateItemVM>(),
            CancellationToken = cts.Token,
        }));
    }
}
