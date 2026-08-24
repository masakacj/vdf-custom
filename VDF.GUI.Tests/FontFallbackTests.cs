// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

namespace VDF.GUI.Tests;

public class FontFallbackTests {
    static string RepoRoot() {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public void WindowsFallbackChain_StartsWithNativeSimplifiedChineseUiFonts() {
        string[] families = VDF.GUI.Program.WindowsCjkFallbackFamilies;

        Assert.Equal("Microsoft YaHei UI", families[0]);
        Assert.Contains("Microsoft YaHei", families);
        Assert.Contains("DengXian", families);
        Assert.Contains("SimSun", families);
        Assert.DoesNotContain("PingFang SC", families);
    }

    [Fact]
    public void MacFallbackChain_DoesNotLeakIntoWindowsChain() {
        Assert.Contains("PingFang SC", VDF.GUI.Program.MacCjkFallbackFamilies);
        Assert.Contains("Hiragino Sans", VDF.GUI.Program.MacCjkFallbackFamilies);
        Assert.DoesNotContain("Microsoft YaHei UI", VDF.GUI.Program.MacCjkFallbackFamilies);
    }

    [Fact]
    public void ExplicitMonospaceViews_AlwaysIncludeACjkFallback() {
        string root = RepoRoot();
        string[] files = [
            "BlacklistManagerView.xaml",
            "ExpressionBuilder.xaml",
            "ResourceConsolidationPreviewDialog.xaml",
            "ScanningView.xaml",
            "SettingsView.xaml",
            "ThumbnailComparer.xaml",
        ];

        foreach (string file in files) {
            string xaml = File.ReadAllText(Path.Combine(root, "VDF.GUI", "Views", file));
            foreach (string line in xaml.Split('\n').Where(line => line.Contains("FontFamily=", StringComparison.Ordinal)
                    && (line.Contains("Consolas", StringComparison.Ordinal) || line.Contains("Cascadia Mono", StringComparison.Ordinal)))) {
                Assert.True(
                    line.Contains("Microsoft YaHei", StringComparison.Ordinal)
                    || line.Contains("PingFang SC", StringComparison.Ordinal)
                    || line.Contains("Noto Sans CJK SC", StringComparison.Ordinal),
                    $"{file} has a Latin-only explicit font chain: {line.Trim()}");
            }
        }
    }

    [Fact]
    public void Program_ConfiguresWindowsFontManagerFallbacks() {
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "VDF.GUI", "Program.cs"));

        Assert.Contains("if (OperatingSystem.IsWindows())", code, StringComparison.Ordinal);
        Assert.Contains("WindowsCjkFallbackFamilies", code, StringComparison.Ordinal);
        Assert.Contains("new FontFallback", code, StringComparison.Ordinal);
    }
}
