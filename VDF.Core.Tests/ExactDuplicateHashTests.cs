// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

using MemoryPack;
using VDF.Core.Utils;

namespace VDF.Core.Tests;

public sealed class ExactDuplicateHashTests : IDisposable {
	readonly string root = Directory.CreateTempSubdirectory("vdf-exact-duplicates-").FullName;

	public void Dispose() {
		try { Directory.Delete(root, recursive: true); } catch { }
	}

	FileEntry Write(string name, byte[] bytes) {
		string path = Path.Combine(root, name);
		File.WriteAllBytes(path, bytes);
		return new FileEntry(new FileInfo(path));
	}

	static byte[] Pattern(int length, byte seed) {
		var bytes = new byte[length];
		for (int i = 0; i < bytes.Length; i++)
			bytes[i] = unchecked((byte)(seed + i * 31));
		return bytes;
	}

	[Fact]
	public void ExactHashCache_IsCurrentOnlyForSameSizeAndModifiedTime() {
		var entry = new FileEntry {
			FileSize = 1234,
			DateModified = new DateTime(2026, 8, 25, 1, 2, 3, DateTimeKind.Utc),
		};
		entry.SetExactHash(new string('a', 64));

		Assert.True(entry.HasCurrentExactHash);
		entry.FileSize++;
		Assert.False(entry.HasCurrentExactHash);
		entry.FileSize--;
		Assert.True(entry.HasCurrentExactHash);
		entry.DateModified = entry.DateModified.AddSeconds(1);
		Assert.False(entry.HasCurrentExactHash);
	}

	[Fact]
	public async Task FullSha256_SameBytesMatch_AndMiddleChangeDoesNot() {
		byte[] original = Pattern(3 * 1024 * 1024, 17);
		byte[] changed = (byte[])original.Clone();
		changed[changed.Length / 2] ^= 0x7f;
		FileEntry a = Write("a.bin", original);
		FileEntry b = Write("b.bin", original);
		FileEntry c = Write("c.bin", changed);

		string? ha = await ScanEngine.ComputeExactSha256Async(a, CancellationToken.None);
		string? hb = await ScanEngine.ComputeExactSha256Async(b, CancellationToken.None);
		string? hc = await ScanEngine.ComputeExactSha256Async(c, CancellationToken.None);

		Assert.NotNull(ha);
		Assert.Equal(ha, hb);
		Assert.NotEqual(ha, hc);
	}

	[Fact]
	public async Task MiddleSample_SeparatesSameHeadTailButDifferentMiddle() {
		byte[] aBytes = Pattern(4 * 1024 * 1024, 23);
		byte[] bBytes = (byte[])aBytes.Clone();
		// Keep the first/last 64 KiB identical so the existing OsHash remains equal,
		// while changing content squarely inside the 1 MiB middle sample.
		for (int i = bBytes.Length / 2 - 4096; i < bBytes.Length / 2 + 4096; i++)
			bBytes[i] ^= 0x55;
		FileEntry a = Write("middle-a.bin", aBytes);
		FileEntry b = Write("middle-b.bin", bBytes);

		Assert.Equal(OsHashUtils.TryCompute(a.Path), OsHashUtils.TryCompute(b.Path));
		ScanEngine.ExactQuickHash? qa = await ScanEngine.ComputeExactMiddleHashAsync(a, CancellationToken.None);
		ScanEngine.ExactQuickHash? qb = await ScanEngine.ComputeExactMiddleHashAsync(b, CancellationToken.None);

		Assert.NotNull(qa);
		Assert.NotNull(qb);
		Assert.False(qa!.Value.CoversWholeFile);
		Assert.False(qb!.Value.CoversWholeFile);
		Assert.NotEqual(qa.Value.Sha256, qb.Value.Sha256);
	}

	[Fact]
	public async Task SmallFile_MiddlePassCoversWholeFileAndEqualsFullSha() {
		FileEntry entry = Write("small.bin", Pattern(128 * 1024, 9));
		ScanEngine.ExactQuickHash? quick = await ScanEngine.ComputeExactMiddleHashAsync(entry, CancellationToken.None);
		string? full = await ScanEngine.ComputeExactSha256Async(entry, CancellationToken.None);

		Assert.NotNull(quick);
		Assert.True(quick!.Value.CoversWholeFile);
		Assert.Equal(full, quick.Value.Sha256);
	}

	[Fact]
	public void FingerprintGrouping_DoesNotDropBucketWhenOneOsHashIsUnavailable() {
		var a = new FileEntry { FileSize = 2_000_000, OsHash = "aaa" };
		a.Path = Path.Combine(root, "group-a.mp4");
		var b = new FileEntry { FileSize = 2_000_000, OsHash = null };
		b.Path = Path.Combine(root, "group-b.mp4");
		var c = new FileEntry { FileSize = 2_000_000, OsHash = "bbb" };
		c.Path = Path.Combine(root, "group-c.mp4");

		List<List<FileEntry>> groups = ScanEngine.BuildExactFingerprintGroups(new[] { a, b, c });

		List<FileEntry> group = Assert.Single(groups);
		Assert.Equal(3, group.Count);
	}

	[Fact]
	public void ExactHashGroups_RequireCurrentFullHashAndMatchSizePlusSha() {
		DateTime stamp = new(2026, 8, 25, 4, 5, 6, DateTimeKind.Utc);
		FileEntry Entry(string name, long size, string hash, bool current = true) {
			var e = new FileEntry { FileSize = size, DateModified = stamp };
			e.Path = Path.Combine(root, name);
			e.SetExactHash(hash);
			if (!current)
				e.DateModified = stamp.AddSeconds(1);
			return e;
		}
		string hashA = new('a', 64);
		var a = Entry("a.mp4", 1000, hashA);
		var b = Entry("b.mp4", 1000, hashA);
		var stale = Entry("stale.mp4", 1000, hashA, current: false);
		var otherSize = Entry("other-size.mp4", 1001, hashA);

		List<List<FileEntry>> groups = ScanEngine.BuildExactHashGroups(new[] { a, b, stale, otherSize });

		List<FileEntry> exact = Assert.Single(groups);
		Assert.Equal(2, exact.Count);
		Assert.Contains(a, exact);
		Assert.Contains(b, exact);
	}

	[Fact]
	public void ExactHashFields_RoundTripThroughVersionTolerantDatabaseSchema() {
		var entry = new FileEntry {
			FileSize = 987654,
			DateModified = new DateTime(2026, 8, 25, 7, 8, 9, DateTimeKind.Utc),
		};
		entry.Path = Path.Combine(root, "roundtrip.mp4");
		entry.SetExactHash("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

		byte[] payload = MemoryPackSerializer.Serialize(entry);
		FileEntry? restored = MemoryPackSerializer.Deserialize<FileEntry>(payload);

		Assert.NotNull(restored);
		Assert.Equal(entry.ExactSha256, restored!.ExactSha256);
		Assert.Equal(entry.ExactHashFileSize, restored.ExactHashFileSize);
		Assert.Equal(entry.ExactHashDateModified, restored.ExactHashDateModified);
		Assert.True(restored.HasCurrentExactHash);
	}
}
