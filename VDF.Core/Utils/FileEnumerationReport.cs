// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

namespace VDF.Core.Utils {
	public enum FileEnumerationBackend {
		Detecting = 0,
		EverythingIpc = 1,
		NativeFileSystem = 2,
	}

	/// <summary>
	/// One include-root's file-enumeration state. Frontends can show which backend is
	/// actually in use instead of making users infer it from scan speed or log output.
	/// </summary>
	public sealed record FileEnumerationReport(
		string RootPath,
		FileEnumerationBackend Backend,
		bool IsCompleted,
		int FileCount,
		TimeSpan Elapsed,
		string Detail);
}
