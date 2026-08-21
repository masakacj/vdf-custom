using System.IO.Compression;
using System.Text.Json;
using VDF.Updater;

namespace VDF.GUI.Tests;

public class StandaloneUpdaterTests {
    [Fact]
    public void ParseLatestRelease_SelectsExactGuiAssetAndDigest() {
        const string json = """
        {
          "tag_name": "v4.1.25",
          "assets": [
            {
              "name": "VDF.Updater.exe",
              "browser_download_url": "https://github.com/x/updater.exe",
              "size": 100,
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            },
            {
              "name": "VDF-Custom-GUI-v4.1.25-win-x64.zip",
              "browser_download_url": "https://github.com/masakacj/vdf-custom/releases/download/v4.1.25/gui.zip",
              "size": 123456,
              "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
          ]
        }
        """;
        using JsonDocument doc = JsonDocument.Parse(json);

        ReleaseInfo release = ReleaseUpdateClient.ParseLatestRelease(doc.RootElement);

        Assert.Equal(new Version(4, 1, 25), release.Version);
        Assert.Equal("VDF-Custom-GUI-v4.1.25-win-x64.zip", release.AssetName);
        Assert.Equal(123456, release.AssetSize);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", release.Sha256);
    }

    [Theory]
    [InlineData("v4.1.20", 4, 1, 20)]
    [InlineData("4.2.0", 4, 2, 0)]
    [InlineData("V10.7.321", 10, 7, 321)]
    public void TagVersionParser_AcceptsReleaseTags(string tag, int major, int minor, int patch) {
        Assert.True(ReleaseUpdateClient.TryParseTagVersion(tag, out Version version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Fact]
    public void ExtractZipSafely_RejectsPathTraversal() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-standalone-update-test-" + Guid.NewGuid().ToString("N"));
        string zipPath = Path.Combine(root, "bad.zip");
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        try {
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) {
                ZipArchiveEntry entry = zip.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("bad");
            }

            Assert.Throws<InvalidDataException>(() => ReleaseUpdateClient.ExtractZipSafely(zipPath, output));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally {
            ReleaseUpdateClient.DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void InstallerCopy_OverwritesReleaseFilesIncludingUpdater_ButPreservesUserFiles() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-standalone-copy-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "payload");
        string target = Path.Combine(root, "install");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try {
            File.WriteAllText(Path.Combine(source, "VDF.GUI.exe"), "new gui");
            File.WriteAllText(Path.Combine(source, "VDF.Updater.exe"), "new updater");
            Directory.CreateDirectory(Path.Combine(source, "runtimes"));
            File.WriteAllText(Path.Combine(source, "runtimes", "new.dll"), "new dll");

            File.WriteAllText(Path.Combine(target, "VDF.GUI.exe"), "old gui");
            File.WriteAllText(Path.Combine(target, "VDF.Updater.exe"), "old updater");
            File.WriteAllText(Path.Combine(target, "Settings.json"), "user settings");
            File.WriteAllText(Path.Combine(target, "ScannedFiles.db"), "user db");
            File.WriteAllText(Path.Combine(target, "log.txt"), "user log");

            StandaloneInstaller.CopyPayloadWithRetry(source, target, TimeSpan.FromSeconds(2));

            Assert.Equal("new gui", File.ReadAllText(Path.Combine(target, "VDF.GUI.exe")));
            Assert.Equal("new updater", File.ReadAllText(Path.Combine(target, "VDF.Updater.exe")));
            Assert.Equal("new dll", File.ReadAllText(Path.Combine(target, "runtimes", "new.dll")));
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(target, "Settings.json")));
            Assert.Equal("user db", File.ReadAllText(Path.Combine(target, "ScannedFiles.db")));
            Assert.Equal("user log", File.ReadAllText(Path.Combine(target, "log.txt")));
        }
        finally {
            ReleaseUpdateClient.DeleteDirectoryBestEffort(root);
        }
    }
}
