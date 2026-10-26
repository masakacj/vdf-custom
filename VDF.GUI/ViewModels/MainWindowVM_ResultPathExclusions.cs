// Result-only folder exclusions never delete or rewrite scan records.
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
    public partial class MainWindowVM {
        string[]? resultExclusionDirectoriesCache;

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
