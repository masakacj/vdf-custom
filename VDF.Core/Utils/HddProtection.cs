// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

namespace VDF.Core.Utils {

	internal readonly record struct HddProtectionSnapshot(
		int DiskSlot,
		int? TemperatureC,
		bool IsBlocked,
		bool IsCooling,
		bool IsWaitingForTemperature,
		bool IsWarm);

	internal static class HddProtectionMappings {
		/// <summary>
		/// Parses mappings such as <c>Y:=2; Z:=3</c> or one mapping per line. The left side
		/// is a drive/share root and the right side is QNAP's physical disk slot index.
		/// </summary>
		internal static Dictionary<string, int> Parse(string? text) {
			var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrWhiteSpace(text))
				return result;
			string[] items = text.Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			foreach (string item in items) {
				int separator = item.LastIndexOf('=');
				if (separator <= 0 || separator >= item.Length - 1)
					continue;
				string root = NormalizeRoot(item[..separator].Trim());
				if (root.Length == 0 || !int.TryParse(item[(separator + 1)..].Trim(), out int slot) || slot <= 0)
					continue;
				result[root] = slot;
			}
			return result;
		}

		internal static string NormalizeRoot(string root) {
			root = root.Trim();
			if (root.Length == 2 && char.IsAsciiLetter(root[0]) && root[1] == ':')
				return char.ToUpperInvariant(root[0]) + ":";
			root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return root;
		}

		internal static bool TryGetSlot(IReadOnlyDictionary<string, int> mappings, string root, out int slot) =>
			mappings.TryGetValue(NormalizeRoot(root), out slot);
	}

	/// <summary>
	/// Per-physical-HDD temperature gate. Mapped drives are blocked until a fresh SNMP
	/// temperature exists, pause after the current file once the pause threshold is reached,
	/// and resume only after both the minimum cooldown and consecutive low-temperature polls.
	/// A transient SNMP failure is fail-safe: no new heavy read starts on protected disks.
	/// </summary>
	internal sealed class HddProtectionController : IAsyncDisposable {
		sealed class DiskState {
			internal required string Root;
			internal required int Slot;
			internal int? TemperatureC;
			internal bool IsBlocked = true;
			internal bool IsCooling;
			internal bool IsWaitingForTemperature = true;
			internal bool IsWarm;
			internal DateTime? CoolingSinceUtc;
			internal int ResumePolls;
			internal TaskCompletionSource<bool> AllowedSignal = NewSignal();
			internal readonly SemaphoreSlim HeavyReadGate = new(1, 1);
		}

		readonly object sync = new();
		readonly Dictionary<string, DiskState> states;
		readonly IDiskTemperatureSource source;
		readonly int warnTemperatureC;
		readonly int pauseTemperatureC;
		readonly int resumeTemperatureC;
		readonly TimeSpan minimumCooldown;
		readonly int resumeConsecutivePolls;
		readonly TimeSpan pollInterval;
		CancellationTokenSource? loopCts;
		Task? loopTask;

		internal HddProtectionController(IReadOnlyDictionary<string, int> driveMappings,
			IDiskTemperatureSource source,
			int warnTemperatureC,
			int pauseTemperatureC,
			int resumeTemperatureC,
			TimeSpan minimumCooldown,
			int resumeConsecutivePolls,
			TimeSpan pollInterval) {
			this.source = source;
			this.warnTemperatureC = warnTemperatureC;
			this.pauseTemperatureC = pauseTemperatureC;
			this.resumeTemperatureC = resumeTemperatureC;
			this.minimumCooldown = minimumCooldown < TimeSpan.Zero ? TimeSpan.Zero : minimumCooldown;
			this.resumeConsecutivePolls = Math.Max(1, resumeConsecutivePolls);
			this.pollInterval = pollInterval < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : pollInterval;
			states = new Dictionary<string, DiskState>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, int> mapping in driveMappings) {
				string normalized = HddProtectionMappings.NormalizeRoot(mapping.Key);
				states[normalized] = new DiskState { Root = normalized, Slot = mapping.Value };
			}
		}

		internal static HddProtectionController? TryCreate(Settings settings, IReadOnlyList<DriveScanGroup> groups,
			IDiskTemperatureSource? sourceOverride = null) {
			if (!settings.EnableHddProtection)
				return null;
			Dictionary<string, int> configured = HddProtectionMappings.Parse(settings.HddProtectionDriveMappings);
			var active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (DriveScanGroup group in groups) {
				if (HddProtectionMappings.TryGetSlot(configured, group.Root, out int slot))
					active[HddProtectionMappings.NormalizeRoot(group.Root)] = slot;
			}
			if (active.Count == 0) {
				Logger.Instance.Warn("HDD protection is enabled but none of the active scan drive roots has a valid QNAP disk-slot mapping.");
				return null;
			}
			if (sourceOverride == null) {
				if (string.IsNullOrWhiteSpace(settings.HddProtectionSnmpHost) || string.IsNullOrWhiteSpace(settings.HddProtectionSnmpUser)) {
					Logger.Instance.Warn("HDD protection is enabled but the QNAP SNMP host or SNMPv3 user is empty.");
					return null;
				}
				sourceOverride = new QnapSnmpV3TemperatureClient(settings.HddProtectionSnmpHost.Trim(),
					Math.Clamp(settings.HddProtectionSnmpPort, 1, 65535), settings.HddProtectionSnmpUser.Trim());
			}
			return new HddProtectionController(active, sourceOverride,
				settings.HddProtectionWarnTemperatureC,
				settings.HddProtectionPauseTemperatureC,
				settings.HddProtectionResumeTemperatureC,
				TimeSpan.FromMinutes(Math.Max(0, settings.HddProtectionMinimumCooldownMinutes)),
				settings.HddProtectionResumeConsecutivePolls,
				TimeSpan.FromSeconds(Math.Max(5, settings.HddProtectionPollSeconds)));
		}

		internal async Task StartAsync(CancellationToken scanToken) {
			if (states.Count == 0)
				return;
			await PollOnceAsync(scanToken);
			loopCts = CancellationTokenSource.CreateLinkedTokenSource(scanToken);
			loopTask = PollLoopAsync(loopCts.Token);
		}

		internal async Task WaitUntilAllowedAsync(string root, CancellationToken token) {
			string normalized = HddProtectionMappings.NormalizeRoot(root);
			while (true) {
				Task wait;
				lock (sync) {
					if (!states.TryGetValue(normalized, out DiskState? state) || !state.IsBlocked)
						return;
					wait = state.AllowedSignal.Task;
				}
				await wait.WaitAsync(token);
			}
		}

		internal HddProtectionSnapshot? GetSnapshot(string root) {
			string normalized = HddProtectionMappings.NormalizeRoot(root);
			lock (sync) {
				if (!states.TryGetValue(normalized, out DiskState? state))
					return null;
				return new HddProtectionSnapshot(state.Slot, state.TemperatureC, state.IsBlocked,
					state.IsCooling, state.IsWaitingForTemperature, state.IsWarm);
			}
		}

		/// <summary>
		/// Acquires the one-heavy-read-per-physical-HDD lease for an operation that can
		/// touch one or more mapped roots (for example partial-clip visual verification
		/// decodes both a source and a clip). Gates are acquired in root order to avoid
		/// deadlocks, and temperature state is rechecked after acquisition.
		/// </summary>
		internal async Task<IDisposable> EnterHeavyReadAsync(IEnumerable<string> roots, CancellationToken token) {
			List<DiskState> targets;
			lock (sync) {
				targets = roots
					.Select(HddProtectionMappings.NormalizeRoot)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.Select(root => states.TryGetValue(root, out DiskState? state) ? state : null)
					.Where(state => state != null)
					.Cast<DiskState>()
					.OrderBy(state => state.Root, StringComparer.OrdinalIgnoreCase)
					.ToList();
			}
			if (targets.Count == 0)
				return NoopLease.Instance;

			while (true) {
				foreach (DiskState state in targets)
					await WaitUntilAllowedAsync(state.Root, token);

				var acquired = new List<SemaphoreSlim>(targets.Count);
				bool transferred = false;
				try {
					foreach (DiskState state in targets) {
						await state.HeavyReadGate.WaitAsync(token);
						acquired.Add(state.HeavyReadGate);
					}
					bool blockedAgain;
					lock (sync)
						blockedAgain = targets.Any(state => state.IsBlocked);
					if (!blockedAgain) {
						transferred = true;
						return new GateLease(acquired);
					}
				}
				finally {
					// A temperature poll can cross the pause threshold while the worker is
					// queued on a disk gate. If that happened, give all gates back and wait
					// for the normal cooldown/resume path instead of starting a new read.
					if (!transferred)
						for (int i = acquired.Count - 1; i >= 0; i--)
							acquired[i].Release();
				}
			}
		}

		internal async Task PollOnceAsync(CancellationToken token, DateTime? utcNowOverride = null) {
			int[] slots;
			lock (sync)
				slots = states.Values.Select(x => x.Slot).Distinct().OrderBy(x => x).ToArray();
			IReadOnlyDictionary<int, int> temperatures;
			try {
				temperatures = await source.GetTemperaturesAsync(slots, token);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested) {
				throw;
			}
			catch (Exception ex) {
				lock (sync) {
					foreach (DiskState state in states.Values) {
						state.TemperatureC = null;
						state.IsWaitingForTemperature = true;
						state.IsWarm = false;
						SetBlockedLocked(state, true);
					}
				}
				Logger.Instance.Warn($"HDD protection: QNAP SNMP temperature poll failed; protected drives are waiting before starting new IO. {ex.Message}");
				return;
			}

			DateTime now = utcNowOverride ?? DateTime.UtcNow;
			lock (sync) {
				foreach (DiskState state in states.Values) {
					if (!temperatures.TryGetValue(state.Slot, out int temperature)) {
						state.TemperatureC = null;
						state.IsWaitingForTemperature = true;
						state.IsWarm = false;
						SetBlockedLocked(state, true);
						continue;
					}

					state.TemperatureC = temperature;
					state.IsWaitingForTemperature = false;
					state.IsWarm = temperature >= warnTemperatureC;

					if (!state.IsCooling && temperature >= pauseTemperatureC) {
						state.IsCooling = true;
						state.CoolingSinceUtc = now;
						state.ResumePolls = 0;
						SetBlockedLocked(state, true);
						Logger.Instance.Warn($"HDD protection: {state.Root} / QNAP Disk {state.Slot} reached {temperature}°C; pausing new reads after the current file.");
						continue;
					}

					if (state.IsCooling) {
						bool cooledLongEnough = state.CoolingSinceUtc != null && now - state.CoolingSinceUtc.Value >= minimumCooldown;
						if (temperature <= resumeTemperatureC && cooledLongEnough)
							state.ResumePolls++;
						else if (temperature > resumeTemperatureC)
							state.ResumePolls = 0;

						if (state.ResumePolls >= resumeConsecutivePolls) {
							state.IsCooling = false;
							state.CoolingSinceUtc = null;
							state.ResumePolls = 0;
							SetBlockedLocked(state, false);
							Logger.Instance.Info($"HDD protection: {state.Root} / QNAP Disk {state.Slot} cooled to {temperature}°C; resuming reads.");
						}
						else {
							SetBlockedLocked(state, true);
						}
						continue;
					}

					SetBlockedLocked(state, false);
				}
			}
		}

		async Task PollLoopAsync(CancellationToken token) {
			using var timer = new PeriodicTimer(pollInterval);
			try {
				while (await timer.WaitForNextTickAsync(token))
					await PollOnceAsync(token);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested) { }
		}

		static void SetBlockedLocked(DiskState state, bool blocked) {
			if (state.IsBlocked == blocked)
				return;
			state.IsBlocked = blocked;
			if (blocked) {
				state.AllowedSignal = NewSignal();
			}
			else {
				state.AllowedSignal.TrySetResult(true);
			}
		}

		static TaskCompletionSource<bool> NewSignal() =>
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		sealed class GateLease : IDisposable {
			readonly IReadOnlyList<SemaphoreSlim> gates;
			int disposed;
			internal GateLease(IReadOnlyList<SemaphoreSlim> gates) => this.gates = gates;
			public void Dispose() {
				if (Interlocked.Exchange(ref disposed, 1) != 0)
					return;
				for (int i = gates.Count - 1; i >= 0; i--)
					gates[i].Release();
			}
		}

		sealed class NoopLease : IDisposable {
			internal static readonly NoopLease Instance = new();
			public void Dispose() { }
		}

		public async ValueTask DisposeAsync() {
			if (loopCts != null) {
				loopCts.Cancel();
				if (loopTask != null) {
					try { await loopTask; } catch (OperationCanceledException) { }
				}
				loopCts.Dispose();
				loopCts = null;
				loopTask = null;
			}
			lock (sync) {
				foreach (DiskState state in states.Values)
					state.AllowedSignal.TrySetResult(true);
			}
		}
	}
}
