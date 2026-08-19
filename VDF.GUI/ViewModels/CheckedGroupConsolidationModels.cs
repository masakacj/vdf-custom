// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

namespace VDF.GUI.ViewModels {
	public sealed record CheckedGroupConsolidationDialogResult(
		DuplicateItemVM Keeper,
		string DestinationFolder,
		string DestinationPath);
}
