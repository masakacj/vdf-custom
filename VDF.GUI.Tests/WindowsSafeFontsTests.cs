using Avalonia.Media;
using VDF.GUI;

namespace VDF.GUI.Tests;

public class WindowsSafeFontsTests {
    [Fact]
    public void RecoveryFontManagerUsesOnlyMemoryCollectionForConfiguredFamilies() {
        FontManagerOptions options = WindowsSafeFonts.CreateOptions();
        Assert.Equal("fonts:VdfSafe#Microsoft YaHei", options.DefaultFamilyName);
        Assert.NotNull(options.FontFamilyMappings);
        Assert.NotNull(options.FontFallbacks);
        foreach (string name in new[] {
            "Microsoft YaHei UI", "Microsoft YaHei", "DengXian",
            "Consolas", "Cascadia Mono", "Segoe UI", "Segoe UI Emoji",
            "Segoe UI Symbol", "Noto Sans CJK SC", "PingFang SC"
        }) {
            Assert.True(options.FontFamilyMappings!.TryGetValue(name, out FontFamily? family), name);
            Assert.StartsWith("fonts:VdfSafe#", family!.ToString());
        }
        Assert.All(options.FontFallbacks!, fallback =>
            Assert.StartsWith("fonts:VdfSafe#", fallback.FontFamily.ToString()));
    }
}
