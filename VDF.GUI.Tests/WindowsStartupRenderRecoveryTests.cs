using VDF.GUI;

namespace VDF.GUI.Tests;

public class WindowsStartupRenderRecoveryTests {
    [Fact]
    public void DetectsTraceStoppedAfterFrameworkCompletion() {
        string trace = "2026-10-10 18:00 App.Initialize begin\n" +
                       "2026-10-10 18:00 Framework initialization completed\n";
        Assert.True(WindowsStartupRenderRecovery.PreviousLaunchDiedBeforeWindow(trace));
    }

    [Fact]
    public void OpenedWindowDoesNotTriggerRecovery() {
        string trace = "App.Initialize begin\nFramework initialization completed\nMainWindow Opened\n";
        Assert.False(WindowsStartupRenderRecovery.PreviousLaunchDiedBeforeWindow(trace));
    }

    [Fact]
    public void IncompleteInitializationDoesNotTriggerRecovery() {
        Assert.False(WindowsStartupRenderRecovery.PreviousLaunchDiedBeforeWindow(
            "App.Initialize begin\nMainWindow constructed\n"));
    }

    [Fact]
    public void OnlyLastLaunchCounts() {
        string trace = "App.Initialize begin\nFramework initialization completed\n" +
                       "App.Initialize begin\nFramework initialization completed\nMainWindow Opened\n";
        Assert.False(WindowsStartupRenderRecovery.PreviousLaunchDiedBeforeWindow(trace));
    }

    [Fact]
    public void OldTraceDoesNotForceSafeMode() {
        var now = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(WindowsStartupRenderRecovery.IsRecentFailure(now.AddMinutes(-1), now));
        Assert.False(WindowsStartupRenderRecovery.IsRecentFailure(now.AddDays(-15), now));
        Assert.False(WindowsStartupRenderRecovery.IsRecentFailure(now.AddMinutes(1), now));
    }
}
