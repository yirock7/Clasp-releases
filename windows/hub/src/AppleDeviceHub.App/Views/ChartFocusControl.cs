using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// The focusable battery chart. A plain ContentControl has no automation peer of its own, so screen
/// readers saw nothing to land on; this gives it a named "chart" element and lets the view send a
/// spoken notification whenever the keyboard moves to another reading.
/// </summary>
public sealed partial class ChartFocusControl : ContentControl
{
    public const string HelpText = "Use Left and Right arrows to read values";

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartFocusPeer(this);

    /// <summary>Asks narrators to say <paramref name="text"/> now, replacing anything still queued.</summary>
    public void Announce(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
                peer.RaiseNotificationEvent(
                    AutomationNotificationKind.ActionCompleted,
                    AutomationNotificationProcessing.MostRecent,
                    text,
                    "BatteryChartReading");
        }
        catch
        {
            // Accessibility is best effort; a failure here must never reach the UI thread.
        }
    }

    private sealed partial class ChartFocusPeer : FrameworkElementAutomationPeer
    {
        public ChartFocusPeer(ChartFocusControl owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetLocalizedControlTypeCore() => "chart";

        protected override string GetClassNameCore() => nameof(ChartFocusControl);

        protected override string GetHelpTextCore() => HelpText;

        protected override bool IsKeyboardFocusableCore() => true;
    }
}
