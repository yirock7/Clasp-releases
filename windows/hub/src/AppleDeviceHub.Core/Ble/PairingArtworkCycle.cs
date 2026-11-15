// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Ble;

/// <summary>
/// The models the waiting artwork cycles through, in order, and which one to rest on under Reduce motion. Each is a model id with
/// its own per-model drawing (see <see cref="Artwork.ArtworkCatalog"/>): earbuds with a case, Beats buds, over-ear and on-ear
/// headphones, an ear-hook model and a neckband.
/// </summary>
public static class PairingArtworkCycle
{
    /// <summary>AirPods Pro 2, AirPods 4, AirPods Max, Beats Studio Pro, Beats Studio Buds+, Powerbeats Pro 2, Beats Solo 4, Beats Flex.</summary>
    public static IReadOnlyList<ushort> Models { get; } = [0x2420, 0x1920, 0x0A20, 0x1720, 0x1620, 0x1D20, 0x2520, 0x1020];

    /// <summary>The model shown with Reduce motion while nothing is found: an AirPods Pro case.</summary>
    public static ushort Resting => Models[0];

    /// <summary>The index after <paramref name="index"/>, wrapping at the end.</summary>
    public static int Next(int index) => (index + 1) % Models.Count;
}

/// <summary>
/// A device the welcome guide has found: the name to show, the drawing group, and the model id whose per-model drawing to
/// settle on (null when no model could be told, which keeps the group drawing).
/// </summary>
public sealed record DetectedDevice(string Name, ArtworkGroup Art, ushort? ModelId = null);

/// <summary>Decides whether the welcome guide has found a device yet.</summary>
public static class DeviceDetection
{
    /// <summary>AirPods (2nd generation), the plain stem earbuds, stands in for a paired device called "... AirPods" that names no catalog model.</summary>
    private const ushort PlainAirPods = 0x0F20;

    /// <summary>
    /// A live hub model (connected or nearby) wins, since it names the exact model. Otherwise a chosen paired
    /// device counts, drawn by the longest catalog model name inside its name. Null means "still looking".
    /// </summary>
    public static DetectedDevice? Decide(string? selectedDeviceName, string? hubModelName, bool hubDeviceIsLive)
    {
        if (hubDeviceIsLive && ModelForName(hubModelName, requireCatalogMatch: true) is { } hub)
            return new DetectedDevice(hubModelName!.Trim(), hub.Art, hub.Id);
        if (!string.IsNullOrWhiteSpace(selectedDeviceName))
        {
            var picked = ModelForName(selectedDeviceName, requireCatalogMatch: false);
            return new DetectedDevice(selectedDeviceName.Trim(), picked?.Art ?? ArtworkGroup.Generic, picked?.Id);
        }
        return null;
    }

    private static (ushort Id, ArtworkGroup Art)? ModelForName(string? name, bool requireCatalogMatch)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string best = "";
        (ushort, ArtworkGroup)? found = null;
        foreach (ushort id in AppleModelCatalog.ListedIds)
        {
            string? model = AppleModelCatalog.NameOf(id);
            if (model is null || model.Length <= best.Length) continue;
            if (name.Contains(model, StringComparison.OrdinalIgnoreCase))
            {
                best = model;
                found = (id, AppleModelCatalog.ArtworkOf(id));
            }
        }
        if (found is not null || requireCatalogMatch) return found;
        return name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ? (PlainAirPods, ArtworkGroup.StemEarbuds) : null;
    }
}
