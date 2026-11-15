// SPDX-License-Identifier: GPL-3.0-or-later
//
// How to put each listed AirPods or Beats model into pairing mode, so it can be paired in Windows.
// The wording follows Apple Support and Beats Support. Sources and the models with no source are
// recorded in docs/PAIRING-MODES.md. A model with no source gets the fallback guide, never a guess.

using AppleDeviceHub.Core.Resources;

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

    // Built on first use, after the startup language is set; the language only changes with a restart.
    private static readonly PairingGuide[] All =
    [
        new("airpods-setup-button", Strings.Ble_PairingGuide_AirpodsSetupButton_Label,
            Strings.Ble_PairingGuide_AirpodsSetupButton_Title,
            [Strings.Ble_PairingGuide_Step_PutBothAirPodsInCase,
             Strings.Ble_PairingGuide_AirpodsSetupButton_Step2,
             Strings.Ble_PairingGuide_Step_ChooseYourAirPods],
            [0x0220, 0x0F20, 0x1320, 0x0E20, 0x1420, 0x2420]),

        new("airpods-double-tap", Strings.Ble_PairingGuide_AirpodsDoubleTap_Label,
            Strings.Ble_PairingGuide_AirpodsDoubleTap_Title,
            [Strings.Ble_PairingGuide_Step_PutBothAirPodsInCase,
             Strings.Ble_PairingGuide_AirpodsDoubleTap_Step2,
             Strings.Ble_PairingGuide_Step_ChooseYourAirPods],
            [0x1920, 0x1B20, 0x3620, 0x3020, 0x2720]),

        new("airpods-max", Strings.Ble_PairingGuide_AirpodsMax_Label,
            Strings.Ble_PairingGuide_AirpodsMax_Title,
            [Strings.Ble_PairingGuide_AirpodsMax_Step1,
             Strings.Ble_PairingGuide_AirpodsMax_Step2,
             Strings.Ble_PairingGuide_AirpodsMax_Step3],
            [0x0A20, 0x1F20, 0x2D20]),

        new("beats-case-system-button", Strings.Ble_PairingGuide_BeatsCaseSystemButton_Label,
            Strings.Ble_PairingGuide_BeatsCaseSystemButton_Title,
            [Strings.Ble_PairingGuide_Step_PutBothEarbudsInCase,
             Strings.Ble_PairingGuide_BeatsCaseSystemButton_Step2,
             Strings.Ble_PairingGuide_BeatsCaseSystemButton_Step3],
            [0x1220, 0x1620, 0x2F20, 0x1D20]),

        new("beats-studio-buds", Strings.Ble_PairingGuide_BeatsStudioBuds_Label,
            Strings.Ble_PairingGuide_BeatsStudioBuds_Title,
            [Strings.Ble_PairingGuide_Step_PutBothEarbudsInCase,
             Strings.Ble_PairingGuide_BeatsStudioBuds_Step2,
             Strings.Ble_PairingGuide_Step_ChooseInWindows],
            [0x1120]),

        new("beats-solo-buds", Strings.Ble_PairingGuide_BeatsSoloBuds_Label,
            Strings.Ble_PairingGuide_BeatsSoloBuds_Title,
            [Strings.Ble_PairingGuide_Step_PutBothEarbudsInCase,
             Strings.Ble_PairingGuide_BeatsSoloBuds_Step2,
             Strings.Ble_PairingGuide_Step_ChooseInWindows],
            [0x2620]),

        new("beats-flex", Strings.Ble_PairingGuide_BeatsFlex_Label,
            Strings.Ble_PairingGuide_BeatsFlex_Title,
            [Strings.Ble_PairingGuide_BeatsFlex_Step1,
             Strings.Ble_PairingGuide_BeatsFlex_Step2,
             Strings.Ble_PairingGuide_Step_ChooseInWindows],
            [0x1020]),

        new("beats-solo-4", Strings.Ble_PairingGuide_BeatsSolo4_Label,
            Strings.Ble_PairingGuide_BeatsSolo4_Title,
            [Strings.Ble_PairingGuide_Step_MakeSureBeatsOn,
             Strings.Ble_PairingGuide_BeatsSolo4_Step2,
             Strings.Ble_PairingGuide_Step_ChooseInWindows],
            [0x2520]),

        new("beats-studio-pro", Strings.Ble_PairingGuide_BeatsStudioPro_Label,
            Strings.Ble_PairingGuide_BeatsStudioPro_Title,
            [Strings.Ble_PairingGuide_Step_MakeSureBeatsOn,
             Strings.Ble_PairingGuide_BeatsStudioPro_Step2,
             Strings.Ble_PairingGuide_Step_ChooseInWindows],
            [0x1720]),

        new("beats-360", Strings.Ble_PairingGuide_Beats360_Label,
            Strings.Ble_PairingGuide_Beats360_Title,
            [Strings.Ble_PairingGuide_Step_MakeSureBeatsOn,
             Strings.Ble_PairingGuide_Beats360_Step2,
             Strings.Ble_PairingGuide_Step_ChooseInWindows],
            [0x3820]),

        new("beats-solo-3", Strings.Ble_PairingGuide_BeatsSolo3_Label,
            Strings.Ble_PairingGuide_BeatsSolo3_Title,
            [Strings.Ble_PairingGuide_BeatsSolo3_Step1,
             Strings.Ble_PairingGuide_BeatsSolo3_Step2],
            [0x0620]),

        // No primary source was confirmed for these models, so no steps are claimed for them.
        new(FallbackKey, Strings.Ble_PairingGuide_CheckGuide_Label,
            Strings.Ble_PairingGuide_CheckGuide_Title,
            [Strings.Ble_PairingGuide_CheckGuide_Step1,
             Strings.Ble_PairingGuide_CheckGuide_Step2,
             Strings.Ble_PairingGuide_CheckGuide_Step3],
            [0x0320, 0x0D20, 0x0B20, 0x0520, 0x0C20, 0x0920]),
    ];

    private static readonly Dictionary<ushort, PairingGuide> ById = BuildIndex();

    /// <summary>Every guide, in picker order. The last one is the fallback.</summary>
    public static IReadOnlyList<PairingGuide> Guides => All;

    /// <summary>
    /// The guide's steps for the in-app welcome guide: a last step that starts with "In Windows, open Bluetooth settings"
    /// is replaced by waiting for the device to appear in Clasp. Other steps are unchanged.
    /// </summary>
    public static IReadOnlyList<string> InAppSteps(PairingGuide guide)
    {
        ArgumentNullException.ThrowIfNull(guide);
        var steps = guide.Steps;
        if (steps.Count == 0 || !IsWindowsSettingsStep(steps[^1])) return steps;
        var result = new List<string>(steps);
        result[^1] = Strings.Ble_PairingGuide_InApp_WaitForDevice;
        return result;
    }

    /// <summary>True for the guides' last step that sends the user to Windows Bluetooth settings (matched by its exact text, in any language).</summary>
    private static bool IsWindowsSettingsStep(string step)
        => string.Equals(step, Strings.Ble_PairingGuide_Step_ChooseYourAirPods, StringComparison.Ordinal)
        || string.Equals(step, Strings.Ble_PairingGuide_Step_ChooseInWindows, StringComparison.Ordinal)
        || string.Equals(step, Strings.Ble_PairingGuide_AirpodsMax_Step3, StringComparison.Ordinal)
        || string.Equals(step, Strings.Ble_PairingGuide_BeatsCaseSystemButton_Step3, StringComparison.Ordinal)
        || string.Equals(step, Strings.Ble_PairingGuide_BeatsSolo3_Step2, StringComparison.Ordinal)
        || string.Equals(step, Strings.Ble_PairingGuide_CheckGuide_Step3, StringComparison.Ordinal);

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
