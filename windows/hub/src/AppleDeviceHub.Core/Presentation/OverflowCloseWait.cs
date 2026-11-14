namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// Decides how long the flyout waits for the Windows hidden-icons popup to close before it shows.
/// The caller polls every <see cref="PollIntervalMs"/>; once the popup is gone, or the budget is
/// spent, the flyout shows regardless so opening never feels sluggish.
/// </summary>
public enum OverflowWaitAction
{
    Wait,
    Show,
    Cancel,
}

public static class OverflowCloseWait
{
    public const int PollIntervalMs = 16;
    public const int MaxWaitMs = 350;

    /// <summary>True when the flyout should show now: the popup is gone or the time budget is used up.</summary>
    public static bool ShouldShow(long elapsedMs, bool popupStillVisible)
        => !popupStillVisible || elapsedMs >= MaxWaitMs;

    /// <summary>
    /// The full per-poll decision. A deferred open is abandoned (<see cref="OverflowWaitAction.Cancel"/>) when
    /// the user has moved on: pressed Escape, clicked somewhere else, or another app took the foreground.
    /// It shows only while focus is still with the taskbar, the hidden-icons popup, our own window, or nothing.
    /// </summary>
    public static OverflowWaitAction Decide(long elapsedMs, bool popupStillVisible, bool escapePressed, bool clickedElsewhere, bool otherAppForeground)
    {
        if (escapePressed || clickedElsewhere || otherAppForeground) return OverflowWaitAction.Cancel;
        return ShouldShow(elapsedMs, popupStillVisible) ? OverflowWaitAction.Show : OverflowWaitAction.Wait;
    }

    /// <summary>The one log line describing how the wait ended.</summary>
    public static string Describe(long elapsedMs, bool popupStillVisible)
        => popupStillVisible
            ? $"Flyout open: hidden-icons popup still visible after {elapsedMs} ms"
            : $"Flyout open: waited {elapsedMs} ms for hidden-icons popup to close";
}
