// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Text.Json;
using VDF.GUI.Data;

namespace VDF.GUI.Tests;

public class ChineseLanguageDefaultsTests : IDisposable {
    readonly string dir = Directory.CreateTempSubdirectory("vdf-chinese-language-tests-").FullName;

    public void Dispose() {
        SettingsFile.SetSettingsPath(null);
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void FreshSettings_DefaultToSimplifiedChinese() {
        var settings = new SettingsFile();
        Assert.Equal("zh-Hans", settings.LanguageCode);
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("zh-CN")]
    [InlineData("zh-SG")]
    [InlineData("zh-Hans")]
    [InlineData("ZH-cn")]
    public void ChineseAliases_NormalizeToZhHans(string code) {
        var settings = new SettingsFile { LanguageCode = code };
        Assert.Equal("zh-Hans", settings.LanguageCode);
    }

    [Fact]
    public void SaveSettings_UsesAtomicSiblingAndLeavesValidJson() {
        string path = Path.Combine(dir, "Settings.json");
        SettingsFile.Instance.LanguageCode = "zh-Hans";

        SettingsFile.SaveSettings(path);
        SettingsFile.SaveSettings(path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("zh-Hans", doc.RootElement.GetProperty("LanguageCode").GetString());
    }
}
