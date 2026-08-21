using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace VDF.Updater;

internal sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string AssetName,
    Uri AssetUrl,
    long AssetSize,
    string Sha256);

internal sealed record PreparedUpdate(
    ReleaseInfo Release,
    string TempRoot,
    string PayloadRoot);

/// <summary>
/// Minimal GitHub Releases client used by the standalone updater. It intentionally has no
/// dependency on VDF.GUI/Avalonia so VDF.Updater.exe can run by itself from a stopped install.
/// </summary>
internal static class ReleaseUpdateClient {
    internal const string LatestReleaseApi = "https://api.github.com/repos/masakacj/vdf-custom/releases/latest";
    internal const long MaxGuiZipBytes = 512L * 1024 * 1024;
    static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(90);

    internal static async Task<ReleaseInfo> GetLatestAsync(CancellationToken token) {
        using var http = CreateHttpClient(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await http.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseContentRead, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub Releases 查询失败：{(int)response.StatusCode} {response.ReasonPhrase}");

        await using Stream body = await response.Content.ReadAsStreamAsync(token);
        using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: token);
        return ParseLatestRelease(json.RootElement);
    }

    internal static ReleaseInfo ParseLatestRelease(JsonElement root) {
        string tag = root.TryGetProperty("tag_name", out JsonElement tagNode)
            ? tagNode.GetString() ?? string.Empty
            : string.Empty;
        if (!TryParseTagVersion(tag, out Version version))
            throw new InvalidDataException($"GitHub Release 版本号无效：'{tag}'");

        string expectedAsset = $"VDF-Custom-GUI-v{FormatVersion(version)}-win-x64.zip";
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub Release 没有 assets 列表。");

        foreach (JsonElement asset in assets.EnumerateArray()) {
            string name = asset.TryGetProperty("name", out JsonElement nameNode)
                ? nameNode.GetString() ?? string.Empty
                : string.Empty;
            if (!name.Equals(expectedAsset, StringComparison.OrdinalIgnoreCase))
                continue;

            string? urlText = asset.TryGetProperty("browser_download_url", out JsonElement urlNode)
                ? urlNode.GetString()
                : null;
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("GitHub GUI 更新包下载地址无效。");

            long size = asset.TryGetProperty("size", out JsonElement sizeNode) && sizeNode.TryGetInt64(out long parsedSize)
                ? parsedSize
                : 0;
            if (size <= 0 || size > MaxGuiZipBytes)
                throw new InvalidDataException($"GitHub GUI 更新包大小异常：{size:N0} bytes。");

            string? digest = asset.TryGetProperty("digest", out JsonElement digestNode)
                ? digestNode.GetString()
                : null;
            string? sha256 = ParseSha256Digest(digest);
            if (sha256 == null)
                throw new InvalidDataException("GitHub GUI 更新包没有可用的 SHA-256 digest，拒绝自动更新。");

            return new ReleaseInfo(version, tag, name, url, size, sha256);
        }

        throw new InvalidDataException($"GitHub Release 中未找到 {expectedAsset}。");
    }

    internal static async Task<PreparedUpdate> DownloadAndPrepareAsync(
        ReleaseInfo release,
        Action<long, long?>? progress,
        CancellationToken token) {
        string tempRoot = Path.Combine(Path.GetTempPath(), "VDF-Standalone-Update-" + Guid.NewGuid().ToString("N"));
        string zipPath = Path.Combine(tempRoot, release.AssetName);
        string payloadRoot = Path.Combine(tempRoot, "payload");
        Directory.CreateDirectory(payloadRoot);
        try {
            using var http = CreateHttpClient(TimeSpan.FromMinutes(20));
            await DownloadFileAsync(http, release.AssetUrl, zipPath, release.AssetName, progress, token, MaxGuiZipBytes);
            await VerifySha256Async(zipPath, release.Sha256, token);
            ExtractZipSafely(zipPath, payloadRoot);

            string guiExe = Path.Combine(payloadRoot, "VDF.GUI.exe");
            if (!File.Exists(guiExe))
                throw new InvalidDataException("更新包缺少 VDF.GUI.exe。");
            Version? payloadVersion = ReadExecutableVersion(guiExe);
            if (payloadVersion == null || !payloadVersion.Equals(release.Version))
                throw new InvalidDataException($"更新包程序版本不匹配：期望 {FormatVersion(release.Version)}，实际 {payloadVersion}。");

            string updaterExe = Path.Combine(payloadRoot, "VDF.Updater.exe");
            if (!File.Exists(updaterExe))
                throw new InvalidDataException("更新包缺少 VDF.Updater.exe，无法安全完成独立更新。");

            return new PreparedUpdate(release, tempRoot, payloadRoot);
        }
        catch {
            DeleteDirectoryBestEffort(tempRoot);
            throw;
        }
    }

    internal static bool TryParseTagVersion(string? tag, out Version version) {
        version = new Version(0, 0, 0);
        string text = (tag ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        if (!Version.TryParse(text, out Version? parsed) || parsed.Major < 0 || parsed.Minor < 0)
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    internal static string? ParseSha256Digest(string? digest) {
        if (string.IsNullOrWhiteSpace(digest))
            return null;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string value = digest[prefix.Length..].Trim();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    internal static void ExtractZipSafely(string zipPath, string destination) {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in zip.Entries) {
            if (string.IsNullOrEmpty(entry.FullName))
                continue;
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

    internal static Version? ReadExecutableVersion(string? path) {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try {
            string? raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            string numeric = raw.Split('+', '-', ' ')[0];
            if (!Version.TryParse(numeric, out Version? parsed))
                return null;
            return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        }
        catch {
            return null;
        }
    }

    internal static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    internal static void DeleteDirectoryBestEffort(string? path) {
        try {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    static async Task DownloadFileAsync(
        HttpClient http,
        Uri url,
        string destination,
        string displayName,
        Action<long, long?>? progress,
        CancellationToken token,
        long maxBytes) {
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载失败：{(int)response.StatusCode} {response.ReasonPhrase}");

        long? total = response.Content.Headers.ContentLength;
        if (total > maxBytes)
            throw new HttpRequestException($"更新包过大：{total:N0} bytes。");

        await using Stream source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        while (true) {
            int read;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                readCts.CancelAfter(ReadStallTimeout);
                try {
                    read = await source.ReadAsync(buffer, readCts.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) {
                    throw new TimeoutException($"{displayName} 下载停滞超过 {ReadStallTimeout.TotalSeconds:0} 秒。");
                }
            }
            if (read == 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            readTotal += read;
            if (readTotal > maxBytes)
                throw new HttpRequestException($"更新包超过大小限制：{maxBytes:N0} bytes。");
            progress?.Invoke(readTotal, total);
        }
        progress?.Invoke(readTotal, total);
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
        Version updaterVersion = ReadExecutableVersion(Environment.ProcessPath) ?? new Version(0, 0, 0);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VDF-Custom-Updater", FormatVersion(updaterVersion)));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }
}
