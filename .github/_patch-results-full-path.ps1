$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $oldNorm = $Old.Replace("`r`n", "`n")
    $newNorm = $New.Replace("`r`n", "`n")
    $matches = ([regex]::Matches($text, [regex]::Escape($oldNorm))).Count
    if ($matches -ne 1) { throw "Expected exactly one match in $Path, got $matches" }
    $text = $text.Replace($oldNorm, $newNorm)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$old = @'
		bool _ResultsCompactRows;
		[JsonPropertyName("ResultsCompactRows")]
		public bool ResultsCompactRows {
			get => _ResultsCompactRows;
			set => this.RaiseAndSetIfChanged(ref _ResultsCompactRows, value);
		}
		ThumbnailDoubleClickAction _ThumbnailDoubleClickAction = ThumbnailDoubleClickAction.OpenFile;
'@
$new = @'
		bool _ResultsCompactRows;
		[JsonPropertyName("ResultsCompactRows")]
		public bool ResultsCompactRows {
			get => _ResultsCompactRows;
			set => this.RaiseAndSetIfChanged(ref _ResultsCompactRows, value);
		}
		bool _ResultsWrapFullPath = true;
		/// <summary>Show the complete result-file path on multiple lines in comfortable rows.</summary>
		[JsonPropertyName("ResultsWrapFullPath")]
		public bool ResultsWrapFullPath {
			get => _ResultsWrapFullPath;
			set => this.RaiseAndSetIfChanged(ref _ResultsWrapFullPath, value);
		}
		ThumbnailDoubleClickAction _ThumbnailDoubleClickAction = ThumbnailDoubleClickAction.OpenFile;
'@
Replace-Exact 'VDF.GUI/Data/SettingsFile.cs' $old $new

$old = @'
			double width = values.Count > 0 && values[0] is double d ? d : 160;
			bool compact = values.Count > 1 && values[1] is true;
			bool previewVisible = true;
			double thumbWidth = 0, thumbHeight = 0;
			int frameCount = 0, gridColumns = 0;
			bool haveFrameCount = false;
			Utils.ThumbnailSizePrediction? prediction = null;
			for (int i = 2; i < values.Count; i++) {
'@
$new = @'
			double width = values.Count > 0 && values[0] is double d ? d : 160;
			bool compact = values.Count > 1 && values[1] is true;
			bool rowMode = string.Equals(parameter as string, "row", StringComparison.OrdinalIgnoreCase);
			// The result-row binding appends ResultsWrapFullPath as its eighth value.
			// Keep it out of the generic bool scan below (where bool means preview visibility).
			bool hasPathWrapValue = rowMode && values.Count >= 8 && values[^1] is bool;
			bool wrapFullPath = hasPathWrapValue && values[^1] is true;
			int valueCount = hasPathWrapValue ? values.Count - 1 : values.Count;
			bool previewVisible = true;
			double thumbWidth = 0, thumbHeight = 0;
			int frameCount = 0, gridColumns = 0;
			bool haveFrameCount = false;
			Utils.ThumbnailSizePrediction? prediction = null;
			for (int i = 2; i < valueCount; i++) {
'@
Replace-Exact 'VDF.GUI/Mvvm/Converters.cs' $old $new

$old = @'
			return string.Equals(parameter as string, "row", StringComparison.OrdinalIgnoreCase)
				? Utils.ResultsRowSizing.RowHeight(width, compact, previewVisible, thumbWidth, thumbHeight, frameCount, gridColumns)
				: Utils.ResultsRowSizing.ImageHeight(width, compact, thumbWidth, thumbHeight, frameCount, gridColumns);
'@
$new = @'
			// Wrapped full paths need a content-driven row height. Only comfortable rows
			// opt into variable height; compact rows keep the fixed fast-path geometry.
			if (rowMode && wrapFullPath && !compact)
				return double.NaN;
			return rowMode
				? Utils.ResultsRowSizing.RowHeight(width, compact, previewVisible, thumbWidth, thumbHeight, frameCount, gridColumns)
				: Utils.ResultsRowSizing.ImageHeight(width, compact, thumbWidth, thumbHeight, frameCount, gridColumns);
'@
Replace-Exact 'VDF.GUI/Mvvm/Converters.cs' $old $new

$old = @'
        <ToggleButton
            Classes="chip"
            Margin="10,0,0,0"
            IsChecked="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=ResultsCompactRows}"
            Content="{Binding Source={x:Static local:App.Lang}, Path=[Results.Toolbar.CompactRows]}" />
'@
$new = @'
        <ToggleButton
            Classes="chip"
            Margin="10,0,0,0"
            IsChecked="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=ResultsCompactRows}"
            Content="{Binding Source={x:Static local:App.Lang}, Path=[Results.Toolbar.CompactRows]}" />
        <ToggleButton
            Classes="chip"
            Margin="4,0,0,0"
            IsEnabled="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=!ResultsCompactRows}"
            IsChecked="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=ResultsWrapFullPath}"
            Content="路径换行"
            ToolTip.Tip="完整显示文件路径；长路径自动换行并按内容增高当前行。紧凑行模式下保持关闭。" />
'@
Replace-Exact 'VDF.GUI/Views/DuplicateResultsView.xaml' $old $new

$old = @'
          <MenuItem
              Header="{Binding Source={x:Static local:App.Lang}, Path=[Results.Toolbar.CompactRows]}"
              ToggleType="CheckBox"
              IsChecked="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=ResultsCompactRows}" />
'@
$new = @'
          <MenuItem
              Header="{Binding Source={x:Static local:App.Lang}, Path=[Results.Toolbar.CompactRows]}"
              ToggleType="CheckBox"
              IsChecked="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=ResultsCompactRows}" />
          <MenuItem
              Header="路径自动换行"
              ToggleType="CheckBox"
              IsEnabled="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=!ResultsCompactRows}"
              IsChecked="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=ResultsWrapFullPath}" />
'@
Replace-Exact 'VDF.GUI/Views/DuplicateResultsView.xaml' $old $new

$old = @'
                <Binding Path="Item.ThumbnailFrameCount" />
                <Binding Path="Item.ThumbnailGridColumns" />
                <Binding Path="Item.ThumbnailPrediction" />
              </MultiBinding>
'@
$new = @'
                <Binding Path="Item.ThumbnailFrameCount" />
                <Binding Path="Item.ThumbnailGridColumns" />
                <Binding Path="Item.ThumbnailPrediction" />
                <Binding Source="{x:Static Settings:SettingsFile.Instance}" Path="ResultsWrapFullPath" />
              </MultiBinding>
'@
Replace-Exact 'VDF.GUI/Views/DuplicateResultsView.xaml' $old $new

$old = @'
                <!-- Directory is a real action now: click the displayed folder path to open it.
                     Full-file-path copy remains available from the row context menu. -->
                <Border
                    Background="Transparent"
                    IsVisible="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=!ResultsCompactRows}">
                  <DockPanel>
                    <TextBlock
                        DockPanel.Dock="Right"
                        Margin="10,0,0,0"
                        FontSize="10.8"
                        Opacity="0.52"
                        VerticalAlignment="Center"
                        IsVisible="{Binding FolderStatsText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
                        Text="{Binding FolderStatsText}"
                        ToolTip.Tip="当前文件所在文件夹中的已索引媒体文件数量和总大小" />
                    <Button Classes="folder-link"
                            Command="{Binding $parent[UserControl].((vm:MainWindowVM)DataContext).OpenContainingFolderCommand}"
                            CommandParameter="{Binding Item}"
                            ToolTip.Tip="点击打开文件所在文件夹">
                      <ctl:MiddleEllipsisTextBlock
                          FontFamily="Microsoft YaHei UI"
                          FontSize="11.5"
                          Foreground="{DynamicResource VdfPrimaryBrush}"
                          Text="{Binding Item.ItemInfo.Path, Converter={StaticResource DirectoryPathConverter}}" />
                    </Button>
                  </DockPanel>
                </Border>
'@
$new = @'
                <!-- Full path uses the otherwise-empty second line across the remaining
                     File column. Single-line mode keeps the cheap middle ellipsis;
                     optional wrap mode shows the entire path and lets visible rows grow. -->
                <Border
                    Background="Transparent"
                    IsVisible="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=!ResultsCompactRows}">
                  <DockPanel LastChildFill="True">
                    <TextBlock
                        DockPanel.Dock="Right"
                        Margin="10,0,0,0"
                        FontSize="10.8"
                        Opacity="0.52"
                        VerticalAlignment="Center"
                        IsVisible="{Binding FolderStatsText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
                        Text="{Binding FolderStatsText}"
                        ToolTip.Tip="当前文件所在文件夹中的已索引媒体文件数量和总大小" />
                    <Button Classes="folder-link"
                            VerticalContentAlignment="Stretch"
                            Command="{Binding $parent[UserControl].((vm:MainWindowVM)DataContext).OpenContainingFolderCommand}"
                            CommandParameter="{Binding Item}"
                            ToolTip.Tip="{Binding Item.ItemInfo.Path}">
                      <Grid HorizontalAlignment="Stretch">
                        <ctl:MiddleEllipsisTextBlock
                            IsVisible="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=!ResultsWrapFullPath}"
                            FontFamily="Microsoft YaHei UI"
                            FontSize="11.5"
                            Foreground="{DynamicResource VdfPrimaryBrush}"
                            Text="{Binding Item.ItemInfo.Path}" />
                        <TextBlock
                            IsVisible="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=ResultsWrapFullPath}"
                            HorizontalAlignment="Stretch"
                            VerticalAlignment="Center"
                            FontSize="11.5"
                            Foreground="{DynamicResource VdfPrimaryBrush}"
                            TextWrapping="Wrap"
                            Text="{Binding Item.ItemInfo.Path}" />
                      </Grid>
                    </Button>
                  </DockPanel>
                </Border>
'@
Replace-Exact 'VDF.GUI/Views/DuplicateResultsView.xaml' $old $new

$test = @'
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
}
'@
[IO.File]::WriteAllText('VDF.GUI.Tests/ResultsPathDisplayTests.cs', $test.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))

git rm .github/workflows/_patch-results-full-path.yml .github/_patch-results-full-path.ps1
git add VDF.GUI/Data/SettingsFile.cs VDF.GUI/Mvvm/Converters.cs VDF.GUI/Views/DuplicateResultsView.xaml VDF.GUI.Tests/ResultsPathDisplayTests.cs
git diff --check
git config user.name 'github-actions[bot]'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git commit -m 'Show complete result paths with optional wrapping'
git push origin HEAD:feat/results-full-path-wrap
