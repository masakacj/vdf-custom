// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder (GPLv3-or-later).
// */

using VDF.Core.Utils;

namespace VDF.GUI {
    /// <summary>
    /// Recover from a native graphics fault that terminates the process before the main
    /// window opens. A native AccessViolation cannot be caught by a C# try/catch, so
    /// the NEXT launch selects the Win32 software rendering backend instead.
    /// Deliberately never touches the scan DB, journal, or scan-result backups.
    /// </summary>
    internal static class WindowsStartupRenderRecovery {
        const string MarkerName = "software-rendering-fallback.txt";
        const string TraceName = "startup-trace.txt";

        internal static bool PreviousLaunchDiedBeforeWindow(string trace) {
            int start = trace.LastIndexOf("App.Initialize begin", StringComparison.Ordinal);
            if (start < 0) return false;
            string latest = trace[start..];
            return latest.Contains("Framework initialization completed", StringComparison.Ordinal) &&
                   !latest.Contains("MainWindow Opened", StringComparison.Ordinal);
        }

        internal static bool IsRecentFailure(DateTime lastWriteUtc, DateTime nowUtc) =>
            lastWriteUtc <= nowUtc && lastWriteUtc >= nowUtc.AddDays(-14);

        internal static bool SelectRenderingMode(bool forceSoftware, bool forceHardware, out string reason) {
            if (!OperatingSystem.IsWindows()) {
                reason = "default non-Windows";
                return false;
            }
            if (forceHardware) {
                reason = "hardware (explicit override)";
                return false;
            }
            if (forceSoftware) {
                reason = "software (explicit override)";
                return true;
            }

            try {
                string folder = CoreUtils.SettingsFolder;
                string marker = Path.Combine(folder, MarkerName);
                if (File.Exists(marker)) {
                    reason = "software (previous startup recovery)";
                    return true;
                }

                string tracePath = Path.Combine(folder, TraceName);
                if (File.Exists(tracePath) &&
                    IsRecentFailure(File.GetLastWriteTimeUtc(tracePath), DateTime.UtcNow) &&
                    PreviousLaunchDiedBeforeWindow(File.ReadAllText(tracePath))) {
                    // Remain on the safe backend after it successfully opens; otherwise
                    // the app would alternate between one working and one crashing start.
                    // The user can explicitly test hardware via --hardware-rendering.
                    try {
                        File.WriteAllText(marker,
                            $"Automatic software rendering enabled {DateTime.UtcNow:O} after a pre-window startup failure.\n" +
                            "Use --hardware-rendering for a one-time GPU startup test.\n");
                    }
                    catch { /* Failure to persist must not prevent recovery now. */ }
                    reason = "software (detected previous pre-window crash)";
                    return true;
                }
            }
            catch { /* A damaged/missing trace must never block a regular launch. */ }

            reason = "default hardware (with Avalonia fallback)";
            return false;
        }
    }
}
