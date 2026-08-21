// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using VDF.Core.Utils;

namespace VDF.GUI.Utils {
	internal sealed record GitHubReleaseUpdate(
		Version Version,
		string Tag,
		string AssetName,
		Uri AssetUrl,
		long AssetSize,
		string? Sha256);

	internal sealed record PreparedGitHubUpdate(
		GitHubReleaseUpdate Release,
		string TempRoot,
		string PayloadRoot);

	/// <summary>
	/// Update client for the public masakacj/vdf-custom GitHub Releases feed.
	/// It never updates in place while the GUI is running: download + verification happen
	/// first, then the freshly downloaded VDF.GUI.exe is launched in installer mode.
	/// </summary>
	internal static class GitHubUpdateService {
		const string LatestReleaseApi = "https://api.github.com/repos/masakacj/vdf-custom/releases/latest";
		const long MaxGuiZipBytes = 512L * 1024 * 1024;

		internal static Version CurrentVersion => ReadExecutableVersion(Environment.ProcessPath) ?? new Version(0, 0, 0);

		internal static async Task<GitHubReleaseUpdate?> CheckLatestAsync(CancellationToken token) {
			if (!OperatingSystem.IsWindows()) return null;
			using var http = CreateHttpClient(TimeSpan.FromSeconds(30));
			using HttpResponseMessage response = await http.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseContentRead, token);
			if (!response.IsSuccessStatusCode)
				throw new HttpRequestException($"GitHub Releases 查询失败：{(int)response.StatusCode} {response.ReasonPhrase}");
			await using Stream body = await response.Content.ReadAsStreamAsync(token);
			using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: token);
			GitHubReleaseUpdate release = ParseLatestRelease(json.RootElement);
			return release.Version.CompareTo(CurrentVersion) > 0 ? release : null;
		}

		internal static GitHubReleaseUpdate ParseLatestRelease(JsonElement root) {
			string tag = root.TryGetProperty("tag_name", out JsonElement tagNode)
				? tagNode.GetString() ?? string.Empty : string.Empty;
			if (!TryParseTagVersion(tag, out Version version))
				throw new InvalidDataException($"GitHub Release 版本号无效：'{tag}'");

			string expectedAsset = $"VDF-Custom-GUI-v{FormatVersion(version)}-win-x64.zip";
			if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
				throw new InvalidDataException("GitHub Release 没有 assets 列表。");
			foreach (JsonElement asset in assets.EnumerateArray()) {
				string name = asset.TryGetProperty("name", out JsonElement nameNode)
					? nameNode.GetString() ?? string.Empty : string.Empty;
				if (!name.Equals(expectedAsset, StringComparison.OrdinalIgnoreCase)) continue;
				string? urlText = asset.TryGetProperty("browser_download_url", out JsonElement urlNode)
					? urlNode.GetString() : null;
				if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttps)
					throw new InvalidDataException("GitHub GUI 更新包下载地址无效。");
				long size = asset.TryGetProperty("size", out JsonElement sizeNode) && sizeNode.TryGetInt64(out long parsedSize)
					? parsedSize : 0;
				if (size <= 0 || size > MaxGuiZipBytes)
					throw new InvalidDataException($"GitHub GUI 更新包大小异常：{size:N0} bytes。");
				string? digest = asset.TryGetProperty("digest", out JsonElement digestNode)
					? digestNode.GetString() : null;
				string? sha256 = ParseSha256Digest(digest);
				if (sha256 == null)
					throw new InvalidDataException("GitHub GUI 更新包没有可用的 SHA-256 digest，拒绝自动更新。");
				return new GitHubReleaseUpdate(version, tag, name, url, size, sha256);
			}
			throw new InvalidDataException($"GitHub Release 中未找到 {expectedAsset}。");
		}

		internal static async Task<PreparedGitHubUpdate> DownloadAndPrepareAsync(
			GitHubReleaseUpdate release,
			Action<long, long?>? progress,
			CancellationToken token) {
			string tempRoot = Path.Combine(Path.GetTempPath(), "VDF-Update-" + Guid.NewGuid().ToString("N"));
			string zipPath = Path.Combine(tempRoot, release.AssetName);
			string payloadRoot = Path.Combine(tempRoot, "payload");
			Directory.CreateDirectory(payloadRoot);
			try {
				using var http = CreateHttpClient(TimeSpan.FromMinutes(20));
				await DownloadUtils.DownloadFileAsync(http, release.AssetUrl, zipPath, release.AssetName,
					progress, token, maxBytes: MaxGuiZipBytes);
				if (release.Sha256 is { Length: > 0 })
					await VerifySha256Async(zipPath, release.Sha256, token);
				ExtractZipSafely(zipPath, payloadRoot);
				string updaterExe = Path.Combine(payloadRoot, "VDF.GUI.exe");
				if (!File.Exists(updaterExe))
					throw new InvalidDataException("更新包缺少 VDF.GUI.exe。");
				Version? payloadVersion = ReadExecutableVersion(updaterExe);
				if (payloadVersion == null || !payloadVersion.Equals(release.Version))
					throw new InvalidDataException($"更新包程序版本不匹配：期望 {FormatVersion(release.Version)}，实际 {payloadVersion}。");
				return new PreparedGitHubUpdate(release, tempRoot, payloadRoot);
			}
			catch {
				DeleteDirectoryBestEffort(tempRoot);
				throw;
			}
		}

		internal static Process LaunchInstaller(PreparedGitHubUpdate prepared, string targetFolder, int currentPid) {
			string updaterExe = Path.Combine(prepared.PayloadRoot, "VDF.GUI.exe");
			var psi = new ProcessStartInfo {
				FileName = updaterExe,
				UseShellExecute = false,
				WorkingDirectory = prepared.PayloadRoot,
			};
			psi.ArgumentList.Add("--apply-update");
			psi.ArgumentList.Add(currentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
			psi.ArgumentList.Add(prepared.PayloadRoot);
			psi.ArgumentList.Add(targetFolder);
			psi.ArgumentList.Add(prepared.TempRoot);
			return Process.Start(psi) ?? throw new InvalidOperationException("无法启动 VDF 更新进程。");
		}

		internal static bool TryParseTagVersion(string? tag, out Version version) {
			version = new Version(0, 0, 0);
			string text = (tag ?? string.Empty).Trim();
			if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
			if (!Version.TryParse(text, out Version? parsed) || parsed.Major < 0 || parsed.Minor < 0)
				return false;
			version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
			return true;
		}

		internal static string? ParseSha256Digest(string? digest) {
			if (string.IsNullOrWhiteSpace(digest)) return null;
			const string prefix = "sha256:";
			if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
			string value = digest[prefix.Length..].Trim();
			return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
		}

		internal static void ExtractZipSafely(string zipPath, string destination) {
			string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
			using ZipArchive zip = ZipFile.OpenRead(zipPath);
			foreach (ZipArchiveEntry entry in zip.Entries) {
				if (string.IsNullOrEmpty(entry.FullName)) continue;
				string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
				if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException($"更新包包含非法路径：{entry.FullName}");
				if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) {
					Directory.CreateDirectory(target);
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				entry.ExtractToFile(target, overwrite: true);
			}
		}

		static async Task VerifySha256Async(string path, string expected, CancellationToken token) {
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
			byte[] hash = await SHA256.HashDataAsync(stream, token);
			string actual = Convert.ToHexStringLower(hash);
			if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException($"更新包 SHA-256 校验失败。期望 {expected}，实际 {actual}。");
		}

		static HttpClient CreateHttpClient(TimeSpan timeout) {
			var http = new HttpClient { Timeout = timeout };
			http.DefaultRequestHeaders.UserAgent.ParseAdd($"VDF-Custom-Updater/{FormatVersion(CurrentVersion)}");
			http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
			http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
			return http;
		}

		static Version? ReadExecutableVersion(string? path) {
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
			try {
				string? raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
				if (string.IsNullOrWhiteSpace(raw)) return null;
				string numeric = raw.Split('+', '-', ' ')[0];
				if (!Version.TryParse(numeric, out Version? parsed)) return null;
				return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
			}
			catch { return null; }
		}

		internal static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

		internal static void DeleteDirectoryBestEffort(string? path) {
			try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, recursive: true); }
			catch { }
		}
	}
}
