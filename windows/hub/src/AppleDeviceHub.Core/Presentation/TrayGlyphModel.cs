using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// What the tray icon draws as its silhouette. <see cref="Group"/> is null when the Clasp logo
/// should be drawn instead (no device has been selected yet). <see cref="Dimmed"/> asks for the
/// muted rendering used when the device is not connected or sound is going elsewhere.
/// </summary>
public sealed record TrayGlyph(ArtworkGroup? Group, bool Dimmed);

/// <summary>How the silhouette is coloured: the taskbar foreground, or a status colour for the battery level.</summary>
public enum TrayTint { Normal, Low, Critical, Charging }

/// <summary>
/// Pure decision logic for the tray silhouette. The model id must come only from the selected
/// Windows-paired device, never from BLE broadcasts, because those are spoofable.
/// </summary>
public static class TrayGlyphModel
{
    /// <param name="selectedModelId">Model id parsed from the selected Windows-paired device, or null when unknown.</param>
    /// <param name="hasSelectedDevice">True when a device has been selected; false draws the Clasp logo.</param>
    /// <param name="connected">True when the selected device is currently connected.</param>
    /// <param name="outputIsDevice">True when Windows is currently playing sound to the device.</param>
    /// <param name="dimWhenNotOutput">User setting: dim the icon when sound is not on the device.</param>
    public static TrayGlyph For(ushort? selectedModelId, bool hasSelectedDevice, bool connected, bool outputIsDevice, bool dimWhenNotOutput)
    {
        var group = hasSelectedDevice ? AppleModelCatalog.ArtworkOf(selectedModelId) : (ArtworkGroup?)null;
        var dimmed = !connected || (dimWhenNotOutput && !outputIsDevice);
        return new TrayGlyph(group, dimmed);
    }

    /// <summary>
    /// Demo mode only (screenshots): the silhouette for the demo state's model id, never dimmed by audio routing.
    /// The non-demo path must keep using <see cref="For"/> with the Windows-paired device only.
    /// </summary>
    public static TrayGlyph ForDemo(ushort? demoModelId, bool connected, bool dimWhenNotOutput)
        => For(demoModelId, true, connected, true, dimWhenNotOutput);

    /// <summary>Battery percent at or below which the tint is <see cref="TrayTint.Low"/>.</summary>
    public const int LowPercent = 40;

    /// <summary>Critical level used when the low-battery alert threshold is off (0).</summary>
    public const int DefaultCriticalPercent = 20;

    /// <summary>
    /// The silhouette tint: charging wins, then critical (at or below the low-battery threshold, 20 % when that is off),
    /// then low (40 % or less). Unknown level or colouring turned off gives the normal taskbar colour.
    /// </summary>
    public static TrayTint TintFor(int? level, bool charging, int lowThresholdPercent, bool colourEnabled)
    {
        if (!colourEnabled || level is not { } value) return TrayTint.Normal;
        if (charging) return TrayTint.Charging;
        if (value <= (lowThresholdPercent > 0 ? lowThresholdPercent : DefaultCriticalPercent)) return TrayTint.Critical;
        if (value <= LowPercent) return TrayTint.Low;
        return TrayTint.Normal;
    }
}
