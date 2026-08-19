// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
// */

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// One folder-merge group that has a system BEST recommendation but does not pass the
	/// unattended confidence gate. The preview dialog lets the user accept/change it inline.
	/// </summary>
	public sealed record ResourceSeriesManualReview(
		Guid GroupId,
		IReadOnlyList<DuplicateItemVM> Candidates,
		DuplicateItemVM RecommendedKeeper,
		string RecommendationReason);

	/// <summary>
	/// One folder-merge group whose keeper passed the strict unattended BEST confidence gate.
	/// These rows are shown in the collapsed automatic section of the merge preview so the
	/// exact keeper and preselected loser/deletion set remain inspectable before execution.
	/// </summary>
	public sealed record ResourceSeriesConfirmedReview(
		Guid GroupId,
		IReadOnlyList<DuplicateItemVM> Candidates,
		DuplicateItemVM ConfirmedKeeper,
		string ConfirmationReason);
}
