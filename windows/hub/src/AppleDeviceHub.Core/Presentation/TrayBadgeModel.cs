using System.Globalization;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// What the tray icon should show: the average battery <see cref="Level"/> (null when unreported), whether the
/// icon is dimmed, whether the earbuds are charging, the silhouette <see cref="Tint"/> and an accessible tooltip
/// (which carries the exact percentage). <see cref="Text"/> is the number as text, null when not reported or
/// when the user turned colouring off; the icon itself draws no digits.
/// </summary>
public sealed record TrayBadge(string? Text, bool Dimmed, bool Charging, string Tooltip, TrayGlyph? Glyph = null, int? Level = null, TrayTint Tint = TrayTint.Normal);

/// <summary>
/// Pure decision logic for the tray battery number. The number is the average of the earbud
/// batteries that were actually reported (case is ignored). If one earbud is unreported the other
/// is used alone; if neither is reported there is no number. Values keep the precision they were
/// reported with: BLE broadcasts report ten-percent steps, so the average of two readings may land
/// on a five (for example 80 and 70 give 75); nothing is rounded to look more exact than it is.
/// </summary>
public static class TrayBadgeModel
{
    /// <summary>Tooltip used when no earbud battery has been reported. Claims no value.</summary>
    public static string UnknownTooltip => UnknownTooltipFor("AirPods");

    private static string UnknownTooltipFor(string noun) => string.Format(CultureInfo.CurrentCulture, Strings.Hub_Tray_Tooltip_BatteryUnknown, noun);

    /// <param name="state">The current device state, or null when there is none.</param>
    /// <param name="outputIsAirPods">True when Windows is currently playing sound to the AirPods.</param>
    /// <param name="showPercentage">User setting: draw the number on the icon.</param>
    /// <param name="dimWhenNotOutput">User setting: dim the icon when sound is not on the AirPods.</param>
    /// <param name="glyph">The silhouette to carry on the badge, if any.</param>
    public static TrayBadge From(DeviceState? state, bool outputIsAirPods, bool showPercentage = true, bool dimWhenNotOutput = true, TrayGlyph? glyph = null)
    {
        var dimmed = dimWhenNotOutput && !outputIsAirPods;
        var noun = state?.DeviceNoun ?? "AirPods";
        var left = Reported(state?.Battery.Left);
        var right = Reported(state?.Battery.Right);

        int? average = (left, right) switch
        {
            ({ } l, { } r) => (int)Math.Round((l.Level + r.Level) / 2.0, MidpointRounding.AwayFromZero),
            ({ } l, null) => l.Level,
            (null, { } r) => r.Level,
            _ => null,
        };
        if (average is null) return new TrayBadge(null, dimmed, false, WithDim(UnknownTooltipFor(noun), dimmed), glyph, null);

        var charging = left?.Charging == true || right?.Charging == true;
        var level = average.Value.ToString(CultureInfo.InvariantCulture);
        var tooltip = string.Format(CultureInfo.CurrentCulture, charging ? Strings.Hub_Tray_Tooltip_LevelCharging : Strings.Hub_Tray_Tooltip_Level, noun, level);
        var text = showPercentage ? average.Value.ToString(CultureInfo.InvariantCulture) : null;
        return new TrayBadge(text, dimmed, charging, WithDim(tooltip, dimmed), glyph, average.Value);
    }

    /// <summary>
    /// The badge for Windows' own battery reading (one combined percent reported over the Bluetooth
    /// Handsfree link), used when no fresh per-earbud reading is available.
    /// </summary>
    public static TrayBadge FromWindowsBattery(int percent, bool outputIsAirPods, bool showPercentage = true, bool dimWhenNotOutput = true, string noun = "AirPods", TrayGlyph? glyph = null)
    {
        var dimmed = dimWhenNotOutput && !outputIsAirPods;
        var value = Math.Clamp(percent, 0, 100);
        var tooltip = string.Format(CultureInfo.CurrentCulture, Strings.Hub_Tray_Tooltip_LevelFromWindows, noun, value.ToString(CultureInfo.InvariantCulture));
        var text = showPercentage ? value.ToString(CultureInfo.InvariantCulture) : null;
        return new TrayBadge(text, dimmed, false, WithDim(tooltip, dimmed), glyph, value);
    }

    private static string WithDim(string text, bool dimmed) => dimmed ? string.Format(CultureInfo.CurrentCulture, Strings.Hub_Tray_Tooltip_SoundElsewhere, text) : text;

    private static (int Level, bool Charging)? Reported(BatteryComponent? part)
    {
        if (part is not { } p || !p.Level.HasValue) return null;
        return (p.Level.Value!.Value, p.Charging.Value == true);
    }
}
