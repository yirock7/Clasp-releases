// SPDX-License-Identifier: GPL-3.0-or-later
//
// How to put each listed AirPods or Beats model into pairing mode, so it can be paired in Windows.
// The wording follows Apple Support and Beats Support. Sources and the models with no source are
// recorded in docs/PAIRING-MODES.md. A model with no source gets the fallback guide, never a guess.

namespace AppleDeviceHub.Core.Ble;

/// <summary>One pairing guide: a picker label, a short title and 2 to 4 plain steps, shared by one or more models.</summary>
public sealed record PairingGuide(string Key, string Label, string Title, IReadOnlyList<string> Steps, IReadOnlyList<ushort> ModelIds)
{
    /// <summary>True for the "check your model's guide" entry, which has no model-specific steps.</summary>
    public bool IsFallback => Key == PairingInstructions.FallbackKey;
}

/// <summary>Pairing-mode steps for every model in <see cref="AppleModelCatalog"/>.</summary>
public static class PairingInstructions
{
    public const string FallbackKey = "check-guide";

    private const string ChooseInWindows = "In Windows, open Bluetooth settings and choose it.";

    private static readonly PairingGuide[] All =
    [
        new("airpods-setup-button", "AirPods (1st to 3rd generation), AirPods Pro, AirPods Pro 2",
            "AirPods with a setup button",
            ["Put both AirPods in the case and open the lid.",
             "Press and hold the setup button on the back of the case for about 5 seconds. The light flashes white.",
             "In Windows, open Bluetooth settings and choose your AirPods."],
            [0x0220, 0x0F20, 0x1320, 0x0E20, 0x1420, 0x2420]),

        new("airpods-double-tap", "AirPods 4, AirPods 5, AirPods Pro 3",
            "AirPods with no setup button",
            ["Put both AirPods in the case and open the lid.",
             "Double-tap the front of the case. The light flashes white.",
             "In Windows, open Bluetooth settings and choose your AirPods."],
            [0x1920, 0x1B20, 0x3620, 0x3020, 0x2720]),

        new("airpods-max", "AirPods Max",
            "AirPods Max",
            ["Make sure your AirPods Max are on.",
             "Press and hold the noise control button (the listening mode button, on the right earcup next to the Digital Crown) for about 5 seconds. The light flashes white.",
             "In Windows, open Bluetooth settings and choose your AirPods Max."],
            [0x0A20, 0x1F20, 0x2D20]),

        new("beats-case-system-button", "Beats Fit Pro, Studio Buds +, Powerbeats Fit, Powerbeats Pro 2",
            "Beats earbuds with a case",
            ["Put both earbuds in the case and open the lid.",
             "Press and hold the system button on the case until the light flashes white.",
             "In Windows, open Bluetooth settings and choose your Beats."],
            [0x1220, 0x1620, 0x2F20, 0x1D20]),

        new("beats-studio-buds", "Beats Studio Buds",
            "Beats Studio Buds",
            ["Put both earbuds in the case and open the lid.",
             "Press and hold the small round button on the case until the light flashes.",
             ChooseInWindows],
            [0x1120]),

        new("beats-solo-buds", "Beats Solo Buds",
            "Beats Solo Buds",
            ["Put both earbuds in the case and open the lid.",
             "Press and hold the button above the b logo on either earbud until a tone repeats.",
             ChooseInWindows],
            [0x2620]),

        new("beats-flex", "Beats Flex",
            "Beats Flex",
            ["Make sure your Beats Flex are on.",
             "Press and hold the power button on the right control module until the light flashes.",
             ChooseInWindows],
            [0x1020]),

        new("beats-solo-4", "Beats Solo 4",
            "Beats Solo 4",
            ["Make sure your Beats are on.",
             "Press and hold the power button on the right earcup until the light flashes.",
             ChooseInWindows],
            [0x2520]),

        new("beats-studio-pro", "Beats Studio Pro",
            "Beats Studio Pro",
            ["Make sure your Beats are on.",
             "Press and hold the system button on the right earcup until the light flashes.",
             ChooseInWindows],
            [0x1720]),

        new("beats-360", "Beats 360",
            "Beats 360",
            ["Make sure your Beats are on.",
             "Press and hold the power button on the back of the left earcup until the light flashes.",
             ChooseInWindows],
            [0x3820]),

        new("beats-solo-3", "Beats Solo 3",
            "Beats Solo 3",
            ["Press and hold the power button for 5 seconds, until the Fuel Gauge lights flash.",
             "In Windows, open Bluetooth settings and choose your headphones."],
            [0x0620]),

        // No primary source was confirmed for these models, so no steps are claimed for them.
        new(FallbackKey, "Powerbeats 3, Powerbeats 4, Powerbeats Pro, BeatsX, Solo Pro, Studio 3",
            "Check your model's guide",
            ["Find pairing mode in the guide that came with your Beats, or on Beats Support.",
             "Put your Beats in pairing mode as the guide shows.",
             "In Windows, open Bluetooth settings and choose them."],
            [0x0320, 0x0D20, 0x0B20, 0x0520, 0x0C20, 0x0920]),
    ];

    private static readonly Dictionary<ushort, PairingGuide> ById = BuildIndex();

    /// <summary>Every guide, in picker order. The last one is the fallback.</summary>
    public static IReadOnlyList<PairingGuide> Guides => All;

    /// <summary>The guide shown when a model is unknown or unlisted.</summary>
    public static PairingGuide Fallback => All[^1];

    /// <summary>The guide for a catalog model id, or null when the id is not in the catalog.</summary>
    public static PairingGuide? Find(ushort modelId) => ById.GetValueOrDefault(modelId);

    /// <summary>The guide for a model id; the first guide when the id is missing, the fallback when it is unlisted.</summary>
    public static PairingGuide ForModel(ushort? modelId)
        => modelId is ushort id ? Find(id) ?? Fallback : All[0];

    /// <summary>The guide for a catalog model name (for example "AirPods Pro 3"), or null when the name is not listed.</summary>
    public static PairingGuide? FindByModelName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        foreach (ushort id in AppleModelCatalog.ListedIds)
        {
            if (string.Equals(AppleModelCatalog.NameOf(id), modelName, StringComparison.OrdinalIgnoreCase)) return Find(id);
        }
        return null;
    }

    private static Dictionary<ushort, PairingGuide> BuildIndex()
    {
        var index = new Dictionary<ushort, PairingGuide>();
        foreach (var guide in All)
        {
            foreach (ushort id in guide.ModelIds) index.Add(id, guide);
        }
        return index;
    }
}
