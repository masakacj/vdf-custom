// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Linq;
using VDF.Core.Utils;

namespace VDF.Core {
    public sealed partial class ScanEngine {
        /// <summary>
        /// Path-only O(number of transferred files) preflight, not O(total database size).
        /// Existing database entries at destination are NEVER overwritten, even when
        /// the physical file disappeared (tombstones must not be silently hijacked).
        /// </summary>
        public static bool ValidateFolderMergeDatabaseMoves(
            IReadOnlyList<(string OldPath, string NewPath)> moves, out string error) {
            lock (DatabaseUtils.Database)
                return TryValidateFolderMergeMoves(DatabaseUtils.Database, moves, out _, out error);
        }

        /// <summary>
        /// Apply indexed moves as one transaction, journaling all changes durably before
        /// the GUI deletes any source file. Non-indexed files can still be copied.
        /// </summary>
        public static bool CommitFolderMergeDatabaseMoves(
            IReadOnlyList<(string OldPath, string NewPath)> moves, out string error) {
            lock (DatabaseUtils.Database)
                return TryApplyFolderMergeMoves(DatabaseUtils.Database, moves, journalMoves => {
                    if (PersistInteractiveDatabaseMoves(journalMoves, out string journalError))
                        return true;
                    Logger.Instance.Warn(
                        $"Folder merge journal failed; falling back to full database checkpoint: {journalError}");
                    DatabaseUtils.SaveDatabase();
                    return true;
                }, out error);
        }

        internal static bool TryValidateFolderMergeMoves(
            HashSet<FileEntry> entries, IReadOnlyList<(string OldPath, string NewPath)> moves,
            out List<(FileEntry Entry, string OldPath, string NewPath)> indexed, out string error) {
            indexed = new();
            error = string.Empty;
            var comparer = CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var sources = new HashSet<string>(comparer);
            var destinations = new HashSet<string>(comparer);
            try {
                foreach (var move in moves) {
                    string oldPath = Path.GetFullPath(move.OldPath);
                    string newPath = Path.GetFullPath(move.NewPath);
                    if (comparer.Equals(oldPath, newPath) ||
                        !sources.Add(oldPath) || !destinations.Add(newPath))
                        throw new InvalidOperationException("源与目标路径重复，不能安全写入扫描数据库。");
                    if (entries.TryGetValue(new FileEntry { Path = newPath }, out _))
                        throw new InvalidOperationException($"数据库中的目标路径已被其他记录占用：{newPath}");
                    if (entries.TryGetValue(new FileEntry { Path = oldPath }, out FileEntry? entry)
                        && entry != null)
                        indexed.Add((entry, oldPath, newPath));
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; indexed.Clear(); return false; }
        }

        internal static bool TryApplyFolderMergeMoves(
            HashSet<FileEntry> entries, IReadOnlyList<(string OldPath, string NewPath)> moves,
            Func<IReadOnlyList<(string OldPath, string NewPath)>, bool> persist,
            out string error) {
            if (!TryValidateFolderMergeMoves(entries, moves, out var indexed, out error))
                return false;
            if (indexed.Count == 0) { error = string.Empty; return true; }

            var removed = new List<(FileEntry Entry, string Original)>();
            try {
                foreach (var (entry, oldPath, _) in indexed) {
                    if (!entries.Remove(entry)) throw new IOException($"扫描记录未找到：{oldPath}");
                    removed.Add((entry, entry.Path));
                }
                foreach (var (entry, _, dest) in indexed) {
                    entry.Path = dest;
                    if (!entries.Add(entry))
                        throw new InvalidOperationException($"数据库路径发生冲突：{dest}");
                }
                var indexedMoves = indexed.Select(x => (x.OldPath, x.NewPath)).ToArray();
                if (!persist(indexedMoves))
                    throw new IOException("无法持久记录文件夹合并的数据库路径变化");
                error = string.Empty;
                return true;
            }
            catch (Exception ex) {
                foreach (var (entry, _) in removed)
                    entries.Remove(entry);
                foreach (var (entry, original) in removed) {
                    entry.Path = original;
                    entries.Add(entry);
                }
                error = ex.Message;
                return false;
            }
        }
    }
}
