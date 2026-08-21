// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.Core.Utils;

namespace VDF.Core.Tests;

public class HddProtectionTests {
	sealed class FakeTemperatureSource : IDiskTemperatureSource {
		internal IReadOnlyDictionary<int, int> Values { get; set; } = new Dictionary<int, int>();
		internal Exception? Error { get; set; }
		public Task<IReadOnlyDictionary<int, int>> GetTemperaturesAsync(IReadOnlyCollection<int> diskSlots, CancellationToken token) {
			if (Error != null)
				throw Error;
			return Task.FromResult(Values);
		}
	}

	static FileEntry Entry(string path) {
		var entry = new FileEntry { Folder = Path.GetDirectoryName(path) ?? string.Empty, FileSize = 1 };
		entry._Path = path;
		return entry;
	}

	[Fact]
	public void MappingParser_AcceptsDriveRootsAndMultipleSeparators() {
		Dictionary<string, int> mappings = HddProtectionMappings.Parse("y:=2; Z:\\=3\nX:=4");
		Assert.Equal(3, mappings.Count);
		Assert.Equal(2, mappings["Y:"]);
		Assert.Equal(3, mappings["Z:"]);
		Assert.Equal(4, mappings["X:"]);
		Assert.True(HddProtectionMappings.TryGetSlot(mappings, @"y:\", out int slot));
		Assert.Equal(2, slot);
	}

	[Fact]
	public void ApplyProtection_OnlyMappedDriveIsSerialAndPathSorted() {
		var y = new DriveScanGroup(@"Y:\") { SpeedClass = DriveSpeedClass.Fast, DegreeOfParallelism = 8 };
		y.Entries.Add(Entry(@"Y:\movies\z.mp4"));
		y.Entries.Add(Entry(@"Y:\movies\a.mp4"));
		var x = new DriveScanGroup(@"X:\") { SpeedClass = DriveSpeedClass.Fast, DegreeOfParallelism = 8 };
		x.Entries.Add(Entry(@"X:\a.mp4"));

		DriveScanPlanner.ApplyHddProtection(new[] { y, x }, HddProtectionMappings.Parse("Y:=2"));

		Assert.Equal(1, y.DegreeOfParallelism);
		Assert.Equal(DriveSpeedClass.Slow, y.SpeedClass);
		Assert.Contains("Disk 2", y.ClassSource);
		Assert.Equal(@"Y:\movies\a.mp4", y.Entries[0].Path);
		Assert.Equal(8, x.DegreeOfParallelism);
		Assert.Equal(DriveSpeedClass.Fast, x.SpeedClass);
	}

	[Fact]
	public async Task TemperatureGate_PausesAndRequiresCooldownPlusTwoLowPolls() {
		var source = new FakeTemperatureSource { Values = new Dictionary<int, int> { [2] = 49 } };
		await using var controller = new HddProtectionController(
			new Dictionary<string, int> { ["Y:"] = 2 }, source,
			warnTemperatureC: 48, pauseTemperatureC: 50, resumeTemperatureC: 45,
			minimumCooldown: TimeSpan.FromMinutes(5), resumeConsecutivePolls: 2,
			pollInterval: TimeSpan.FromMinutes(1));
		DateTime t0 = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

		await controller.PollOnceAsync(CancellationToken.None, t0);
		HddProtectionSnapshot safe = controller.GetSnapshot(@"Y:\")!.Value;
		Assert.False(safe.IsBlocked);
		Assert.True(safe.IsWarm);
		Assert.Equal(49, safe.TemperatureC);

		source.Values = new Dictionary<int, int> { [2] = 50 };
		await controller.PollOnceAsync(CancellationToken.None, t0.AddMinutes(1));
		Assert.True(controller.GetSnapshot(@"Y:\")!.Value.IsCooling);
		Assert.True(controller.GetSnapshot(@"Y:\")!.Value.IsBlocked);

		source.Values = new Dictionary<int, int> { [2] = 45 };
		await controller.PollOnceAsync(CancellationToken.None, t0.AddMinutes(3));
		Assert.True(controller.GetSnapshot(@"Y:\")!.Value.IsBlocked); // minimum cooldown not met

		await controller.PollOnceAsync(CancellationToken.None, t0.AddMinutes(6));
		Assert.True(controller.GetSnapshot(@"Y:\")!.Value.IsBlocked); // first qualifying low poll

		source.Values = new Dictionary<int, int> { [2] = 44 };
		await controller.PollOnceAsync(CancellationToken.None, t0.AddMinutes(7));
		HddProtectionSnapshot resumed = controller.GetSnapshot(@"Y:\")!.Value;
		Assert.False(resumed.IsBlocked);
		Assert.False(resumed.IsCooling);
		Assert.Equal(44, resumed.TemperatureC);
	}

	[Fact]
	public async Task SnmpFailure_IsFailSafeAndBlocksProtectedRoot() {
		var source = new FakeTemperatureSource { Error = new TimeoutException("test timeout") };
		await using var controller = new HddProtectionController(
			new Dictionary<string, int> { ["Y:"] = 2 }, source,
			48, 50, 45, TimeSpan.Zero, 1, TimeSpan.FromMinutes(1));

		await controller.PollOnceAsync(CancellationToken.None);
		HddProtectionSnapshot snapshot = controller.GetSnapshot(@"Y:\")!.Value;
		Assert.True(snapshot.IsBlocked);
		Assert.True(snapshot.IsWaitingForTemperature);
		Assert.Null(snapshot.TemperatureC);
	}
}
