// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using VDF.GUI.Utils;

namespace VDF.GUI.Tests {
	public class ThumbPackTests : IDisposable {
		readonly string dir;

		public ThumbPackTests() {
			dir = Path.Combine(Path.GetTempPath(), "vdf-thumbpack-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
		}

		public void Dispose() {
			try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
		}

		static void Append(ThumbPack pack, string key, byte[] payload) =>
			pack.AppendIfMissing(key, s => s.Write(payload, 0, payload.Length));

		static byte[] ReadKey(ThumbPack pack, string key) {
			using var s = pack.OpenKey(key)!;
			using var ms = new MemoryStream();
			s.CopyTo(ms);
			return ms.ToArray();
		}

		[Fact]
		public void AppendAndOpen_RoundTrips() {
			using var pack = OpenPack();
			Append(pack, "a", new byte[] { 1, 2, 3 });
			Append(pack, "b", new byte[] { 9, 8 });
			Assert.Equal(new byte[] { 1, 2, 3 }, ReadKey(pack, "a"));
			Assert.Equal(new byte[] { 9, 8 }, ReadKey(pack, "b"));
		}

		[Fact]
		public void EmptyWrite_IsNotRecorded_AndStaysRetryable() {
			using var pack = OpenPack();
			pack.AppendIfMissing("k", _ => { });
			Assert.False(pack.TryGetEntry("k", out _, out _));
			Append(pack, "k", new byte[] { 7 });
			Assert.Equal(new byte[] { 7 }, ReadKey(pack, "k"));
		}

		// Regression (Linux forum report, unresponsive results GUI): AppendIfMissing used
		// to run the JPEG encode callback while holding the pack lock, so any reader —
		// including the UI thread's Thumbnail getter — blocked for the duration of every
		// encode in the worker convoy. Readers must proceed while a writer is encoding.
		[Fact]
		public async Task Readers_AreNotBlocked_WhileAWriterEncodes() {
			using var pack = OpenPack();
			Append(pack, "seed", new byte[] { 1 });

			using var encodeStarted = new ManualResetEventSlim();
			using var releaseEncode = new ManualResetEventSlim();
			var writer = Task.Run(() => pack.AppendIfMissing("slow", s => {
				encodeStarted.Set();
				releaseEncode.Wait(TimeSpan.FromSeconds(10));
				s.WriteByte(42);
			}));
			Assert.True(encodeStarted.Wait(TimeSpan.FromSeconds(5)));

			// Do the read synchronously on the test thread. The previous Task.Run-based
			// version could false-fail on a busy/low-core CI runner when the ThreadPool
			// delayed scheduling the reader even though the pack lock was completely free.
			// If AppendIfMissing ever moves the encode back under _gate, this direct call
			// blocks until the writer's 10-second safety timeout and the elapsed assertion
			// fails deterministically.
			var sw = System.Diagnostics.Stopwatch.StartNew();
			bool found = pack.TryGetEntry("seed", out _, out _);
			sw.Stop();
			Assert.True(found);
			Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
				$"TryGetEntry blocked behind an in-progress encode for {sw.Elapsed}; the encode is running inside the pack lock again");

			releaseEncode.Set();
			await writer.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(new byte[] { 42 }, ReadKey(pack, "slow"));
		}

		[Fact]
		public async Task ConcurrentAppends_SameKey_OneEntryWins_AndIsReadable() {
			using var pack = OpenPack();
			using var gate = new ManualResetEventSlim();
			var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
				pack.AppendIfMissing("k", s => {
					gate.Wait(TimeSpan.FromSeconds(10));
					s.Write(new byte[] { (byte)i, (byte)i }, 0, 2);
				}))).ToArray();
			gate.Set();
			await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

			// All callers agree on the surviving entry, and it contains one full payload.
			var entries = tasks.Select(t => t.Result).Distinct().ToList();
			Assert.Single(entries);
			byte[] data = ReadKey(pack, "k");
			Assert.Equal(2, data.Length);
			Assert.Equal(data[0], data[1]);
		}

		// Regression: OpenKey created a private FileStream but handed it to the slice
		// with leaveOpen: true, so nothing ever closed it — one leaked file descriptor
		// per thumbnail load (fd exhaustion on Linux). Disposing the slice must release
		// the underlying handle; with a leaked handle the delete below fails on Windows.
		[Fact]
		public void OpenKey_DisposingSlice_ReleasesTheFileHandle() {
			string packPath = Path.Combine(dir, "thumbs.pack");
			using (var pack = OpenPack()) {
				Append(pack, "a", new byte[] { 1, 2, 3 });
				using (var slice = pack.OpenKey("a")) {
					Assert.NotNull(slice);
					slice!.ReadByte();
				}
			}
			File.Delete(packPath);
			Assert.False(File.Exists(packPath));
		}

		[Fact]
		public void SnapshotForExport_IsConsistent_AndExcludesLaterAppends() {
			using var pack = OpenPack();
			Append(pack, "a", new byte[] { 1, 2, 3 });
			var snap = pack.SnapshotForExport();
			Append(pack, "b", new byte[] { 4, 5, 6, 7 });

			using var ms = new MemoryStream();
			pack.CopyPackTo(ms, snap.PackLength);
			Assert.Equal(snap.PackLength, ms.Length);
			Assert.Equal(new byte[] { 1, 2, 3 }, ms.ToArray());

			var idx = System.Text.Json.JsonSerializer.Deserialize(
				snap.IndexJson, Data.GuiJsonFieldsContext.Default.ThumbPackIndex)!;
			Assert.True(idx.ContainsKey("a"));
			Assert.False(idx.ContainsKey("b"));
		}

		ThumbPack OpenPack() => ThumbPack.Open(dir);
	}
}