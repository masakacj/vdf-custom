using System.Diagnostics;

namespace VDF.Updater;

/// <summary>
/// Runs from the newly downloaded updater in the temporary payload directory. It waits
/// for the old updater process to exit, then overlays release files onto the installation.
/// It never mirrors or purges the target directory, so Settings.json, databases, logs and
/// scan-result files that are not part of the release package are preserved.
/// </summary>
internal static class StandaloneInstaller {
    internal const string ApplyUpdateSwitch = "--apply-update";

    internal static int? TryHandle(string[] args) {
        if (args.Length == 0 || !args[0].Equals(ApplyUpdateSwitch, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!OperatingSystem.IsWindows())
            return 20;

        var positional = args.Skip(1).Where(x => !x.Equals("--no-pause", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (positional.Length != 4 || !int.TryParse(positional[0], out int oldPid) || oldPid <= 0)
            return 21;

        string sourceRoot = positional[1];
        string targetRoot = positional[2];
        string tempRoot = positional[3];
        try {
            Console.WriteLine("等待旧更新器退出...");
            WaitForProcessExit(oldPid, TimeSpan.FromMinutes(2));
            Console.WriteLine("正在覆盖 VDF 程序文件...");
            CopyPayloadWithRetry(sourceRoot, targetRoot, TimeSpan.FromSeconds(60));

            string installedGui = Path.Combine(targetRoot, "VDF.GUI.exe");
            string installedUpdater = Path.Combine(targetRoot, "VDF.Updater.exe");
            if (!File.Exists(installedGui))
                throw new FileNotFoundException("更新后未找到 VDF.GUI.exe。", installedGui);
            if (!File.Exists(installedUpdater))
                throw new FileNotFoundException("更新后未找到 VDF.Updater.exe。", installedUpdater);

            Console.WriteLine("更新完成。主程序不会自动启动。");
            ScheduleCleanup(tempRoot);
            return 0;
        }
        catch (Exception ex) {
            WriteError(targetRoot, tempRoot, ex);
            Console.Error.WriteLine();
            Console.Error.WriteLine("更新失败：" + ex.Message);
            Console.Error.WriteLine($"详细错误已写入：{Path.Combine(targetRoot, "update-error.txt")}");
            return 22;
        }
    }

    internal static void WaitForProcessExit(int pid, TimeSpan timeout) {
        try {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
                return;
            if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
                throw new TimeoutException($"等待旧更新器进程 {pid} 退出超时。");
        }
        catch (ArgumentException) {
            // Already gone.
        }
    }

    internal static void CopyPayloadWithRetry(string sourceRoot, string targetRoot, TimeSpan timeout) {
        string source = Path.GetFullPath(sourceRoot);
        string target = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(target);

        DateTime deadline = DateTime.UtcNow + timeout;
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            while (true) {
                try {
                    File.Copy(file, destination, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && DateTime.UtcNow < deadline) {
                    Thread.Sleep(500);
                }
            }
        }
    }

    static void WriteError(string targetRoot, string tempRoot, Exception ex) {
        string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{ex}";
        try {
            Directory.CreateDirectory(targetRoot);
            File.WriteAllText(Path.Combine(targetRoot, "update-error.txt"), text);
        }
        catch { }
        try {
            Directory.CreateDirectory(tempRoot);
            File.WriteAllText(Path.Combine(tempRoot, "update-error.txt"), text);
        }
        catch { }
    }

    static void ScheduleCleanup(string tempRoot) {
        try {
            var psi = new ProcessStartInfo {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{tempRoot}\"");
            Process.Start(psi)?.Dispose();
        }
        catch { }
    }
}
