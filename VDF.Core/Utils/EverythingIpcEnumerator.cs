// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VDF.Core.Utils {
	/// <summary>
	/// Metadata returned together with an Everything IPC result. Size and modified date are
	/// indexed by Everything by default. Creation time / attributes are optional on purpose:
	/// requesting unindexed properties can make Everything itself touch every media file and
	/// would defeat the HDD-friendly enumeration fast path.
	/// </summary>
	internal sealed record EverythingIndexedFileMetadata(
		long? Length,
		DateTime? CreationTimeUtc,
		DateTime? LastWriteTimeUtc,
		FileAttributes? Attributes) {
		internal bool HasFastIdentity => Length.HasValue && LastWriteTimeUtc.HasValue;
	}

	internal sealed record EverythingEnumerationStats(
		int ResultCount,
		int FastMetadataCount,
		int Pages,
		TimeSpan Elapsed,
		string WindowClass);

	/// <summary>
	/// Windows-only Everything 1.4+ Query2 enumerator. It talks directly to Everything via
	/// WM_COPYDATA: no ES.exe and no SDK DLL are bundled or required.
	///
	/// Everything is only an accelerator. Missing IPC, network paths, strict folder-attribute
	/// filters, missing default metadata, a malformed/slow reply or an empty result all return
	/// false so FileUtils performs the original filesystem enumeration instead.
	/// </summary>
	internal static unsafe class EverythingIpcEnumerator {
		const string EverythingWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
		const string ReplyWindowClass = "VDF_CUSTOM_EVERYTHING_IPC_REPLY";
		const uint WM_COPYDATA = 0x004A;
		const uint PM_REMOVE = 0x0001;
		const uint SMTO_BLOCK = 0x0001;
		const uint SMTO_ABORTIFHUNG = 0x0002;
		const int ERROR_CLASS_ALREADY_EXISTS = 1410;
		const uint EVERYTHING_IPC_COPYDATA_QUERY2W = 18;
		const uint ReplyCopyDataMessage = 0x56444645; // "VDFE"
		const uint EVERYTHING_IPC_SORT_PATH_ASCENDING = 3;
		const uint REQUEST_FULL_PATH_AND_NAME = 0x00000004;
		const uint REQUEST_SIZE = 0x00000010;
		const uint REQUEST_DATE_CREATED = 0x00000020;
		const uint REQUEST_DATE_MODIFIED = 0x00000040;
		const uint REQUEST_ATTRIBUTES = 0x00000100;
		// Deliberately do NOT request DATE_CREATED or ATTRIBUTES here. Everything does not
		// index those by default and would gather them from the filesystem on demand.
		const uint RequestedFields = REQUEST_FULL_PATH_AND_NAME | REQUEST_SIZE | REQUEST_DATE_MODIFIED;
		const int Query2HeaderBytes = 7 * sizeof(uint);
		const int List2HeaderBytes = 5 * sizeof(uint);
		const int Item2Bytes = 2 * sizeof(uint);
		const uint PageSize = 50_000;
		static readonly TimeSpan PageReplyTimeout = TimeSpan.FromSeconds(12);

		static readonly ConditionalWeakTable<FileInfo, EverythingIndexedFileMetadata> indexedMetadata = new();
		static readonly ConcurrentDictionary<nint, QueryContext> queryContexts = new();
		static readonly object windowClassLock = new();
		static bool windowClassReady;

		internal static bool TryGetIndexedMetadata(FileInfo fileInfo, out EverythingIndexedFileMetadata metadata) =>
			indexedMetadata.TryGetValue(fileInfo, out metadata!);

		/// <summary>Pure helper used by tests and by the IPC path. Extensions may include a leading dot.</summary>
		internal static string BuildSearch(string initial, bool recursive, IEnumerable<string> extensions) {
			string path = NormalizeDirectory(initial);
			string scope = recursive ? "path" : "parent";
			string ext = string.Join(';', extensions
				.Select(x => x.Trim().TrimStart('.'))
				.Where(x => x.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase));
			// file: prevents a folder whose name ends in .mp4/.jpg from becoming a candidate.
			return ext.Length == 0
				? $"file: {scope}:\"{path}\""
				: $"file: {scope}:\"{path}\" ext:{ext}";
		}

		/// <summary>Pure scope guard: VDF does not trust search syntax alone to enforce the root.</summary>
		internal static bool IsPathInScope(string initial, string fullPath, bool recursive) {
			string root = NormalizeDirectory(initial);
			string? parent;
			try { parent = Path.GetDirectoryName(Path.GetFullPath(fullPath)); }
			catch { return false; }
			if (string.IsNullOrEmpty(parent)) return false;
			parent = NormalizeDirectory(parent);
			if (parent.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
			if (!recursive) return false;
			return StartsWithDirectory(parent, root);
		}

		/// <summary>
		/// Mirrors FileUtils' native blacklist rule across every ancestor below the scan root.
		/// A plain path excludes that exact folder/subtree; wildcard rules match folder full
		/// paths when they contain a separator, otherwise folder names.
		/// </summary>
		internal static bool IsExcludedByFolderRules(string initial, string fullPath, IReadOnlyList<string> excludeFolders) {
			if (excludeFolders.Count == 0) return false;
			string root = NormalizeDirectory(initial);
			string? current;
			try { current = Path.GetDirectoryName(Path.GetFullPath(fullPath)); }
			catch { return true; }
			while (!string.IsNullOrEmpty(current)) {
				current = NormalizeDirectory(current);
				if (current.Equals(root, StringComparison.OrdinalIgnoreCase))
					return false; // native walker does not blacklist the initial include itself
				if (!StartsWithDirectory(current, root))
					return false;
				foreach (string rule in excludeFolders) {
					if (string.IsNullOrWhiteSpace(rule)) continue;
					if (rule.IndexOfAny(['*', '?']) < 0) {
						if (current.Equals(NormalizeDirectory(rule), StringComparison.OrdinalIgnoreCase))
							return true;
						continue;
					}
					bool hasSeparator = rule.Contains(Path.DirectorySeparatorChar) || rule.Contains(Path.AltDirectorySeparatorChar);
					string value = hasSeparator ? current : Path.GetFileName(current);
					if (FileSystemName.MatchesSimpleExpression(rule, value, ignoreCase: true))
						return true;
				}
				string? next = Path.GetDirectoryName(current);
				if (string.IsNullOrEmpty(next) || next.Equals(current, StringComparison.OrdinalIgnoreCase))
					break;
				current = next;
			}
			return false;
		}

		internal static bool TryEnumerate(
			string initial,
			bool ignoreReadonly,
			bool ignoreReparsePoints,
			bool recursive,
			bool includeImages,
			IEnumerable<string> allowedExtensions,
			IReadOnlyList<string> excludeFolders,
			CancellationToken cancellationToken,
			out List<FileInfo> files,
			out EverythingEnumerationStats? stats,
			out string? fallbackReason) {
			files = new List<FileInfo>();
			stats = null;
			fallbackReason = null;

			if (!OperatingSystem.IsWindows()) {
				fallbackReason = "Everything IPC is Windows-only";
				return false;
			}
			// Do not trust an optional Everything Folder Index as the source of truth for a
			// NAS/mapped share. Native enumeration remains the safe path for network storage.
			if (IsNetworkPath(initial)) {
				fallbackReason = "network/UNC paths keep native enumeration so folder-index completeness is never assumed";
				return false;
			}
			// The native walker can skip whole folders based on these attributes. Query2 gives
			// result metadata, not every ancestor's attributes; preserve exact semantics.
			if (ignoreReadonly || ignoreReparsePoints) {
				fallbackReason = "strict read-only/reparse folder filtering requires the native walker";
				return false;
			}
			if (cancellationToken.IsCancellationRequested) {
				fallbackReason = "scan cancelled";
				return false;
			}

			nint everything = FindEverythingWindow(out string everythingClass);
			if (everything == 0) {
				fallbackReason = "Everything IPC window not found";
				return false;
			}
			if (!EnsureReplyWindowClass()) {
				fallbackReason = "could not register VDF Everything reply window";
				return false;
			}

			nint replyWindow = Native.CreateWindowExW(0, ReplyWindowClass, string.Empty, 0,
				0, 0, 0, 0, new nint(-3), 0, Native.GetModuleHandleW(null), 0); // HWND_MESSAGE
			if (replyWindow == 0) {
				fallbackReason = $"could not create VDF Everything reply window ({Marshal.GetLastWin32Error()})";
				return false;
			}

			var stopwatch = Stopwatch.StartNew();
			var context = new QueryContext();
			queryContexts[replyWindow] = context;
			try {
				string search = BuildSearch(initial, recursive, allowedExtensions);
				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				uint offset = 0;
				int pages = 0;
				while (true) {
					if (cancellationToken.IsCancellationRequested) {
						fallbackReason = "scan cancelled";
						return false;
					}
					context.ResetPage();
					if (!SendQueryPage(everything, replyWindow, search, offset, PageSize)) {
						fallbackReason = "Everything Query2 is unavailable, busy or unsupported";
						return false;
					}
					if (!WaitForReply(replyWindow, context, cancellationToken, PageReplyTimeout)) {
						fallbackReason = context.Error ?? "Everything IPC reply timed out";
						return false;
					}
					if (context.Error != null) {
						fallbackReason = context.Error;
						return false;
					}
					// Size + modified date are the default Everything property indexes. If a user
					// disabled them, fall back instead of making Everything gather them from disk.
					if ((context.AvailableRequestFlags & (REQUEST_SIZE | REQUEST_DATE_MODIFIED)) !=
						(REQUEST_SIZE | REQUEST_DATE_MODIFIED)) {
						fallbackReason = "Everything is not returning indexed size/date-modified metadata";
						return false;
					}
					pages++;

					foreach (EverythingRawResult result in context.PageResults) {
						if (!IsPathInScope(initial, result.FullPath, recursive)) continue;
						if (IsExcludedByFolderRules(initial, result.FullPath, excludeFolders)) continue;
						string extension;
						try { extension = Path.GetExtension(result.FullPath); }
						catch { continue; }
						if (!FileUtils.IsMediaExtension(extension)) continue;
						if (!includeImages && FileUtils.IsImageFile(result.FullPath)) continue;
						if (!seen.Add(result.FullPath)) continue;

						FileInfo fileInfo;
						try { fileInfo = new FileInfo(result.FullPath); }
						catch { continue; }
						indexedMetadata.Add(fileInfo, new EverythingIndexedFileMetadata(
							result.Size, result.CreationTimeUtc, result.LastWriteTimeUtc, result.Attributes));
						files.Add(fileInfo);
					}

					uint returned = context.ReturnedItems;
					uint total = context.TotalItems;
					if (returned == 0 || (ulong)offset + returned >= total)
						break;
					offset += returned;
				}

				// Empty is deliberately not trusted. A folder omitted/excluded from Everything's
				// index would also return zero; native enumeration verifies it instead.
				if (files.Count == 0) {
					fallbackReason = "Everything returned no media candidates; native enumeration will verify the folder";
					return false;
				}

				stopwatch.Stop();
				stats = new EverythingEnumerationStats(
					files.Count,
					files.Count(file => indexedMetadata.TryGetValue(file, out var m) && m.HasFastIdentity),
					pages,
					stopwatch.Elapsed,
					everythingClass);
				return true;
			}
			catch (Exception ex) {
				fallbackReason = $"Everything IPC error: {ex.Message}";
				return false;
			}
			finally {
				queryContexts.TryRemove(replyWindow, out _);
				Native.DestroyWindow(replyWindow);
			}
		}

		static bool SendQueryPage(nint everything, nint replyWindow, string search, uint offset, uint maxResults) {
			byte[] searchBytes = Encoding.Unicode.GetBytes(search + '\0');
			int size = checked(Query2HeaderBytes + searchBytes.Length);
			nint query = Marshal.AllocHGlobal(size);
			try {
				var span = new Span<byte>((void*)query, size);
				span.Clear();
				BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], unchecked((uint)(nuint)replyWindow));
				BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], ReplyCopyDataMessage);
				BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], 0); // use normal Everything search syntax
				BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], offset);
				BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], maxResults);
				BinaryPrimitives.WriteUInt32LittleEndian(span[20..24], RequestedFields);
				BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], EVERYTHING_IPC_SORT_PATH_ASCENDING);
				searchBytes.AsSpan().CopyTo(span[Query2HeaderBytes..]);

				var cds = new CopyDataStruct {
					dwData = EVERYTHING_IPC_COPYDATA_QUERY2W,
					cbData = checked((uint)size),
					lpData = query,
				};
				nuint result;
				nint ok = Native.SendMessageTimeoutW(everything, WM_COPYDATA, (nuint)replyWindow,
					ref cds, SMTO_BLOCK | SMTO_ABORTIFHUNG, 2_000, out result);
				return ok != 0 && result != 0;
			}
			finally {
				Marshal.FreeHGlobal(query);
			}
		}

		static bool WaitForReply(nint replyWindow, QueryContext context, CancellationToken token, TimeSpan timeout) {
			var sw = Stopwatch.StartNew();
			while (!context.Completed && sw.Elapsed < timeout && !token.IsCancellationRequested) {
				while (Native.PeekMessageW(out Message message, replyWindow, 0, 0, PM_REMOVE)) {
					Native.TranslateMessage(ref message);
					Native.DispatchMessageW(ref message);
				}
				if (!context.Completed)
					Thread.Sleep(2);
			}
			return context.Completed;
		}

		static nint FindEverythingWindow(out string windowClass) {
			windowClass = EverythingWindowClass;
			nint direct = Native.FindWindowW(EverythingWindowClass, null);
			if (direct != 0) return direct;

			var state = new EnumWindowState();
			GCHandle handle = GCHandle.Alloc(state);
			try {
				nint callback = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&EnumWindowsProc;
				Native.EnumWindows(callback, GCHandle.ToIntPtr(handle));
			}
			finally {
				handle.Free();
			}
			if (state.Found != 0)
				windowClass = state.ClassName;
			return state.Found;
		}

		[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
		static int EnumWindowsProc(nint hwnd, nint lParam) {
			try {
				var state = (EnumWindowState?)GCHandle.FromIntPtr(lParam).Target;
				if (state == null) return 0;
				char* chars = stackalloc char[256];
				int length = Native.GetClassNameW(hwnd, chars, 256);
				if (length <= 0) return 1;
				string cls = new(chars, 0, length);
				// Named instances use the same base class plus an instance suffix. Prefix
				// matching supports those without asking users to install/run ES.exe.
				if (!cls.StartsWith(EverythingWindowClass, StringComparison.OrdinalIgnoreCase)) return 1;
				state.Found = hwnd;
				state.ClassName = cls;
				return 0;
			}
			catch {
				return 1;
			}
		}

		static bool EnsureReplyWindowClass() {
			lock (windowClassLock) {
				if (windowClassReady) return true;
				fixed (char* className = ReplyWindowClass) {
					var wc = new WndClassEx {
						cbSize = (uint)sizeof(WndClassEx),
						lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc,
						hInstance = Native.GetModuleHandleW(null),
						lpszClassName = (nint)className,
					};
					ushort atom = Native.RegisterClassExW(ref wc);
					if (atom == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
						return false;
				}
				windowClassReady = true;
				return true;
			}
		}

		[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
		static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam) {
			try {
				if (message == WM_COPYDATA && queryContexts.TryGetValue(hwnd, out QueryContext? context)) {
					CopyDataStruct cds = *(CopyDataStruct*)lParam;
					if ((uint)cds.dwData == ReplyCopyDataMessage) {
						try {
							ParseReply(cds.lpData, cds.cbData, context);
							return 1;
						}
						catch (Exception ex) {
							context.Error = $"invalid Everything IPC reply: {ex.Message}";
							context.Completed = true;
							return 0;
						}
					}
				}
			}
			catch { /* never let an exception cross the unmanaged window-proc boundary */ }
			return Native.DefWindowProcW(hwnd, message, wParam, lParam);
		}

		static void ParseReply(nint data, uint dataBytes, QueryContext context) {
			if (data == 0 || dataBytes < List2HeaderBytes || dataBytes > int.MaxValue)
				throw new InvalidDataException("reply is too small or too large");
			var span = new ReadOnlySpan<byte>((void*)data, checked((int)dataBytes));
			uint totalItems = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]);
			uint numItems = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);
			uint requestFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[12..16]);
			if ((requestFlags & REQUEST_FULL_PATH_AND_NAME) == 0)
				throw new InvalidDataException("Everything did not return full paths");
			long itemTableEnd = List2HeaderBytes + (long)numItems * Item2Bytes;
			if (itemTableEnd > span.Length)
				throw new InvalidDataException("item table exceeds reply buffer");

			var page = new List<EverythingRawResult>(checked((int)numItems));
			for (uint i = 0; i < numItems; i++) {
				int itemOffset = checked(List2HeaderBytes + (int)i * Item2Bytes);
				uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(itemOffset + 4, 4));
				if (dataOffset < itemTableEnd || dataOffset >= span.Length)
					throw new InvalidDataException("result data offset is outside the reply buffer");
				int cursor = checked((int)dataOffset);

				string fullPath = ReadString(span, ref cursor);
				long? size = null;
				DateTime? created = null;
				DateTime? modified = null;
				FileAttributes? attributes = null;
				if ((requestFlags & REQUEST_SIZE) != 0)
					size = checked((long)ReadUInt64(span, ref cursor));
				if ((requestFlags & REQUEST_DATE_CREATED) != 0)
					created = FileTimeToUtc(ReadUInt64(span, ref cursor));
				if ((requestFlags & REQUEST_DATE_MODIFIED) != 0)
					modified = FileTimeToUtc(ReadUInt64(span, ref cursor));
				if ((requestFlags & REQUEST_ATTRIBUTES) != 0)
					attributes = (FileAttributes)ReadUInt32(span, ref cursor);
				if (!string.IsNullOrWhiteSpace(fullPath))
					page.Add(new EverythingRawResult(fullPath, size, created, modified, attributes));
			}

			context.TotalItems = totalItems;
			context.ReturnedItems = numItems;
			context.AvailableRequestFlags = requestFlags;
			context.PageResults = page;
			context.Completed = true;
		}

		static string ReadString(ReadOnlySpan<byte> data, ref int cursor) {
			uint charCount = ReadUInt32(data, ref cursor);
			long byteCountLong = (long)charCount * 2;
			if (byteCountLong > int.MaxValue || cursor + byteCountLong + 2 > data.Length)
				throw new InvalidDataException("string exceeds reply buffer");
			int byteCount = (int)byteCountLong;
			string value = Encoding.Unicode.GetString(data.Slice(cursor, byteCount));
			cursor += byteCount + 2; // trailing UTF-16 NUL
			return value;
		}

		static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor) {
			if (cursor < 0 || cursor + 4 > data.Length) throw new InvalidDataException("truncated uint32");
			uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor, 4));
			cursor += 4;
			return value;
		}

		static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int cursor) {
			if (cursor < 0 || cursor + 8 > data.Length) throw new InvalidDataException("truncated uint64");
			ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor, 8));
			cursor += 8;
			return value;
		}

		static DateTime? FileTimeToUtc(ulong value) {
			if (value == 0 || value > long.MaxValue) return null;
			try { return DateTime.FromFileTimeUtc((long)value); }
			catch { return null; }
		}

		static bool IsNetworkPath(string path) {
			if (path.StartsWith("\\\\", StringComparison.Ordinal)) return true;
			try {
				string? root = Path.GetPathRoot(Path.GetFullPath(path));
				if (string.IsNullOrEmpty(root)) return false;
				return new DriveInfo(root).DriveType == DriveType.Network;
			}
			catch {
				return false;
			}
		}

		static string NormalizeDirectory(string path) {
			try {
				string full = Path.GetFullPath(path);
				string? root = Path.GetPathRoot(full);
				if (!string.IsNullOrEmpty(root) && full.Equals(root, StringComparison.OrdinalIgnoreCase))
					return root;
				return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			}
			catch {
				return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			}
		}

		static bool StartsWithDirectory(string path, string directory) {
			if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) return false;
			if (path.Length == directory.Length) return true;
			char last = directory[^1];
			if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar) return true;
			char next = path[directory.Length];
			return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
		}

		sealed class QueryContext {
			internal bool Completed;
			internal string? Error;
			internal uint TotalItems;
			internal uint ReturnedItems;
			internal uint AvailableRequestFlags;
			internal List<EverythingRawResult> PageResults = new();
			internal void ResetPage() {
				Completed = false;
				Error = null;
				TotalItems = 0;
				ReturnedItems = 0;
				AvailableRequestFlags = 0;
				PageResults = new List<EverythingRawResult>();
			}
		}

		sealed class EnumWindowState {
			internal nint Found;
			internal string ClassName = string.Empty;
		}

		sealed record EverythingRawResult(
			string FullPath,
			long? Size,
			DateTime? CreationTimeUtc,
			DateTime? LastWriteTimeUtc,
			FileAttributes? Attributes);

		[StructLayout(LayoutKind.Sequential)]
		struct WndClassEx {
			internal uint cbSize;
			internal uint style;
			internal nint lpfnWndProc;
			internal int cbClsExtra;
			internal int cbWndExtra;
			internal nint hInstance;
			internal nint hIcon;
			internal nint hCursor;
			internal nint hbrBackground;
			internal nint lpszMenuName;
			internal nint lpszClassName;
			internal nint hIconSm;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct CopyDataStruct {
			internal nuint dwData;
			internal uint cbData;
			internal nint lpData;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct Message {
			internal nint hwnd;
			internal uint message;
			internal nuint wParam;
			internal nint lParam;
			internal uint time;
			internal Point pt;
			internal uint lPrivate;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct Point { internal int x; internal int y; }

		static class Native {
			[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
			internal static extern nint FindWindowW(string lpClassName, string? lpWindowName);
			[DllImport("user32.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			internal static extern bool EnumWindows(nint lpEnumFunc, nint lParam);
			[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
			internal static extern int GetClassNameW(nint hWnd, char* lpClassName, int nMaxCount);
			[DllImport("user32.dll", SetLastError = true)]
			internal static extern ushort RegisterClassExW(ref WndClassEx lpwcx);
			[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
			internal static extern nint CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName,
				uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
			[DllImport("user32.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			internal static extern bool DestroyWindow(nint hWnd);
			[DllImport("user32.dll", SetLastError = true)]
			internal static extern nint SendMessageTimeoutW(nint hWnd, uint Msg, nuint wParam, ref CopyDataStruct lParam,
				uint fuFlags, uint uTimeout, out nuint lpdwResult);
			[DllImport("user32.dll")]
			[return: MarshalAs(UnmanagedType.Bool)]
			internal static extern bool PeekMessageW(out Message lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
			[DllImport("user32.dll")]
			[return: MarshalAs(UnmanagedType.Bool)]
			internal static extern bool TranslateMessage(ref Message lpMsg);
			[DllImport("user32.dll")]
			internal static extern nint DispatchMessageW(ref Message lpMsg);
			[DllImport("user32.dll")]
			internal static extern nint DefWindowProcW(nint hWnd, uint Msg, nuint wParam, nint lParam);
			[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
			internal static extern nint GetModuleHandleW(string? lpModuleName);
		}
	}
}
