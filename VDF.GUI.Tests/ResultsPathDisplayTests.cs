// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Globalization;
using VDF.GUI.Data;
using VDF.GUI.Mvvm;

namespace VDF.GUI.Tests;

public class ResultsPathDisplayTests {
    static string RepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void FullPathWrapping_IsEnabledByDefaultAndPersistable() {
        var settings = new SettingsFile();
        Assert.True(settings.ResultsWrapFullPath);
        settings.ResultsWrapFullPath = false;
        Assert.False(settings.ResultsWrapFullPath);
    }

    [Fact]
    public void WrappedComfortableRow_UsesAutoHeight_CompactRowStaysFixed() {
        var converter = new ResultsRowSizingConverter();
        object?[] comfortable = [160d, false, true, null, 0, 0, null, true];
        object? result = converter.Convert(comfortable, typeof(double), "row", CultureInfo.InvariantCulture);
        Assert.IsType<double>(result);
        Assert.True(double.IsNaN((double)result!));

        object?[] compact = [160d, true, true, null, 0, 0, null, true];
        result = converter.Convert(compact, typeof(double), "row", CultureInfo.InvariantCulture);
        Assert.IsType<double>(result);
        Assert.False(double.IsNaN((double)result!));
    }

    [Fact]
    public void ResultsXaml_ShowsFullPathAndExposesWrapToggle() {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "VDF.GUI", "Views", "DuplicateResultsView.xaml"));
        Assert.Contains("Path=ResultsWrapFullPath", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"路径换行\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Item.ItemInfo.Path}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding Item.ItemInfo.Path}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FullPathRow_SpansBelowAllMetadataColumns() {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "VDF.GUI", "Views", "DuplicateResultsView.xaml"));
        const string spanningGrid = "<Grid RowDefinitions=\"*,Auto\">";
        const string metadataRow = "<DockPanel Grid.Row=\"0\" LastChildFill=\"True\">";
        const string pathRow = "Grid.Row=\"1\"";

        int gridIndex = xaml.IndexOf(spanningGrid, StringComparison.Ordinal);
        int metadataIndex = xaml.IndexOf(metadataRow, gridIndex >= 0 ? gridIndex : 0, StringComparison.Ordinal);
        int pathIndex = xaml.IndexOf(pathRow, metadataIndex >= 0 ? metadataIndex : 0, StringComparison.Ordinal);

        Assert.True(gridIndex >= 0, "result content must have a two-row spanning grid");
        Assert.True(metadataIndex > gridIndex, "metadata must stay in the upper row");
        Assert.True(pathIndex > metadataIndex, "full path must be a separate lower row spanning beneath metadata columns");
    }
}
