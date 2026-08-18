// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using VDF.GUI.Data;

namespace VDF.GUI.Tests {
	public class ScanProfileMapperTests {

		[Fact]
		public void FreshDefaults_AreTheRecommendedQualityConsolidationProfile() {
			var settings = new SettingsFile();
			Assert.Equal(ScanProfile.EditedAndAltered, ScanProfileMapper.Detect(settings));
		}

		[Fact]
		public void ExactlyThreeProfiles_AreUserFacing() {
			var visible = Enum.GetValues<ScanProfile>().Where(ScanProfileMapper.IsUserFacing).ToArray();
			Assert.Equal(new[] { ScanProfile.ExactAndNear, ScanProfile.EditedAndAltered, ScanProfile.AiScan }, visible);
		}

		[Fact]
		public void ApplyPreciseDedupe_SetsEveryManagedKnob() {
			var settings = new SettingsFile();
			ScanProfileMapper.Apply(ScanProfile.ExactAndNear, settings);

			Assert.Equal(98f, settings.Percent);
			Assert.False(settings.CompareHorizontallyFlipped);
			Assert.False(settings.IgnoreBlackPixels);
			Assert.False(settings.IgnoreWhitePixels);
			Assert.False(settings.EnablePartialClipDetection);
			Assert.False(settings.UseAiMatching);
			Assert.False(settings.EnableAiPartialDetection);
			Assert.Equal(ScanProfile.ExactAndNear, ScanProfileMapper.Detect(settings));
		}

		[Fact]
		public void QualityConsolidation_HasNoPartialDetection() {
			var settings = new SettingsFile();
			ScanProfileMapper.Apply(ScanProfile.EditedAndAltered, settings);

			Assert.Equal(92f, settings.Percent);
			Assert.True(settings.CompareHorizontallyFlipped);
			Assert.True(settings.IgnoreBlackPixels);
			Assert.True(settings.IgnoreWhitePixels);
			Assert.False(settings.EnablePartialClipDetection);
			Assert.False(settings.UseAiMatching);
			Assert.False(settings.EnableAiPartialDetection);
		}

		[Fact]
		public void DeepSameSource_UsesAiButNeverPartialDetection() {
			var settings = new SettingsFile();
			ScanProfileMapper.Apply(ScanProfile.AiScan, settings);

			Assert.True(settings.UseAiMatching);
			Assert.False(settings.EnableAiPartialDetection);
			Assert.False(settings.EnablePartialClipDetection);
			Assert.Equal(ScanProfile.AiScan, ScanProfileMapper.Detect(settings));

			settings.UseAiMatching = false;
			Assert.Equal(ScanProfile.EditedAndAltered, ScanProfileMapper.Detect(settings));
		}

		[Fact]
		public void LegacyDeepClean_RemainsDetectableButIsNotUserFacing() {
			var settings = new SettingsFile();
			ScanProfileMapper.Apply(ScanProfile.DeepClean, settings);

			Assert.True(settings.EnablePartialClipDetection);
			Assert.True(settings.UseAiMatching);
			Assert.True(settings.EnableAiPartialDetection);
			Assert.Equal(ScanProfile.DeepClean, ScanProfileMapper.Detect(settings));
			Assert.False(ScanProfileMapper.IsUserFacing(ScanProfile.DeepClean));
		}

		[Fact]
		public void ToggleManagedKnob_SwitchesDetectionToCustomSentinel() {
			var settings = new SettingsFile();
			ScanProfileMapper.Apply(ScanProfile.EditedAndAltered, settings);

			settings.UseAiMatching = true;
			settings.EnableAiPartialDetection = true;
			Assert.Equal(ScanProfile.Custom, ScanProfileMapper.Detect(settings));
		}

		[Fact]
		public void LegacyAudioWithoutAi_DetectsAsCustom() {
			var settings = new SettingsFile();
			settings.Percent = 92f;
			settings.CompareHorizontallyFlipped = true;
			settings.IgnoreBlackPixels = true;
			settings.IgnoreWhitePixels = true;
			settings.EnablePartialClipDetection = true;
			Assert.Equal(ScanProfile.Custom, ScanProfileMapper.Detect(settings));
		}

		[Fact]
		public void EditingAnyManagedKnob_SwitchesDetectionToCustom() {
			var settings = new SettingsFile();
			ScanProfileMapper.Apply(ScanProfile.EditedAndAltered, settings);

			settings.Percent = 85f;
			Assert.Equal(ScanProfile.Custom, ScanProfileMapper.Detect(settings));
		}

		[Fact]
		public void LeavingCustom_SnapshotsKnobs_AndCustomRestoresThem() {
			var settings = new SettingsFile();
			settings.Percent = 85f;
			settings.CompareHorizontallyFlipped = false;
			settings.IgnoreBlackPixels = false;
			settings.IgnoreWhitePixels = true;
			settings.EnablePartialClipDetection = true;
			Assert.Equal(ScanProfile.Custom, ScanProfileMapper.Detect(settings));

			ScanProfileMapper.Apply(ScanProfile.ExactAndNear, settings);
			Assert.Equal(ScanProfile.ExactAndNear, ScanProfileMapper.Detect(settings));
			Assert.NotNull(settings.CustomScanKnobs);

			ScanProfileMapper.Apply(ScanProfile.Custom, settings);
			Assert.Equal(85f, settings.Percent);
			Assert.False(settings.CompareHorizontallyFlipped);
			Assert.False(settings.IgnoreBlackPixels);
			Assert.True(settings.IgnoreWhitePixels);
			Assert.True(settings.EnablePartialClipDetection);
			Assert.Equal(ScanProfile.Custom, ScanProfileMapper.Detect(settings));
		}

		[Fact]
		public void SwitchingBetweenBundles_DoesNotOverwriteTheCustomSnapshot() {
			var settings = new SettingsFile();
			settings.Percent = 85f;
			ScanProfileMapper.Apply(ScanProfile.ExactAndNear, settings);
			ScanProfileMapper.Apply(ScanProfile.AiScan, settings);

			ScanProfileMapper.Apply(ScanProfile.Custom, settings);
			Assert.Equal(85f, settings.Percent);
		}

		[Fact]
		public void ApplyCustom_WithoutSnapshot_LeavesSettingsUntouched() {
			var settings = new SettingsFile();
			ScanProfileMapper.Apply(ScanProfile.Custom, settings);
			Assert.Equal(ScanProfile.EditedAndAltered, ScanProfileMapper.Detect(settings));
		}
	}
}
