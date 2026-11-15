namespace AppleDeviceHub.Core.Presentation;

/// <summary>Why the flyout is being opened. Only a tray-icon click can come from the hidden-icons popup.</summary>
public enum FlyoutOpenReason
{
    /// <summary>The user clicked, or keyboard-activated, our tray icon (the NotifyIcon callback).</summary>
    TrayClick,
    /// <summary>Anything else: --show=flyout, clasp:// commands, hotkeys, second-instance launches, code.</summary>
    Programmatic,
    /// <summary>The unfocused case-open peek.</summary>
    Passive,
}

/// <summary>The open-reason decisions: when the hidden-icons popup matters, and whether an open may be abandoned.</summary>
public static class FlyoutOpenPolicy
{
    /// <summary>Only tray-icon clicks look at the hidden-icons popup at all.</summary>
    public static bool ChecksOverflow(FlyoutOpenReason reason) => reason == FlyoutOpenReason.TrayClick;

    /// <summary>
    /// True when the open must be deferred until the hidden-icons popup closes: a tray click, with the popup
    /// genuinely open (the caller has verified it is uncloaked, on screen and holds the icon).
    /// </summary>
    public static bool ShouldWaitForOverflow(FlyoutOpenReason reason, bool overflowGenuinelyOpen)
        => ChecksOverflow(reason) && overflowGenuinelyOpen;

    /// <summary>Only a deferred tray-click open can be cancelled; every other open always shows.</summary>
    public static bool CanCancel(FlyoutOpenReason reason) => ChecksOverflow(reason);

    /// <summary>
    /// The per-poll decision for a deferred open. Programmatic and passive opens never wait and never cancel:
    /// they always answer <see cref="OverflowWaitAction.Show"/>.
    /// </summary>
    public static OverflowWaitAction Decide(FlyoutOpenReason reason, long elapsedMs, bool popupStillVisible,
        bool escapePressed, bool clickedElsewhere, bool otherAppForeground)
        => ChecksOverflow(reason)
            ? OverflowCloseWait.Decide(elapsedMs, popupStillVisible, escapePressed, clickedElsewhere, otherAppForeground)
            : OverflowWaitAction.Show;

    /// <summary>
    /// "Genuinely open" for one overflow window: visible, not cloaked, and with a non-empty on-screen rectangle.
    /// Windows 11 keeps TopLevelWindowForOverflowXamlIsland alive and "visible" while it is cloaked or empty.
    /// </summary>
    public static bool IsPopupGenuinelyOpen(bool isVisible, bool isCloaked, bool rectNonEmpty, bool rectOnScreen)
        => isVisible && !isCloaked && rectNonEmpty && rectOnScreen;
}
