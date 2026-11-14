using AppleDeviceHub.Core.Battery;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Windows toast notifications for an unpackaged app, through the Windows App SDK
/// <see cref="AppNotificationManager"/>. Unpackaged apps must call <see cref="Initialize"/> once
/// at startup (it registers the notification activator for this process) and
/// <see cref="Shutdown"/> on exit. If registration fails, <see cref="ShowLowBattery"/> returns
/// false and nothing is shown; the app keeps working and the reason is in the local log.
/// Toasts carry text only and no actions, so there is nothing to handle on activation.
/// </summary>
internal static class Toasts
{
    private static bool _registered;

    /// <summary>Registers for notifications. Safe to call more than once. Returns true when toasts can be shown.</summary>
    public static bool Initialize()
    {
        if (_registered) return true;
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Toast registration failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        return _registered;
    }

    public static void Shutdown()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception ex) { LocalLog.Write($"Toast unregister failed: {ex.GetType().Name}"); }
        _registered = false;
    }

    /// <summary>Shows the low-battery toast. Returns false when it could not be shown.</summary>
    public static bool ShowLowBattery(LowBatteryAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return Show(alert.Title, alert.Body);
    }

    /// <summary>Shows a two-line text toast. Returns false when it could not be shown.</summary>
    public static bool Show(string title, string body)
    {
        if (!Initialize()) return false;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Toast show failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return false;
        }
    }
}
