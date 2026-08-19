// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Text.RegularExpressions;

using VDF.GUI.Utils;

namespace VDF.GUI.Tests;

/// <summary>
/// Keep the application version consistent with the repository's release contract.
/// Upstream VDF may publish to a numeric <c>major.minor.x</c> tag. This custom fork
/// publishes immutable numeric releases such as <c>v4.1.0</c>, <c>v4.1.1</c>, ...;
/// the Windows workflow keeps the configured major/minor and auto-increments patch.
/// </summary>
public class VersionConsistencyTests {

    static string RepoRoot() {
        // Walk up from the test bin folder to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    static string VersionPrefixMajorMinor() {
        string props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var match = Regex.Match(props, @"<VersionPrefix>(\d+)\.(\d+)\.\d+</VersionPrefix>");
        Assert.True(match.Success, "Directory.Build.props: no <VersionPrefix> found");
        return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
    }

    static string? UpstreamReleaseTagMajorMinor() {
        string path = Path.Combine(RepoRoot(), ".github", "workflows", "releases.yml");
        if (!File.Exists(path))
            return null;

        string yml = File.ReadAllText(path);
        var match = Regex.Match(yml, @"tag_name:\s*(\d+)\.(\d+)\.x");
        Assert.True(match.Success, "releases.yml: no 'tag_name: <major>.<minor>.x' found");
        return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
    }

    static void AssertCustomNumericReleaseContract() {
        string path = Path.Combine(RepoRoot(), ".github", "workflows", "build-custom-windows.yml");
        Assert.True(File.Exists(path), "No upstream releases.yml or custom build-custom-windows.yml release workflow found");

        string yml = File.ReadAllText(path);
        Assert.Contains("Resolve release version", yml, StringComparison.Ordinal);
        Assert.Contains("$tag = \"v$version\"", yml, StringComparison.Ordinal);
        Assert.Contains("gh release create $tag", yml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-p:VersionPrefix=${{ steps.version.outputs.version }}", yml, StringComparison.Ordinal);
        Assert.DoesNotContain("Update rolling custom-latest Release", yml, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionPrefix_MatchesReleaseContract() {
        string version = VersionPrefixMajorMinor();
        string? upstreamRelease = UpstreamReleaseTagMajorMinor();
        if (upstreamRelease != null) {
            Assert.Equal(upstreamRelease, version);
            return;
        }

        AssertCustomNumericReleaseContract();
    }

    [Fact]
    public void BuiltAssemblyVersion_MatchesVersionPrefix() {
        // VersionInfo.Version prefers the entry assembly, which under the test host is
        // testhost rather than the GUI — check the VDF.GUI assembly directly instead.
        var v = typeof(VersionInfo).Assembly.GetName().Version;
        Assert.NotNull(v);
        Assert.Equal(VersionPrefixMajorMinor(), $"{v!.Major}.{v.Minor}");
    }
}
