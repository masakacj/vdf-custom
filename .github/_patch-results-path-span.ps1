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

# After checkbox + preview have been docked left, put all top-line metadata/name
# into row 0 of a new grid. Row 1 can then use the entire remaining horizontal width.
$old = @'
              </Grid>
              <!-- similarity chip -->
'@
$new = @'
              </Grid>
              <!-- Metadata/name occupy the upper band. The full-path row below spans
                   the entire remaining width after checkbox + preview, including the
                   otherwise-empty area underneath the right-side metric columns. -->
              <Grid RowDefinitions="*,Auto">
                <DockPanel Grid.Row="0" LastChildFill="True">
              <!-- similarity chip -->
'@
Replace-Exact 'VDF.GUI/Views/DuplicateResultsView.xaml' $old $new

# Replace the former file-column-only path block and the row tail. The new Grid.Row=1
# path is outside the inner metadata DockPanel, so it spans below all metric columns.
$old = @'
                </DockPanel>
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
              </StackPanel>
            </DockPanel>
          </Border>
        </DataTemplate>
'@
$new = @'
                </DockPanel>
              </StackPanel>
                </DockPanel>
                <!-- Full path spans the complete lower band of the content region,
                     including underneath Duration/Size/Bitrate/Format/Similarity. -->
                <Border
                    Grid.Row="1"
                    Margin="6,0,0,2"
                    Background="Transparent"
                    IsVisible="{Binding Source={x:Static Settings:SettingsFile.Instance}, Path=!ResultsCompactRows}">
                  <DockPanel LastChildFill="True">
                    <TextBlock
                        DockPanel.Dock="Right"
                        Margin="10,0,6,0"
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
              </Grid>
            </DockPanel>
          </Border>
        </DataTemplate>
'@
Replace-Exact 'VDF.GUI/Views/DuplicateResultsView.xaml' $old $new

$path = 'VDF.GUI.Tests/ResultsPathDisplayTests.cs'
$old = @'
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Item.ItemInfo.Path}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding Item.ItemInfo.Path}\"", xaml, StringComparison.Ordinal);
'@
$new = @'
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Item.ItemInfo.Path}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding Item.ItemInfo.Path}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Grid RowDefinitions=\"*,Auto\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("including underneath Duration/Size/Bitrate/Format/Similarity", xaml, StringComparison.Ordinal);
'@
Replace-Exact $path $old $new

git rm .github/workflows/_patch-results-path-span.yml .github/_patch-results-path-span.ps1
git add VDF.GUI/Views/DuplicateResultsView.xaml VDF.GUI.Tests/ResultsPathDisplayTests.cs
git diff --check
git config user.name 'github-actions[bot]'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git commit -m 'Span full result paths under metadata columns'
git push origin HEAD:feat/results-full-path-wrap
