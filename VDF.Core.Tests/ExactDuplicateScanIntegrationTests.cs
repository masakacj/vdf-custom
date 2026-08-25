// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using VDF.Core.Utils;

namespace VDF.Core.Tests;

[Collection("DatabaseUtils")]
public sealed class ExactDuplicateScanIntegrationTests : IDisposable {
	readonly string root = Directory.CreateTempSubdirectory("vdf-exact-scan-").FullName;
	readonly string dbDir;

	public ExactDuplicateScanIntegrationTests() {
		dbDir = Path.Combine(root, "db");
		Directory.CreateDirectory(dbDir);
		DatabaseUtils.Database.Clear();
	}

	public void Dispose() {
		DatabaseUtils.Database.Clear();
		DatabaseUtils.CustomDatabaseFolder = null;
		DatabaseUtils.InvalidateDatabaseFolder();
		try { Directory.Delete(root, recursive: true); } catch { }
	}

	static byte[] Pattern(int length, byte seed) {
		var bytes = new byte[length];
		for (int i = 0; i < bytes.Length; i++)
			bytes[i] = unchecked((byte)(seed + i * 17));
		return bytes;
	}

	string Write(string name, byte[] bytes) {
		string path = Path.Combine(root, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	ScanEngine Engine() {
		var engine = new ScanEngine();
		engine.Settings.IncludeList.Add(root);
		engine.Settings.BlackList.Add(dbDir);
		engine.Settings.IncludeSubDirectories = false;
		engine.Settings.IncludeImages = false;
		engine.Settings.CustomDatabaseFolder = dbDir;
		engine.Settings.EnableHddProtection = false;
		engine.Settings.MaxDegreeOfParallelism = 4;
		engine.Settings.HddMaxDegreeOfParallelism = 1;
		return engine;
	}

	static async Task RunExactAsync(ScanEngine engine) {
		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		engine.ScanDone += (_, _) => completion.TrySetResult(true);
		engine.ScanAborted += (_, _) => completion.TrySetResult(false);
		engine.StartExactDuplicateScan();
		bool succeeded = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.True(succeeded, "Exact duplicate scan aborted unexpectedly.");
	}

	[Fact]
	public async Task EndToEnd_FindsOnlyByteIdenticalPair_AndReusesPersistedHashes() {
		byte[] same = Pattern(3 * 1024 * 1024, 31);
		byte[] middleChanged = (byte[])same.Clone();
		for (int i = middleChanged.Length / 2 - 8192; i < middleChanged.Length / 2 + 8192; i++)
			middleChanged[i] ^= 0x33;
		string aPath = Write("a.mp4", same);
		string bPath = Write("b.mp4", same);
		string cPath = Write("same-size-different-middle.mp4", middleChanged);
		Write("unique-size.mp4", Pattern(2 * 1024 * 1024, 77));

		ScanEngine first = Engine();
		await RunExactAsync(first);

		Assert.Equal(2, first.Duplicates.Count);
		Assert.Single(first.Duplicates.Select(item => item.GroupId).Distinct());
		Assert.All(first.Duplicates, item => Assert.True(item.Flags.HasFlag(DuplicateFlags.ByteIdentical)));
		Assert.Contains(first.Duplicates, item => item.Path == aPath);
		Assert.Contains(first.Duplicates, item => item.Path == bPath);
		Assert.DoesNotContain(first.Duplicates, item => item.Path == cPath);

		FileEntry a = Assert.Single(DatabaseUtils.Database, entry => entry.Path == aPath);
		FileEntry b = Assert.Single(DatabaseUtils.Database, entry => entry.Path == bPath);
		FileEntry c = Assert.Single(DatabaseUtils.Database, entry => entry.Path == cPath);
		Assert.True(a.HasCurrentExactHash);
		Assert.True(b.HasCurrentExactHash);
		Assert.Equal(a.ExactSha256, b.ExactSha256);
		Assert.False(c.HasCurrentExactHash); // rejected by the 1 MiB middle sample before full SHA

		string persistedHash = a.ExactSha256!;
		DatabaseUtils.Database.Clear(); // prove the second pass reloads the persisted exact hash
		DatabaseUtils.InvalidateDatabaseFolder();

		ScanEngine second = Engine();
		await RunExactAsync(second);

		Assert.Equal(2, second.Duplicates.Count);
		FileEntry reloadedA = Assert.Single(DatabaseUtils.Database, entry => entry.Path == aPath);
		FileEntry reloadedB = Assert.Single(DatabaseUtils.Database, entry => entry.Path == bPath);
		Assert.Equal(persistedHash, reloadedA.ExactSha256);
		Assert.Equal(persistedHash, reloadedB.ExactSha256);
		Assert.True(reloadedA.HasCurrentExactHash);
		Assert.True(reloadedB.HasCurrentExactHash);
	}
}
