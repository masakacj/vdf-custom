// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VDF.GUI.Utils {
	/// <summary>
	/// Prevents two Windows GUI processes from opening the same multi-GB VDF state at
	/// the same time. A second launch signals the primary instance and exits; the
	/// primary then restores/activates its existing main window.
	/// </summary>
	internal sealed class SingleInstanceCoordinator : IDisposable {
		const string MutexName = @"Local\VDF.Custom.GUI.SingleInstance";
		const string ActivationEventName = @"Local\VDF.Custom.GUI.Activate";

		static readonly object windowGate = new();
		static Window? mainWindow;
		static int pendingActivation;

		readonly Mutex? mutex;
		readonly EventWaitHandle? activationEvent;
		readonly CancellationTokenSource? stop;
		readonly bool ownsMutex;
		Task? listener;

		SingleInstanceCoordinator(Mutex? mutex, EventWaitHandle? activationEvent,
			CancellationTokenSource? stop, bool ownsMutex) {
			this.mutex = mutex;
			this.activationEvent = activationEvent;
			this.stop = stop;
			this.ownsMutex = ownsMutex;
		}

		/// <summary>
		/// Returns null for a secondary Windows launch after signaling the already-running
		/// instance. Non-Windows platforms remain multi-instance to avoid changing their
		/// existing process model in this Windows-focused hotfix. Kernel-object creation
		/// failures fail open rather than preventing VDF from starting.
		/// </summary>
		internal static SingleInstanceCoordinator? TryAcquirePrimary() {
			if (!OperatingSystem.IsWindows())
				return new SingleInstanceCoordinator(null, null, null, ownsMutex: false);

			EventWaitHandle? activationEvent = null;
			Mutex? mutex = null;
			try {
				// Create/open the activation event first. If two launches race before the
				// primary listener starts, AutoResetEvent retains the signal for it.
				activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
				mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
				if (!createdNew) {
					try { activationEvent.Set(); } catch { }
					activationEvent.Dispose();
					mutex.Dispose();
					return null;
				}

				var stop = new CancellationTokenSource();
				var coordinator = new SingleInstanceCoordinator(mutex, activationEvent, stop, ownsMutex: true);
				coordinator.listener = Task.Run(coordinator.ListenForActivation);
				return coordinator;
			}
			catch {
				try { activationEvent?.Dispose(); } catch { }
				try { mutex?.Dispose(); } catch { }
				return new SingleInstanceCoordinator(null, null, null, ownsMutex: false);
			}
		}

		internal static void NotifyMainWindowReady(Window window) {
			lock (windowGate)
				mainWindow = window;
			if (Volatile.Read(ref pendingActivation) != 0)
				TryActivateMainWindow();
		}

		void ListenForActivation() {
			if (activationEvent == null || stop == null) return;
			WaitHandle[] handles = [activationEvent, stop.Token.WaitHandle];
			while (!stop.IsCancellationRequested) {
				int signaled;
				try { signaled = WaitHandle.WaitAny(handles); }
				catch { return; }
				if (signaled != 0 || stop.IsCancellationRequested) return;
				Interlocked.Exchange(ref pendingActivation, 1);
				TryActivateMainWindow();
			}
		}

		static void TryActivateMainWindow() {
			Window? window;
			lock (windowGate)
				window = mainWindow;
			if (window == null) return; // keep pendingActivation set until the window exists

			Interlocked.Exchange(ref pendingActivation, 0);
			Dispatcher.UIThread.Post(() => {
				try {
					if (!window.IsVisible)
						window.Show();
					if (window.WindowState == WindowState.Minimized)
						window.WindowState = WindowState.Normal;
					window.Activate();
				}
				catch { /* activation is best-effort; never destabilize the primary instance */ }
			});
		}

		public void Dispose() {
			Interlocked.Exchange(ref pendingActivation, 0);
			try {
				stop?.Cancel();
				activationEvent?.Set();
				listener?.Wait(TimeSpan.FromSeconds(1));
			}
			catch { }
			try { stop?.Dispose(); } catch { }
			try { activationEvent?.Dispose(); } catch { }
			if (ownsMutex && mutex != null) {
				try { mutex.ReleaseMutex(); } catch { }
			}
			try { mutex?.Dispose(); } catch { }
		}
	}
}
