using ReactiveUI;

namespace VDF.GUI.Data {
    /// <summary>A persistent result-only folder exclusion; scan skipping is opt-in.</summary>
    public sealed class ResultPathExclusion : ReactiveObject {
        public string Path { get; set; } = string.Empty;
        bool skipScan;
        public bool SkipScan {
            get => skipScan;
            set => this.RaiseAndSetIfChanged(ref skipScan, value);
        }

        public static bool TryNormalize(string? input, out string path, out string error) {
            path = string.Empty;
            error = string.Empty;
            string raw = (input ?? string.Empty).Trim().Trim('"');
            if (raw.Length == 0 || raw.IndexOfAny(['*', '?']) >= 0 ||
                !System.IO.Path.IsPathFullyQualified(raw)) {
                error = "请输入完整文件夹路径，不支持相对路径或通配符。";
                return false;
            }
            try {
                string full = System.IO.Path.GetFullPath(raw);
                string root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
                string normalized = System.IO.Path.TrimEndingDirectorySeparator(full);
                if (string.IsNullOrEmpty(normalized) ||
                    normalized.Equals(System.IO.Path.TrimEndingDirectorySeparator(root),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                    error = "不能排除整个磁盘或共享根目录，请选择具体文件夹。";
                    return false;
                }
                path = normalized;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) {
                error = "无效的文件夹路径。";
                return false;
            }
        }

        public static bool Matches(string filePath, IReadOnlyList<string> directories) {
            if (directories.Count == 0 || string.IsNullOrEmpty(filePath)) return false;
            if (OperatingSystem.IsWindows() && filePath.Contains('/'))
                filePath = filePath.Replace('/', '\\');
            StringComparison comparison = OperatingSystem.IsWindows() ?
                StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (string folder in directories) {
                if (string.IsNullOrWhiteSpace(folder) || filePath.Length <= folder.Length ||
                    !filePath.StartsWith(folder, comparison)) continue;
                if (filePath[folder.Length] is '/' or '\\')
                    return true;
            }
            return false;
        }
    }
}
