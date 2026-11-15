// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Artwork;

/// <summary>Which per-model drawing (loose SVG file under Assets\Artwork) a model id uses. Other models still use the older drawings.</summary>
public static class ArtworkCatalog
{
    public const string AirPodsPro = "airpods-pro";
    public const string AirPods = "airpods";
    public const string AirPods3 = "airpods-3";
    public const string AirPods4 = "airpods-4";
    public const string AirPodsMax = "airpods-max";
    public const string PowerbeatsPro2 = "powerbeats-pro-2";
    public const string BeatsStudioBudsPlus = "beats-studio-buds-plus";
    public const string BeatsSolo4 = "beats-solo-4";
    public const string PowerbeatsPro = "powerbeats-pro";
    public const string PowerbeatsFit = "powerbeats-fit";
    public const string BeatsFitPro = "beats-fit-pro";
    public const string BeatsStudioBuds = "beats-studio-buds";
    public const string BeatsSoloBuds = "beats-solo-buds";
    public const string BeatsSolo3 = "beats-solo-3";
    public const string BeatsSoloPro = "beats-solo-pro";
    public const string BeatsStudio3 = "beats-studio-3";
    public const string BeatsStudioPro = "beats-studio-pro";
    public const string Powerbeats3 = "powerbeats-3";
    public const string Powerbeats4 = "powerbeats-4";
    public const string BeatsX = "beatsx";
    public const string BeatsFlex = "beats-flex";

    /// <summary>Every drawing key that has a file.</summary>
    public static IReadOnlyList<string> Keys { get; } =
        [AirPods, AirPods3, AirPods4, AirPodsPro, AirPodsMax, PowerbeatsPro2, BeatsStudioBudsPlus, BeatsSolo4,
        PowerbeatsPro, PowerbeatsFit, BeatsFitPro, BeatsStudioBuds, BeatsSoloBuds, BeatsSolo3, BeatsSoloPro, BeatsStudio3, BeatsStudioPro, Powerbeats3, Powerbeats4, BeatsX, BeatsFlex];

    /// <summary>The drawing key for a wire-order model id, or null when the model has no per-model drawing yet.</summary>
    public static string? DrawingKeyFor(ushort? modelId) => modelId switch
    {
        0x0220 or 0x0F20 => AirPods,
        0x1320 => AirPods3,
        0x1920 or 0x1B20 or 0x3620 or 0x3020 => AirPods4,
        0x0E20 or 0x1420 or 0x2420 or 0x2720 => AirPodsPro,
        0x0A20 or 0x1F20 or 0x2D20 => AirPodsMax,
        0x1D20 => PowerbeatsPro2,
        0x1620 => BeatsStudioBudsPlus,
        0x2520 => BeatsSolo4,
        0x0B20 => PowerbeatsPro,
        0x2F20 => PowerbeatsFit,
        0x1220 => BeatsFitPro,
        0x1120 => BeatsStudioBuds,
        0x2620 => BeatsSoloBuds,
        0x0620 => BeatsSolo3,
        0x0C20 => BeatsSoloPro,
        0x0920 => BeatsStudio3,
        0x1720 => BeatsStudioPro,
        0x0320 => Powerbeats3,
        0x0D20 => Powerbeats4,
        0x0520 => BeatsX,
        0x1020 => BeatsFlex,
        _ => null,
    };

    public static string FileNameFor(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return key + ".svg";
    }
}
