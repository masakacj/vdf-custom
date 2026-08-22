using System.Diagnostics;

namespace VDF.Updater;

internal static class Program {
    static async Task<int> Main(string[] args) {
        bool noPause = args.Any(x => x.Equals("--no-pause", StringComparison.OrdinalIgnoreCase));

        int? installerResult = StandaloneInstaller.TryHandle(args);
        if (installerResult != null) {
            PauseIfNeeded(noPause);
            return installerResult.Value;
        }

        if (args.Any(x => x is "-h" or "--help" or "/?")) {
            PrintHelp();
            return 0;
        }

        if (!OperatingSystem.IsWindows()) {
            Console.Error.WriteLine("VDF.Updater 目前只支持 Windows。");
            PauseIfNeeded(noPause);
            return 10;
        }

        try {
            string targetFolder = ResolveTargetFolder(args);
            string guiExe = Path.Combine(targetFolder, "VDF.GUI.exe");
            if (!File.Exists(guiExe))
                throw new FileNotFoundException("目标目录中没有 VDF.GUI.exe。", guiExe);
            EnsureDirectoryWritable(targetFolder);

            await WaitForGuiToCloseAsync(guiExe, TimeSpan.FromMinutes(10));

            Version installedVersion = ReleaseUpdateClient.ReadExecutableVersion(guiExe)
                ?? throw new InvalidDataException("无法读取当前 VDF.GUI.exe 的版本号。");
            Console.WriteLine($"VDF 目录：{targetFolder}");
            Console.WriteLine($"当前版本：v{ReleaseUpdateClient.FormatVersion(installedVersion)}");
            Console.WriteLine("正在检查 GitHub 最新版本...");

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, e) => {
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try {
                ReleaseInfo latest = await ReleaseUpdateClient.GetLatestAsync(cts.Token);
                bool force = args.Any(x => x.Equals("--force", StringComparison.OrdinalIgnoreCase));
                if (!force && latest.Version.CompareTo(installedVersion) <= 0) {
                    Console.WriteLine($"已经是最新版 v{ReleaseUpdateClient.FormatVersion(installedVersion)}。");
                    PauseIfNeeded(noPause);
                    return 0;
                }

                Console.WriteLine($"最新版本：{latest.Tag}");
                Console.WriteLine($"更新包：{latest.AssetName} ({FormatBytes(latest.AssetSize)})");
                Console.WriteLine($"开始下载（服务器支持时自动使用 {ReleaseUpdateClient.ParallelSegmentCount} 路并发分段）...");

                int lastPercent = -1;
                PreparedUpdate prepared = await ReleaseUpdateClient.DownloadAndPrepareAsync(
                    latest,
                    (done, total) => {
                        if (total is > 0) {
                            int percent = (int)Math.Clamp(done * 100 / total.Value, 0, 100);
                            if (percent == lastPercent)
                                return;
                            lastPercent = percent;
                            Console.Write($"\r下载进度：{percent,3}%  {FormatBytes(done)} / {FormatBytes(total.Value)}   ");
                        }
                        else {
                            Console.Write($"\r已下载：{FormatBytes(done)}   ");
                        }
                    },
                    cts.Token);
                Console.WriteLine();
                Console.WriteLine("SHA-256 校验与解压完成，准备覆盖程序文件...");

                LaunchApplyWorker(prepared, targetFolder, noPause);
                // Do not pause here. The freshly downloaded updater inherits this console,
                // waits for this process to exit, applies the payload, and prints the result.
                return 0;
            }
            finally {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        catch (OperationCanceledException) {
            Console.Error.WriteLine();
            Console.Error.WriteLine("更新已取消。");
            PauseIfNeeded(noPause);
            return 11;
        }
        catch (Exception ex) {
            Console.Error.WriteLine();
            Console.Error.WriteLine("更新失败：" + ex.Message);
            PauseIfNeeded(noPause);
            return 12;
        }
    }

    static string ResolveTargetFolder(string[] args) {
        for (int i = 0; i < args.Length; i++) {
            if (!args[i].Equals("--target", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("--target 后必须提供 VDF 程序目录。");
            return Path.GetFullPath(args[i + 1].Trim().Trim('"'));
        }

        string updaterFolder = Path.GetDirectoryName(Environment.ProcessPath)
            ?? Environment.CurrentDirectory;
        if (File.Exists(Path.Combine(updaterFolder, "VDF.GUI.exe")))
            return Path.GetFullPath(updaterFolder);

        Console.WriteLine("当前目录没有找到 VDF.GUI.exe。");
        Console.Write("请输入 VDF 程序目录：");
        string? input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("未提供 VDF 程序目录。");
        return Path.GetFullPath(input.Trim().Trim('"'));
    }

    static async Task WaitForGuiToCloseAsync(string guiExe, TimeSpan timeout) {
        DateTime deadline = DateTime.UtcNow + timeout;
        bool notified = false;
        while (TryFindRunningGui(guiExe, out Process? process)) {
            using (process) {
                if (!notified) {
                    Console.WriteLine("检测到 VDF.GUI.exe 正在运行。请先在主程序中正常保存并关闭；更新器会自动继续。");
                    notified = true;
                }
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("等待 VDF.GUI.exe 关闭超时。");
                int waitMs = (int)Math.Min(1000, Math.Max(1, remaining.TotalMilliseconds));
                try {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(waitMs));
                }
                catch (TimeoutException) { }
            }
        }
    }

    static bool TryFindRunningGui(string guiExe, out Process? matching) {
        matching = null;
        string expected = Path.GetFullPath(guiExe);
        foreach (Process process in Process.GetProcessesByName("VDF.GUI")) {
            try {
                string? path = process.MainModule?.FileName;
                if (path != null && Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) {
                    matching = process;
                    return true;
                }
            }
            catch {
                // A process we cannot inspect is not assumed to be this installation.
            }
            if (matching != process)
                process.Dispose();
        }
        return false;
    }

    static void EnsureDirectoryWritable(string folder) {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(folder);
        string test = Path.Combine(folder, ".vdf_updater_write_test_" + Guid.NewGuid().ToString("N"));
        try {
            using var stream = new FileStream(test, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception ex) {
            throw new UnauthorizedAccessException($"VDF 目录不可写：{folder}。请把程序放在普通可写目录，或以有权限的账号运行更新器。", ex);
        }
        finally {
            try { if (File.Exists(test)) File.Delete(test); } catch { }
        }
    }

    static void LaunchApplyWorker(PreparedUpdate prepared, string targetFolder, bool noPause) {
        string updaterExe = Path.Combine(prepared.PayloadRoot, "VDF.Updater.exe");
        var psi = new ProcessStartInfo {
            FileName = updaterExe,
            UseShellExecute = false,
            WorkingDirectory = prepared.PayloadRoot,
        };
        psi.ArgumentList.Add(StandaloneInstaller.ApplyUpdateSwitch);
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(prepared.PayloadRoot);
        psi.ArgumentList.Add(targetFolder);
        psi.ArgumentList.Add(prepared.TempRoot);
        if (noPause)
            psi.ArgumentList.Add("--no-pause");
        Process worker = Process.Start(psi) ?? throw new InvalidOperationException("无法启动临时 VDF.Updater.exe 更新进程。");
        worker.Dispose();
    }

    static string FormatBytes(long bytes) {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    static void PauseIfNeeded(bool noPause) {
        if (noPause || Console.IsInputRedirected)
            return;
        Console.WriteLine();
        Console.Write("按 Enter 退出...");
        try { Console.ReadLine(); } catch { }
    }

    static void PrintHelp() {
        Console.WriteLine("VDF.Updater - VDF Custom 独立更新器");
        Console.WriteLine();
        Console.WriteLine("直接双击：更新与 VDF.Updater.exe 位于同一目录的 VDF。");
        Console.WriteLine("VDF.Updater.exe --target <目录>   更新指定 VDF 目录");
        Console.WriteLine("VDF.Updater.exe --force           即使已经是最新版也重新安装");
        Console.WriteLine("VDF.Updater.exe --no-pause        命令完成后不等待 Enter");
        Console.WriteLine();
        Console.WriteLine("更新不会自动启动 VDF.GUI.exe，也不会删除 Settings.json、数据库、日志或扫描结果。");
    }
}
