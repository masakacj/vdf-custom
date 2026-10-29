// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     This file is distributed under the same license as VideoDuplicateFinder.
// */

using System.Buffers;
using System.Security.Cryptography;

namespace VDF.GUI.Utils {
    internal sealed record FolderMergeFile(
        string SourcePath, string DestinationPath, long Bytes, DateTime LastWriteUtc, bool Renamed);

    internal sealed record FolderMergePlan(
        string SourceRoot, string TargetRoot, IReadOnlyList<FolderMergeFile> Files,
        IReadOnlyList<string> RelativeDirectories, long TotalBytes, int CollisionRenames) {
        public int SourceFolderCount => RelativeDirectories.Count;
        internal IReadOnlyList<(string OldPath, string NewPath)> Moves =>
            Files.Select(file => (file.SourcePath, file.DestinationPath)).ToArray();
    }

    internal sealed record FolderMergeOutcome(
        bool Committed, int Copied, int RemovedSources,
        IReadOnlyList<string> RemainingSources, IReadOnlyList<string> Errors);

    /// <summary>
    /// Merges all files recursively into another folder (contents, not nesting the root).
    /// First copy each file to a unique sibling temp, SHA-256 verify, and atomically install
    /// the new path; only after *all* copies and the durable DB journal succeed may any
    /// original be deleted. Even same-volume moves always use copy/verify/delete.
    /// No automatic overwrites, no following reparse points, and no whole-DB checkpoints.
    /// </summary>
    internal static class FolderMergeTransfer {
        internal const int MaxFiles = 30_000;
        static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        internal static bool ContainsPath(string root, string path) {
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string full = Path.GetFullPath(path);
            return full.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison)
                || (OperatingSystem.IsWindows() && full.StartsWith(parent + Path.AltDirectorySeparatorChar, PathComparison));
        }

        internal static (string Source, string Target) NormalizeAndValidate(string source, string target) {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)
                || !Path.IsPathFullyQualified(source.Trim().Trim('"'))
                || !Path.IsPathFullyQualified(target.Trim().Trim('"')))
                throw new ArgumentException("源和目标都必须是完整的绝对目录路径。");
            string from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Trim().Trim('"')));
            string to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Trim().Trim('"')));
            if (PathComparer.Equals(from, to) || ContainsPath(from, to) || ContainsPath(to, from))
                throw new InvalidOperationException("源目录和目标目录不能相同，也不能互为父子目录。");
            if (PathComparer.Equals(from, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(from)!)) ||
                PathComparer.Equals(to, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(to)!)))
                throw new InvalidOperationException("不能以磁盘根目录或共享根目录作为合并对象。");
            if (!Directory.Exists(from))
                throw new DirectoryNotFoundException($"源目录不存在：{from}");
            RejectReparseAncestors(from);
            RejectReparseAncestors(to);
            if (File.Exists(to))
                throw new IOException("目标路径已存在同名文件，不能用作文件夹。");
            return (from, to);
        }

        static void RejectReparseAncestors(string path) {
            DirectoryInfo? dir = new(path);
            for (; dir != null; dir = dir.Parent) {
                if (!dir.Exists) continue;
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"路径包含符号链接或联接目录，已停止：{dir.FullName}");
            }
        }

        internal static FolderMergePlan BuildPlan(string source, string target) {
            var (from, to) = NormalizeAndValidate(source, target);
            var stack = new Stack<string>();
            stack.Push(from);
            var files = new List<(string Path, long Bytes, DateTime Modified)>();
            var relativeFolders = new List<string>();
            while (stack.Count > 0) {
                string folder = stack.Pop();
                foreach (FileSystemInfo item in new DirectoryInfo(folder).EnumerateFileSystemInfos()) {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"源目录包含链接或联接点；为避免跳出目录范围，已停止：{item.FullName}");
                    if (item is DirectoryInfo) {
                        stack.Push(item.FullName);
                        relativeFolders.Add(Path.GetRelativePath(from, item.FullName));
                        if (relativeFolders.Count > MaxFiles * 2)
                            throw new IOException("子目录数量超出单次安全合并上限。");
                    }
                    else if (item is FileInfo info) {
                        if (files.Count >= MaxFiles)
                            throw new IOException($"本次源目录超过 {MaxFiles:N0} 个文件，请先缩小目录范围。");
                        files.Add((info.FullName, info.Length, info.LastWriteTimeUtc));
                    }
                }
            }
            // A source subdirectory cannot replace a target regular file. Preserve all
            // directories; do not overwrite an existing folder with a file either.
            foreach (string relative in relativeFolders) {
                string dir = Path.Combine(to, relative);
                if (File.Exists(dir))
                    throw new IOException($"目标中存在与源子目录同名的文件：{dir}");
                RejectReparseAncestors(dir);
            }

            long bytes = 0;
            int collisions = 0;
            var reserved = new HashSet<string>(PathComparer);
            var result = new List<FolderMergeFile>(files.Count);
            foreach (var file in files.OrderBy(x => x.Path, PathComparer)) {
                string relative = Path.GetRelativePath(from, file.Path);
                string destination = Path.Combine(to, relative);
                if (!ContainsPath(to, destination))
                    throw new IOException($"文件路径超出目标目录：{destination}");
                RejectReparseAncestors(Path.GetDirectoryName(destination)!);
                bool renamed = false;
                if (reserved.Contains(destination) || File.Exists(destination) || Directory.Exists(destination)) {
                    string dir = Path.GetDirectoryName(destination)!;
                    string stem = Path.GetFileNameWithoutExtension(destination);
                    string ext = Path.GetExtension(destination);
                    for (int suffix = 1; ; suffix++) {
                        string candidate = Path.Combine(dir, $"{stem} (VDF合并{suffix}){ext}");
                        if (reserved.Contains(candidate) || File.Exists(candidate) || Directory.Exists(candidate))
                            continue;
                        destination = candidate;
                        renamed = true;
                        collisions++;
                        break;
                    }
                }
                reserved.Add(destination);
                bytes = checked(bytes + file.Bytes);
                result.Add(new FolderMergeFile(file.Path, destination, file.Bytes, file.Modified, renamed));
            }
            return new FolderMergePlan(from, to, result, relativeFolders.ToArray(), bytes, collisions);
        }

        /// <summary>
        /// The callback must durably record DB moves BEFORE source deletion and return
        /// false on failure. The originals remain intact until it returns true.
        /// The returned list of moves is immutable for one execution.
        /// </summary>
        internal static async Task<FolderMergeOutcome> ExecuteAsync(
            FolderMergePlan plan,
            Func<IReadOnlyList<(string OldPath, string NewPath)>, (bool Success, string Error)> commit,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) {
            var installed = new List<(string Path, byte[] Hash)>();
            var errors = new List<string>();
            try {
                var current = NormalizeAndValidate(plan.SourceRoot, plan.TargetRoot);
                if (!PathComparer.Equals(current.Source, plan.SourceRoot) ||
                    !PathComparer.Equals(current.Target, plan.TargetRoot))
                    throw new IOException("目录已变化，必须重新生成预览。");
                foreach (FolderMergeFile item in plan.Files) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new FileInfo(item.SourcePath);
                    if (!info.Exists || info.Length != item.Bytes || info.LastWriteTimeUtc != item.LastWriteUtc)
                        throw new IOException($"源文件在预览后发生变化，请重新预览：{item.SourcePath}");
                    if (File.Exists(item.DestinationPath) || Directory.Exists(item.DestinationPath))
                        throw new IOException($"目标位置出现冲突，未覆盖：{item.DestinationPath}");
                    RejectReparseAncestors(Path.GetDirectoryName(item.DestinationPath)!);
                }

                for (int index = 0; index < plan.Files.Count; index++) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = plan.Files[index];
                    progress?.Report($"正在复制与 SHA-256 校验：{index + 1:N0}/{plan.Files.Count:N0}");
                    string? directory = Path.GetDirectoryName(item.DestinationPath);
                    if (directory == null) throw new IOException("目标文件缺少父目录。");
                    Directory.CreateDirectory(directory);
                    RejectReparseAncestors(directory);
                    if (File.Exists(item.DestinationPath) || Directory.Exists(item.DestinationPath))
                        throw new IOException($"复制期间目标文件出现冲突：{item.DestinationPath}");
                    string staged = item.DestinationPath + $".vdf-foldermerge-{Guid.NewGuid():N}.tmp";
                    try {
                        byte[] expected = await CopyAndHashAsync(item, staged, cancellationToken);
                        byte[] actual;
                        using (var stream = new FileStream(staged, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                            actual = await SHA256.HashDataAsync(stream, cancellationToken);
                        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                            throw new IOException($"复制后的 SHA-256 校验失败：{item.SourcePath}");
                        if (File.Exists(item.DestinationPath) || Directory.Exists(item.DestinationPath))
                            throw new IOException($"安装副本前目标位置出现冲突：{item.DestinationPath}");
                        File.SetLastWriteTimeUtc(staged, item.LastWriteUtc);
                        File.Move(staged, item.DestinationPath);
                        installed.Add((item.DestinationPath, expected));
                    }
                    finally {
                        try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                    }
                }
                // Preserve empty subdirectories too; never copy a directory over a file.
                foreach (string relativeDirectory in plan.RelativeDirectories) {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destinationDirectory = Path.Combine(plan.TargetRoot, relativeDirectory);
                    if (File.Exists(destinationDirectory))
                        throw new IOException($"目标中存在与源目录同名的文件：{destinationDirectory}");
                    Directory.CreateDirectory(destinationDirectory);
                    RejectReparseAncestors(destinationDirectory);
                }
                cancellationToken.ThrowIfCancellationRequested();

                // The journal updates all indexed file paths in one batch, without
                // rewriting a multi-GB ScannedFiles.db. Never delete sources before this.
                progress?.Report("全部副本已经校验，正在提交数据库路径更新…");
                var (success, error) = commit(plan.Moves);
                if (!success) throw new IOException($"数据库同步失败，未删除任何源文件：{error}");
            }
            catch (Exception ex) {
                errors.Add(ex.Message);
                // All source files still exist. Remove only copies created by us,
                // and only if their SHA still matches; never delete altered user data.
                foreach (var installedCopy in installed) {
                    try {
                        if (!File.Exists(installedCopy.Path)) continue;
                        byte[] actual;
                        using (var stream = File.OpenRead(installedCopy.Path)) actual = SHA256.HashData(stream);
                        if (CryptographicOperations.FixedTimeEquals(actual, installedCopy.Hash))
                            File.Delete(installedCopy.Path);
                        else errors.Add($"新副本内容已被修改，未自动清理：{installedCopy.Path}");
                    }
                    catch (Exception cleanupError) {
                        errors.Add($"清理已复制的文件失败：{installedCopy.Path}（{cleanupError.Message}）");
                    }
                }
                return new FolderMergeOutcome(false, installed.Count, 0,
                    plan.Files.Select(f => f.SourcePath).ToArray(), errors);
            }

            // Commit completed and every destination is present and verified.
            // A deletion failure leaves the extra original on disk, never data loss.
            var left = new List<string>();
            int removed = 0;
            foreach (FolderMergeFile item in plan.Files) {
                try {
                    var info = new FileInfo(item.SourcePath);
                    if (!info.Exists) { left.Add(item.SourcePath); errors.Add($"源文件意外消失：{item.SourcePath}"); continue; }
                    if (info.Length != item.Bytes || info.LastWriteTimeUtc != item.LastWriteUtc)
                        throw new IOException("删除前源文件已变化，已保留原文件");
                    File.Delete(item.SourcePath);
                    removed++;
                }
                catch (Exception ex) {
                    left.Add(item.SourcePath);
                    errors.Add($"未能清理源文件：{item.SourcePath}（{ex.Message}）");
                }
            }
            // Only remove directories that are EMPTY now; never recursive-delete.
            var folders = new HashSet<string>(PathComparer) { plan.SourceRoot };
            foreach (string relativeDirectory in plan.RelativeDirectories)
                folders.Add(Path.Combine(plan.SourceRoot, relativeDirectory));
            foreach (FolderMergeFile item in plan.Files) {
                string? dir = Path.GetDirectoryName(item.SourcePath);
                while (dir != null && ContainsPath(plan.SourceRoot, dir)) {
                    folders.Add(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            foreach (string dir in folders.OrderByDescending(s => s.Length)) {
                try {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception ex) { errors.Add($"空目录清理失败：{dir}（{ex.Message}）"); }
            }
            progress?.Report("文件夹合并完成");
            return new FolderMergeOutcome(true, installed.Count, removed, left, errors);
        }

        static async Task<byte[]> CopyAndHashAsync(FolderMergeFile item, string temp,
            CancellationToken cancellationToken) {
            using var source = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            long copied = 0;
            try {
                while (true) {
                    int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha.AppendData(buffer, 0, read);
                    copied = checked(copied + read);
                }
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
                if (copied != item.Bytes || source.Length != item.Bytes ||
                    File.GetLastWriteTimeUtc(item.SourcePath) != item.LastWriteUtc)
                    throw new IOException($"复制期间源文件发生变化：{item.SourcePath}");
                return sha.GetHashAndReset();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }
}
