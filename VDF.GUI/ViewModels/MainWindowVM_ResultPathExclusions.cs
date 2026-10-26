// Result-only folder exclusions never delete or rewrite scan records.
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
    public partial class MainWindowVM {
        string[]? resultExclusionDirectoriesCache;

        // Keep expensive database traversals/checkpoints off the UI thread.
        // IsBusy prevents manual and scheduled scans from racing these operations.
        async Task<IReadOnlyList<string>> LoadManuallyExcludedFilesAsync() {
            if (IsBusy || IsScanning)
                throw new InvalidOperationException("正在扫描或执行其他操作，暂不能读取单文件排除。");
            IsBusy = true;
            IsBusyOverlayText = "正在读取历史单文件排除…";
            try {
                return await Task.Run(() => VDF.Core.ScanEngine.GetManuallyExcludedPaths());
            }
            finally { IsBusy = false; }
        }

        async Task<int> RestoreManuallyExcludedFilesAsync(IReadOnlyList<string> paths) {
            if (IsBusy || IsScanning)
                throw new InvalidOperationException("正在扫描或执行其他操作，暂不能修改扫描数据库。");
            IsBusy = true;
            IsBusyOverlayText = "正在解除单文件排除并保存扫描数据库…";
            try {
                return await Task.Run(() => VDF.Core.ScanEngine.RestoreManuallyExcludedFiles(paths));
            }
            finally { IsBusy = false; }
        }

        internal string[] ResultExclusionDirectories() =>
            resultExclusionDirectoriesCache ??= SettingsFile.Instance.ResultPathExclusions
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Path))
                .Select(rule => rule.Path).ToArray();

        internal string? TryAddResultPathExclusion(string? path, bool skipScan) {
            if (!ResultPathExclusion.TryNormalize(path, out string folder, out string error))
                return error;
            var rules = SettingsFile.Instance.ResultPathExclusions;
            ResultPathExclusion? existing = rules.FirstOrDefault(rule =>
                string.Equals(rule.Path, folder, OperatingSystem.IsWindows() ?
                    StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (existing != null) {
                if (!skipScan || existing.SkipScan) return null;
                existing.SkipScan = true;
            } else {
                rules.Add(new ResultPathExclusion { Path = folder, SkipScan = skipScan });
            }
            ResultPathExclusionsChanged();
            return null;
        }

        internal void ResultPathExclusionsChanged() {
            resultExclusionDirectoriesCache = null;
            lastCompletedPathFilter = null; // cached group hit list may include newly excluded paths
            SettingsFile.SaveSettings();
            RequestFilterResultsRefresh();
            RefreshResultsView();
        }
    }
}
