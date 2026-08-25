// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using System.Security.Cryptography;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Core {
	public sealed partial class ScanEngine {
		const int ExactMiddleSampleBytes = 1024 * 1024;
		const int OsHashMinimumBytes = 64 * 1024;

		internal readonly record struct ExactQuickHash(string Sha256, bool CoversWholeFile);
		internal readonly record struct ExactDuplicatePlanStats(
			int EligibleFiles,
			int SizeCandidateFiles,
			int FullHashesComputed,
			int CachedFullHashesReused,
			int DuplicateGroups,
			int DuplicateFiles);

		/// <summary>
		/// Lightweight exact-content scan. It refreshes only the file index/database metadata,
		/// never probes media or decodes frames. Candidate reduction is: size -> cached/cheap
		/// head+tail OsHash -> 1 MiB middle sample (when useful) -> full-file SHA-256. Full SHA
		/// values are persisted on FileEntry and self-validate by size + mtime on later runs.
		/// </summary>
		public async void StartExactDuplicateScan() {
			try {
				PrepareExactDuplicateScan();
				SearchTimer.Start();
				ElapsedTimer.Start();
				Logger.Instance.BeginSession(T("Log.SessionExactDuplicates"));
				Logger.Instance.Info(T("Log.BuildingFileList"));
				await BuildFileList(cancelationTokenSource.Token);
				Logger.Instance.Info(T("Log.FinishedBuildingFileList", SearchTimer.StopGetElapsedAndRestart()));
				FilesEnumerated?.Invoke(this, EventArgs.Empty);

				ExactDuplicatePlanStats stats = default;
				if (!cancelationTokenSource.IsCancellationRequested)
					stats = await ScanForExactDuplicatesAsync(cancelationTokenSource.Token);

				if (cancelationTokenSource.IsCancellationRequested) {
					AbortScanOnError(new OperationCanceledException());
					return;
				}

				SearchTimer.Stop();
				ElapsedTimer.Stop();
				// Exact groups do not need media-quality highlighting: every byte is equal.
				// The GUI's BEST recommender selects a deterministic path-stable keeper.
				lock (checkpointLock)
					DatabaseUtils.SaveDatabase();
				Logger.Instance.Info(T("Log.ExactDuplicatesDone",
					stats.DuplicateGroups, stats.DuplicateFiles, stats.FullHashesComputed, stats.CachedFullHashesReused));
				isScanning = false;
				ScanDone?.Invoke(this, EventArgs.Empty);
				Logger.Instance.Info(T("Log.ScanDone"));
			}
			catch (Exception ex) {
				AbortScanOnError(ex);
			}
		}

		void PrepareExactDuplicateScan() {
			ResetExcludedLogging();
			CancelAllTasks();
			DatabaseUtils.CustomDatabaseFolder = Settings.CustomDatabaseFolder;
			DatabaseUtils.InvalidateDatabaseFolder();
			ScanCrashJournal.Initialize(DatabaseUtils.GetDatabaseFolderPath());
			Duplicates.Clear();
			ElapsedTimer.Reset();
			SearchTimer.Reset();
			NormalizeScanPaths();
			isScanning = true;
		}

		async Task<ExactDuplicatePlanStats> ScanForExactDuplicatesAsync(CancellationToken token) {
			List<FileEntry> eligible = DatabaseUtils.Database
				.Where(IsExactDuplicateEligible)
				.OrderBy(entry => entry.Path, CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
				.ToList();

			List<FileEntry> sizeCandidates = eligible
				.GroupBy(entry => entry.FileSize)
				.Where(group => group.Count() > 1)
				.SelectMany(group => group)
				.ToList();

			Logger.Instance.Info(T("Log.ExactDuplicatesCandidates", eligible.Count, sizeCandidates.Count));
			if (sizeCandidates.Count < 2) {
				Duplicates.Clear();
				return new ExactDuplicatePlanStats(eligible.Count, sizeCandidates.Count, 0, 0, 0, 0);
			}

			List<DriveScanGroup> allDriveGroups = DriveScanPlanner.PartitionByDrive(sizeCandidates);
			HddProtectionController? hddProtection = HddProtectionController.TryCreate(Settings, allDriveGroups);
			Volatile.Write(ref activeHddProtection, hddProtection);
			try {
				if (hddProtection != null)
					await hddProtection.StartAsync(token);

				// Backfill the cheap head/tail fingerprint only for files that actually have
				// a same-size peer. Tiny files intentionally skip OsHash and go straight to
				// the middle/full hash stages below.
				List<FileEntry> needOsHash = sizeCandidates
					.Where(entry => entry.FileSize >= OsHashMinimumBytes && entry.OsHash == null)
					.ToList();
				if (needOsHash.Count > 0) {
					await RunExactIoPhaseAsync(needOsHash, T("Scan.Stage.ExactFingerprint"), fullFileProgress: false,
						async (entry, ct) => {
							ct.ThrowIfCancellationRequested();
							entry.OsHash = OsHashUtils.TryCompute(entry.Path);
							await Task.CompletedTask;
						}, hddProtection, token);
				}

				List<List<FileEntry>> fingerprintGroups = BuildExactFingerprintGroups(sizeCandidates);

				var fullHashNeeded = new HashSet<FileEntry>();
				var groupsNeedingQuick = new List<List<FileEntry>>();
				int cachedReused = 0;

				foreach (List<FileEntry> group in fingerprintGroups) {
					int cached = group.Count(entry => entry.HasCurrentExactHash);
					cachedReused += cached;
					if (cached < group.Count)
						// Include cached peers too: reading only their middle 1 MiB lets a new
						// file be rejected before its expensive full-file SHA when it clearly
						// does not match the cached content.
						groupsNeedingQuick.Add(group);
				}

				if (groupsNeedingQuick.Count > 0) {
					List<FileEntry> quickHashNeeded = groupsNeedingQuick
						.SelectMany(group => group)
						.Distinct()
						.ToList();
					var quickHashes = new ConcurrentDictionary<FileEntry, ExactQuickHash>();
					await RunExactIoPhaseAsync(quickHashNeeded, T("Scan.Stage.ExactMiddleSample"), fullFileProgress: false,
						async (entry, ct) => {
							ExactQuickHash? hash = await ComputeExactMiddleHashAsync(entry, ct);
							if (hash is ExactQuickHash value) {
								quickHashes[entry] = value;
								if (value.CoversWholeFile && !entry.HasCurrentExactHash)
									entry.SetExactHash(value.Sha256);
							}
						}, hddProtection, token);

					foreach (List<FileEntry> group in groupsNeedingQuick) {
						// A quick-read failure is an optimization failure, never evidence that the
						// file differs. Fall back to full SHA for uncached members so we cannot
						// silently miss a true exact duplicate.
						if (group.Any(entry => !quickHashes.ContainsKey(entry))) {
							foreach (FileEntry entry in group.Where(entry => !entry.HasCurrentExactHash))
								fullHashNeeded.Add(entry);
							continue;
						}

						foreach (var quickGroup in group
							.GroupBy(entry => quickHashes[entry].Sha256, StringComparer.OrdinalIgnoreCase)
							.Where(hashGroup => hashGroup.Count() > 1)) {
							foreach (FileEntry entry in quickGroup.Where(entry => !entry.HasCurrentExactHash))
								fullHashNeeded.Add(entry);
						}
					}
				}

				int fullHashesComputed = 0;
				if (fullHashNeeded.Count > 0) {
					List<FileEntry> ordered = fullHashNeeded
						.OrderBy(entry => entry.Path, CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
						.ToList();
					await RunExactIoPhaseAsync(ordered, T("Scan.Stage.ExactFullHash"), fullFileProgress: true,
						async (entry, ct) => {
							string? sha = await ComputeExactSha256Async(entry, ct);
							if (sha != null) {
								entry.SetExactHash(sha);
								Interlocked.Increment(ref fullHashesComputed);
							}
						}, hddProtection, token);
				}

				BuildExactDuplicateResults(eligible);
				int groupCount = Duplicates.Select(item => item.GroupId).Distinct().Count();
				return new ExactDuplicatePlanStats(eligible.Count, sizeCandidates.Count,
					fullHashesComputed, cachedReused, groupCount, Duplicates.Count);
			}
			finally {
				driveProgressTracker = null;
				Volatile.Write(ref activeHddProtection, null);
				if (hddProtection != null)
					await hddProtection.DisposeAsync();
			}
		}

		/// <summary>
		/// Candidate groups after the cheap head/tail fingerprint. When every file of a
		/// same-size bucket has an OsHash, different fingerprints can be separated safely.
		/// If even one fingerprint is unavailable, keep the entire size bucket together and
		/// let the middle/full SHA stages decide — a transient quick-hash failure must never
		/// create a false negative.
		/// </summary>
		internal static List<List<FileEntry>> BuildExactFingerprintGroups(IReadOnlyList<FileEntry> sizeCandidates) {
			var result = new List<List<FileEntry>>();
			foreach (var sizeGroup in sizeCandidates.GroupBy(entry => entry.FileSize)) {
				List<FileEntry> members = sizeGroup.ToList();
				if (members.Count < 2)
					continue;
				if (members[0].FileSize < OsHashMinimumBytes || members.Any(entry => entry.OsHash == null)) {
					result.Add(members);
					continue;
				}
				foreach (var fingerprintGroup in members.GroupBy(entry => entry.OsHash!, StringComparer.OrdinalIgnoreCase)) {
					List<FileEntry> fingerprintMembers = fingerprintGroup.ToList();
					if (fingerprintMembers.Count > 1)
						result.Add(fingerprintMembers);
				}
			}
			return result;
		}

		bool IsExactDuplicateEligible(FileEntry entry) {
			if (!Settings.IncludeImages && entry.IsImage)
				return false;
			if (!Settings.ScanAgainstEntireDatabase && !IsInIncludeScope(entry))
				return false;
			if (entry.IsManuallyExcluded)
				return false;
			if (Settings.BlackList.Any(path => IsBlackListed(entry.Folder, path)))
				return false;
			if (!File.Exists(entry.Path))
				return false;
			if (Settings.FilterByFileSize && (entry.FileSize.BytesToMegaBytes() > Settings.MaximumFileSize ||
				entry.FileSize.BytesToMegaBytes() < Settings.MinimumFileSize))
				return false;
			if (Settings.FilterByFilePathContains && !Settings.FilePathContainsTexts.Any(pattern =>
				System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, entry.Path)))
				return false;
			if (Settings.FilterByFilePathNotContains && Settings.FilePathNotContainsTexts.Any(pattern =>
				System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, entry.Path)))
				return false;
			return true;
		}

		async Task RunExactIoPhaseAsync(
			IReadOnlyList<FileEntry> entries,
			string stage,
			bool fullFileProgress,
			Func<FileEntry, CancellationToken, Task> action,
			HddProtectionController? hddProtection,
			CancellationToken token) {
			if (entries.Count == 0)
				return;

			List<DriveScanGroup> groups = DriveScanPlanner.PartitionByDrive(entries);
			DriveScanPlanner.ClassifyGroups(groups, Settings.DriveTypeOverrides,
				DriveScanPlanner.IsNetworkRoot,
				group => DriveScanPlanner.ProbeSeekLatencyMs(group.Entries),
				DriveScanPlanner.QueryHasSeekPenalty);
			DriveScanPlanner.AssignParallelism(groups, Settings.MaxDegreeOfParallelism,
				Settings.HddMaxDegreeOfParallelism, Environment.ProcessorCount);
			Dictionary<string, int> mappings = Settings.EnableHddProtection
				? HddProtectionMappings.Parse(Settings.HddProtectionDriveMappings)
				: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			DriveScanPlanner.ApplyHddProtection(groups, mappings);

			InitProgress(entries.Count, stage);
			if (fullFileProgress)
				driveProgressTracker = new DriveProgressTracker(groups, _ => true, classified: true,
					hddProtection == null ? null : hddProtection.GetSnapshot);

			try {
				var driveTasks = new List<Task>(groups.Count);
				for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++) {
					DriveScanGroup group = groups[groupIndex];
					DriveProgressTracker.Counter? counter = fullFileProgress ? driveProgressTracker!.CounterFor(groupIndex) : null;
					driveTasks.Add(Parallel.ForEachAsync(group.Entries,
						new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = group.DegreeOfParallelism },
						async (entry, ct) => {
							if (!pauseTokenSource.TryWaitWhilePaused(ct))
								return;
							IDisposable? heavyRead = null;
							try {
								if (hddProtection != null)
									heavyRead = await hddProtection.EnterHeavyReadAsync([group.Root], ct);
								await action(entry, ct);
							}
							finally {
								try { heavyRead?.Dispose(); } catch { }
								counter?.Complete(Math.Max(0, entry.FileSize));
								IncrementProgress(entry.Path);
							}
						}));
				}
				await Task.WhenAll(driveTasks);
			}
			finally {
				if (fullFileProgress)
					driveProgressTracker = null;
			}
		}

		internal static async Task<ExactQuickHash?> ComputeExactMiddleHashAsync(FileEntry entry, CancellationToken token) {
			try {
				var before = new FileInfo(entry.Path);
				if (!before.Exists || before.Length != entry.FileSize || before.LastWriteTimeUtc != entry.DateModified)
					return null;

				long length = before.Length;
				int count = (int)Math.Min(length, ExactMiddleSampleBytes);
				byte[] buffer = new byte[count];
				await using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
					256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
				if (length > count)
					stream.Seek((length - count) / 2, SeekOrigin.Begin);
				int total = 0;
				while (total < count) {
					int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), token);
					if (read == 0)
						return null;
					total += read;
				}

				before.Refresh();
				if (!before.Exists || before.Length != entry.FileSize || before.LastWriteTimeUtc != entry.DateModified)
					return null;
				string sha = Convert.ToHexStringLower(SHA256.HashData(buffer));
				return new ExactQuickHash(sha, length <= ExactMiddleSampleBytes);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) {
				Logger.Instance.Warn($"Exact duplicate middle-sample hash failed for '{entry.Path}': {ex.Message}");
				return null;
			}
		}

		internal static async Task<string?> ComputeExactSha256Async(FileEntry entry, CancellationToken token) {
			try {
				var before = new FileInfo(entry.Path);
				if (!before.Exists || before.Length != entry.FileSize || before.LastWriteTimeUtc != entry.DateModified)
					return null;

				await using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
					1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
				byte[] hash = await SHA256.HashDataAsync(stream, token);
				before.Refresh();
				if (!before.Exists || before.Length != entry.FileSize || before.LastWriteTimeUtc != entry.DateModified)
					return null;
				return Convert.ToHexStringLower(hash);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) {
				Logger.Instance.Warn($"Exact duplicate SHA-256 failed for '{entry.Path}': {ex.Message}");
				return null;
			}
		}

		internal static List<List<FileEntry>> BuildExactHashGroups(IReadOnlyList<FileEntry> eligible) =>
			eligible
				.Where(entry => entry.HasCurrentExactHash)
				.GroupBy(entry => (entry.FileSize, entry.ExactSha256), ExactHashKeyComparer.Instance)
				.Where(group => group.Count() > 1)
				.Select(group => group
					.OrderBy(entry => entry.Path, CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
					.ToList())
				.ToList();

		void BuildExactDuplicateResults(IReadOnlyList<FileEntry> eligible) {
			var result = new HashSet<DuplicateItem>();
			foreach (List<FileEntry> hashGroup in BuildExactHashGroups(eligible)) {
				List<FileEntry> members = hashGroup;
				if (Settings.ExcludeHardLinks)
					members = RemoveHardLinkAliases(members);
				if (members.Count < 2)
					continue;

				Guid groupId = Guid.NewGuid();
				foreach (FileEntry entry in members)
					result.Add(new DuplicateItem(entry, 0f, groupId, DuplicateFlags.ByteIdentical));
			}
			Duplicates = result;
		}

		static List<FileEntry> RemoveHardLinkAliases(IReadOnlyList<FileEntry> entries) {
			var keep = new List<FileEntry>(entries.Count);
			foreach (FileEntry candidate in entries) {
				bool alias = false;
				foreach (FileEntry existing in keep) {
					if (HardLinkUtils.AreSameFile(existing.Path, candidate.Path)) {
						alias = true;
						break;
					}
				}
				if (!alias)
					keep.Add(candidate);
			}
			return keep;
		}

		sealed class ExactHashKeyComparer : IEqualityComparer<(long Size, string? Hash)> {
			internal static readonly ExactHashKeyComparer Instance = new();
			public bool Equals((long Size, string? Hash) x, (long Size, string? Hash) y) =>
				x.Size == y.Size && string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase);
			public int GetHashCode((long Size, string? Hash) obj) =>
				HashCode.Combine(obj.Size, obj.Hash?.ToUpperInvariant());
		}
	}
}
