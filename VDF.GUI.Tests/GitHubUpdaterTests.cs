// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.IO.Compression;
using System.Text.Json;
using VDF.GUI.Utils;

namespace VDF.GUI.Tests;

public class GitHubUpdaterTests {
    [Fact]
    public void ParseLatestRelease_SelectsExactGuiAssetAndDigest() {
        const string json = """
        {
          "tag_name": "v4.1.23",
          "assets": [
            {
              "name": "VDF-Custom-CLI-v4.1.23-win-x64.zip",
              "browser_download_url": "https://github.com/x/cli.zip",
              "size": 100,
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            },
            {
              "name": "VDF-Custom-GUI-v4.1.23-win-x64.zip",
              "browser_download_url": "https://github.com/masakacj/vdf-custom/releases/download/v4.1.23/gui.zip",
              "size": 123456,
              "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }
          ]
        }
        """;
        using JsonDocument doc = JsonDocument.Parse(json);

        GitHubReleaseUpdate release = GitHubUpdateService.ParseLatestRelease(doc.RootElement);

        Assert.Equal(new Version(4, 1, 23), release.Version);
        Assert.Equal("VDF-Custom-GUI-v4.1.23-win-x64.zip", release.AssetName);
        Assert.Equal(123456, release.AssetSize);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", release.Sha256);
    }

    [Theory]
    [InlineData("v4.1.18", 4, 1, 18)]
    [InlineData("4.2.0", 4, 2, 0)]
    [InlineData("V10.7.321", 10, 7, 321)]
    public void TagVersionParser_AcceptsReleaseTags(string tag, int major, int minor, int patch) {
        Assert.True(GitHubUpdateService.TryParseTagVersion(tag, out Version version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Fact]
    public void ExtractZipSafely_RejectsPathTraversal() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-update-test-" + Guid.NewGuid().ToString("N"));
        string zipPath = Path.Combine(root, "bad.zip");
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        try {
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) {
                ZipArchiveEntry entry = zip.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("bad");
            }

            Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ExtractZipSafely(zipPath, output));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally {
            GitHubUpdateService.DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void InstallerCopy_OverwritesReleaseFilesButPreservesUserFiles() {
        string root = Path.Combine(Path.GetTempPath(), "vdf-update-copy-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "payload");
        string target = Path.Combine(root, "install");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try {
            File.WriteAllText(Path.Combine(source, "VDF.GUI.exe"), "new exe");
            Directory.CreateDirectory(Path.Combine(source, "runtimes"));
            File.WriteAllText(Path.Combine(source, "runtimes", "new.dll"), "new dll");
            File.WriteAllText(Path.Combine(target, "VDF.GUI.exe"), "old exe");
            File.WriteAllText(Path.Combine(target, "Settings.json"), "user settings");
            File.WriteAllText(Path.Combine(target, "ScannedFiles.db"), "user db");

            SelfUpdateInstaller.CopyPayloadWithRetry(source, target, TimeSpan.FromSeconds(2));

            Assert.Equal("new exe", File.ReadAllText(Path.Combine(target, "VDF.GUI.exe")));
            Assert.Equal("new dll", File.ReadAllText(Path.Combine(target, "runtimes", "new.dll")));
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(target, "Settings.json")));
            Assert.Equal("user db", File.ReadAllText(Path.Combine(target, "ScannedFiles.db")));
        }
        finally {
            GitHubUpdateService.DeleteDirectoryBestEffort(root);
        }
    }
}
