// SPDX-License-Identifier: GPL-3.0-or-later
//
// NOTICE / attribution: the model-id values below are taken from the model table in LibrePods
// (https://github.com/kavishdevar/librepods), commit 4589176f7b7943a6091d5c6907c74f441bcf7780,
// file linux/ble/blemanager.cpp (getModelName), licensed GPL-3.0. They are UPSTREAM ASSUMPTIONS.
// That table has NO AirPods Pro 3 entry. 0x2720 (Pro 3) was added from this project's own observation:
// it was seen in a real broadcast from the owner's pair (wire bytes 27 20), and the same pair's Windows
// device ids carry VID&0001004C_PID&2027 (the byte-swapped Windows form). Treat it as observed on
// one pair, not as an upstream fact. All ids here are in wire order.
//
// The AirPods 5, AirPods Max 2, Beats and Powerbeats ids, and each model's battery layout and wear rule,
// were added from two further GPL-3.0 projects: CAPod (https://github.com/d4rken-org/capod) and
// MagicPodsCore (https://github.com/steam3d/MagicPodsCore). See docs/MODEL-AWARE-BATTERY-PLAN.md for
// the model table and its weaker rows. Rows marked provisional have no captured broadcast.

namespace AppleDeviceHub.Core.Ble;

/// <summary>How a broadcast's model id relates to the allow-list.</summary>
public enum ModelRecognition
{
    /// <summary>The id is in the allow-list; <see cref="AppleModelCatalog.NameOf"/> gives a model name.</summary>
    Recognised = 1,

    /// <summary>
    /// An Apple proximity message whose id is NOT in the allow-list (for example a model newer
    /// than this table). Treated as an unrecognised Apple audio device: no model
    /// name is claimed.
    /// </summary>
    Unrecognised = 2,
}

/// <summary>Brand of a listed model.</summary>
public enum ModelFamily
{
    AirPods = 1,
    Beats = 2,
}

/// <summary>Which batteries a model's broadcast carries. The bytes alone cannot tell, so the model id decides.</summary>
public enum BatteryLayout
{
    /// <summary>Two buds and a case.</summary>
    EarbudsWithCase = 0,

    /// <summary>Two buds, and the case reports no battery.</summary>
    EarbudsNoCaseBattery = 1,

    /// <summary>One battery (headphones or neckband).</summary>
    SingleBattery = 2,
}

/// <summary>Which status bits, if any, mean "worn" for a model.</summary>
public enum WearRule
{
    None = 0,

    /// <summary>The earbud XOR rule (bits 0x02 and 0x08, swapped by primary side and in-case).</summary>
    EarbudBits = 1,

    /// <summary>Headset worn bit: status bit 5 (0x20), both sides together.</summary>
    HeadsetBit5 = 2,

    /// <summary>Headset worn bits: left = bit 1 (0x02), right = bit 3 (0x08).</summary>
    HeadsetBits1And3 = 3,
}

/// <summary>Which drawing a model uses. One drawing per group, not per model.</summary>
public enum ArtworkGroup
{
    /// <summary>Plain headphones: unlisted ids, no known model, and Beats 360.</summary>
    Generic = 0,
    StemEarbuds,
    ProEarbuds,
    MaxHeadphones,
    OverEarHeadphones,
    OnEarHeadphones,
    BudsWithCase,
    EarHookWithCase,
    EarHookCorded,
    Neckband,
}

/// <summary>Whether a group is drawn with a charging case.</summary>
public static class ArtworkGroupExtensions
{
    public static bool HasCase(this ArtworkGroup group) => group is ArtworkGroup.StemEarbuds or ArtworkGroup.ProEarbuds
        or ArtworkGroup.BudsWithCase or ArtworkGroup.EarHookWithCase;
}

/// <summary>One listed model: its wire id, display name and how its broadcast is laid out.</summary>
public sealed record AppleModelInfo(ushort Id, string Name, ModelFamily Family, BatteryLayout Layout, WearRule Wear, ArtworkGroup Artwork);

/// <summary>Allow-list of proximity-message model ids (upstream assumption; not verified here).</summary>
public static class AppleModelCatalog
{
    /// <summary>What the UI says when the id is not in the allow-list. Never a specific model name.</summary>
    public const string UnrecognisedName = "Unrecognised Apple audio device";

    private const BatteryLayout Case = BatteryLayout.EarbudsWithCase;
    private const BatteryLayout NoCaseBattery = BatteryLayout.EarbudsNoCaseBattery;
    private const BatteryLayout Single = BatteryLayout.SingleBattery;

    private static readonly AppleModelInfo[] Rows =
    [
        new(0x0220, "AirPods (1st generation)", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.StemEarbuds),
        new(0x0F20, "AirPods (2nd generation)", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.StemEarbuds),
        new(0x1320, "AirPods (3rd generation)", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.StemEarbuds),
        new(0x1920, "AirPods 4", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.StemEarbuds),
        new(0x1B20, "AirPods 4 (ANC)", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.StemEarbuds),
        // Sources disagree on which AirPods 5 version uses 0x3620 and which uses 0x3020.
        new(0x3620, "AirPods 5", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.StemEarbuds),
        new(0x3020, "AirPods 5 (wireless case)", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.StemEarbuds),
        new(0x0E20, "AirPods Pro", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.ProEarbuds),
        new(0x1420, "AirPods Pro 2 (Lightning)", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.ProEarbuds),
        new(0x2420, "AirPods Pro 2 (USB-C)", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.ProEarbuds),
        new(0x2720, "AirPods Pro 3", ModelFamily.AirPods, Case, WearRule.EarbudBits, ArtworkGroup.ProEarbuds),
        // The wear rule is from one app only.
        new(0x0A20, "AirPods Max (Lightning)", ModelFamily.AirPods, Single, WearRule.HeadsetBit5, ArtworkGroup.MaxHeadphones),
        // The only known frame cannot separate the two Max wear rules, so none is applied.
        new(0x1F20, "AirPods Max (USB-C)", ModelFamily.AirPods, Single, WearRule.None, ArtworkGroup.MaxHeadphones),
        // Wear bits are from one user's report.
        new(0x2D20, "AirPods Max 2", ModelFamily.AirPods, Single, WearRule.HeadsetBits1And3, ArtworkGroup.MaxHeadphones),
        // PROVISIONAL: layout has only a handcrafted frame.
        new(0x0320, "Powerbeats 3", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.EarHookCorded),
        new(0x0D20, "Powerbeats 4", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.EarHookCorded),
        new(0x0B20, "Powerbeats Pro", ModelFamily.Beats, Case, WearRule.EarbudBits, ArtworkGroup.EarHookWithCase),
        new(0x1D20, "Powerbeats Pro 2", ModelFamily.Beats, Case, WearRule.EarbudBits, ArtworkGroup.EarHookWithCase),
        // PROVISIONAL: id derived, never seen in a broadcast; layout assumed.
        new(0x2F20, "Powerbeats Fit", ModelFamily.Beats, Case, WearRule.None, ArtworkGroup.BudsWithCase),
        new(0x0520, "BeatsX", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.Neckband),
        new(0x1020, "Beats Flex", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.Neckband),
        // PROVISIONAL: layout has only a handcrafted frame.
        new(0x0620, "Beats Solo 3", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.OnEarHeadphones),
        // PROVISIONAL: layout has only a handcrafted frame.
        new(0x0C20, "Beats Solo Pro", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.OnEarHeadphones),
        new(0x2520, "Beats Solo 4", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.OnEarHeadphones),
        new(0x2620, "Beats Solo Buds", ModelFamily.Beats, NoCaseBattery, WearRule.None, ArtworkGroup.BudsWithCase),
        // PROVISIONAL: layout has only a handcrafted frame.
        new(0x0920, "Beats Studio 3", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.OverEarHeadphones),
        new(0x1720, "Beats Studio Pro", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.OverEarHeadphones),
        // Beats 360: its physical shape is not established by any source here, so it draws as Generic.
        new(0x3820, "Beats 360", ModelFamily.Beats, Single, WearRule.None, ArtworkGroup.Generic),
        // PROVISIONAL: layout has only a handcrafted frame.
        new(0x1120, "Beats Studio Buds", ModelFamily.Beats, Case, WearRule.None, ArtworkGroup.BudsWithCase),
        new(0x1620, "Beats Studio Buds+", ModelFamily.Beats, Case, WearRule.None, ArtworkGroup.BudsWithCase),
        new(0x1220, "Beats Fit Pro", ModelFamily.Beats, Case, WearRule.EarbudBits, ArtworkGroup.BudsWithCase),
    ];

    private static readonly Dictionary<ushort, AppleModelInfo> ById = Rows.ToDictionary(r => r.Id);

    public static IReadOnlyCollection<ushort> ListedIds => ById.Keys;

    public static ModelRecognition Recognise(ushort modelId)
        => ById.ContainsKey(modelId) ? ModelRecognition.Recognised : ModelRecognition.Unrecognised;

    /// <summary>The listed model, or null for an unlisted id (never a guess).</summary>
    public static AppleModelInfo? Find(ushort modelId) => ById.GetValueOrDefault(modelId);

    /// <summary>The listed model name, or null for an unlisted id (never a guess).</summary>
    public static string? NameOf(ushort modelId) => Find(modelId)?.Name;

    /// <summary>Display text: the listed name, or <see cref="UnrecognisedName"/> with the raw id.</summary>
    public static string DisplayNameOf(ushort modelId)
        => NameOf(modelId) ?? $"{UnrecognisedName} (id 0x{modelId:X4})";

    /// <summary>"AirPods" or "Beats" for a listed id; "headphones" when the id is missing or unlisted.</summary>
    public static string FamilyNounOf(ushort? modelId)
        => modelId is ushort id && Find(id) is { } info
            ? (info.Family == ModelFamily.Beats ? "Beats" : "AirPods")
            : "headphones";

    /// <summary>The drawing group for a model id; <see cref="ArtworkGroup.Generic"/> for null or an unlisted id.</summary>
    public static ArtworkGroup ArtworkOf(ushort? modelId)
        => modelId is ushort id && Find(id) is { } info ? info.Artwork : ArtworkGroup.Generic;

    /// <summary>
    /// The drawing group, also using the battery layout when the id is missing or unlisted: an earbud layout (the rings
    /// show buds and a case) gets the stem-earbuds-with-case drawing so the art never contradicts the rings; a
    /// single-battery layout stays <see cref="ArtworkGroup.Generic"/>. A listed id always keeps its own group.
    /// </summary>
    public static ArtworkGroup ArtworkOf(ushort? modelId, BatteryLayout layout)
        => modelId is ushort id && Find(id) is { } info ? info.Artwork
            : layout is BatteryLayout.EarbudsWithCase or BatteryLayout.EarbudsNoCaseBattery ? ArtworkGroup.StemEarbuds
            : ArtworkGroup.Generic;
}
