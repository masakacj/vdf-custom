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
        Assert.Contains("<Grid RowDefinitions=\"*,Auto\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("including underneath Duration/Size/Bitrate/Format/Similarity", xaml, StringComparison.Ordinal);
    }
}