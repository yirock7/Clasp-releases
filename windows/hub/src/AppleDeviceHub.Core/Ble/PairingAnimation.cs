// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Ble;

/// <summary>Which small animation illustrates a pairing guide.</summary>
public enum PairingAnimationKind
{
    /// <summary>No animation: the guide has no model-specific action, so only the artwork shows.</summary>
    None = 0,

    /// <summary>Lid opens, the setup button on the back of the case pulses, the light flashes white.</summary>
    CaseSetupButton,

    /// <summary>Lid opens, two taps ripple on the front of the case, the light flashes white.</summary>
    CaseDoubleTap,

    /// <summary>A press-and-hold ring on the AirPods Max noise control button, then the light flashes.</summary>
    MaxButton,

    /// <summary>Lid opens, a press-and-hold ring on the case button, then the light flashes.</summary>
    CaseButton,

    /// <summary>A press-and-hold ring on the button of an earbud in the open case, then a tone pulse.</summary>
    EarbudButton,

    /// <summary>A press-and-hold ring on an earcup button, then the light flashes.</summary>
    EarcupButton,

    /// <summary>A press-and-hold ring on the control module button, then the light flashes.</summary>
    ModuleButton,

    /// <summary>A press-and-hold ring on a button on the back of the left earcup (shown dashed, with a go-around arrow), then the light flashes.</summary>
    RearEarcupButton,
}

/// <summary>Where on the device the control is. The drawings are front views, so Back is drawn differently from the rest.</summary>
public enum PairingLocation
{
    /// <summary>The front of the case.</summary>
    CaseFront,

    /// <summary>The back of the case or earcup, which a front drawing cannot show.</summary>
    Back,

    /// <summary>Inside the open case, with the earbuds.</summary>
    InsideCase,

    /// <summary>An earcup of over-ear or on-ear headphones.</summary>
    EarCup,

    /// <summary>On an earbud itself.</summary>
    Earbud,

    /// <summary>The control module of a neckband.</summary>
    Module,
}

/// <summary>What the user does to the control.</summary>
public enum PairingGesture
{
    PressAndHold,
    DoubleTap,
}

/// <summary>
/// What the animation says in words next to its marker: where the control is, the gesture, how long to hold it (when the
/// manufacturer gives a time) and what the light or tone does. <see cref="Caption"/> is the short chip text;
/// <see cref="LightText"/> is the second line and also appears in the guide's step text.
/// </summary>
public sealed record PairingCue(PairingLocation Location, PairingGesture Gesture, int? HoldSeconds, int Taps, string Caption, string LightText)
{
    /// <summary>The chip's full text, as read by screen readers: the caption, then what the light does.</summary>
    public string Spoken => string.Format(CultureInfo.CurrentCulture, Strings.PairingAnimation_Spoken, Caption, LightText);
}

/// <summary>
/// An animation kind, the artwork it plays on (the group, plus the model id whose per-model drawing to show; null for the
/// fallback guide), and a sentence for screen readers.
/// </summary>
public sealed record PairingAnimationPlan(PairingAnimationKind Kind, ArtworkGroup Art, string Description, ushort? ModelId = null, PairingCue? Cue = null)
{
    public bool IsAnimated => Kind != PairingAnimationKind.None;
}

/// <summary>Maps each pairing guide to its animation.</summary>
public static class PairingAnimations
{
    public static PairingAnimationPlan For(PairingGuide guide)
    {
        var kind = KindOf(guide.Key);
        ushort? modelId = guide.IsFallback || guide.ModelIds.Count == 0 ? null : guide.ModelIds[0];
        var cue = CueOf(guide.Key);
        var description = cue is null ? DescribeKind(kind) : string.Format(CultureInfo.CurrentCulture, Strings.PairingAnimation_DescriptionWithCue, DescribeKind(kind), cue.Spoken);
        return new PairingAnimationPlan(kind, AppleModelCatalog.ArtworkOf(modelId), description, modelId, cue);
    }

    public static PairingAnimationKind KindOf(string guideKey) => guideKey switch
    {
        "airpods-setup-button" => PairingAnimationKind.CaseSetupButton,
        "airpods-double-tap" => PairingAnimationKind.CaseDoubleTap,
        "airpods-max" => PairingAnimationKind.MaxButton,
        "beats-case-system-button" or "beats-studio-buds" => PairingAnimationKind.CaseButton,
        "beats-solo-buds" => PairingAnimationKind.EarbudButton,
        "beats-solo-4" or "beats-studio-pro" or "beats-solo-3" => PairingAnimationKind.EarcupButton,
        "beats-360" => PairingAnimationKind.RearEarcupButton,
        "beats-flex" => PairingAnimationKind.ModuleButton,
        _ => PairingAnimationKind.None,
    };

    public static string DescribeKind(PairingAnimationKind kind) => kind switch
    {
        PairingAnimationKind.CaseSetupButton => Strings.PairingAnimation_Kind_CaseSetupButton,
        PairingAnimationKind.CaseDoubleTap => Strings.PairingAnimation_Kind_CaseDoubleTap,
        PairingAnimationKind.MaxButton => Strings.PairingAnimation_Kind_MaxButton,
        PairingAnimationKind.CaseButton => Strings.PairingAnimation_Kind_CaseButton,
        PairingAnimationKind.EarbudButton => Strings.PairingAnimation_Kind_EarbudButton,
        PairingAnimationKind.EarcupButton => Strings.PairingAnimation_Kind_EarcupButton,
        PairingAnimationKind.ModuleButton => Strings.PairingAnimation_Kind_ModuleButton,
        PairingAnimationKind.RearEarcupButton => Strings.PairingAnimation_Kind_RearEarcupButton,
        _ => Strings.PairingAnimation_Kind_None,
    };

    /// <summary>
    /// The caption, location, gesture and light for a guide, or null for the fallback guide (no animation, so no marker).
    /// Hold times are only given where Apple or Beats publish one; otherwise the caption says "until" the light or tone.
    /// </summary>
    public static PairingCue? CueOf(string guideKey) => guideKey switch
    {
        "airpods-setup-button" => new(PairingLocation.Back, PairingGesture.PressAndHold, 5, 0,
            Strings.PairingAnimation_Cue_AirpodsSetupButton_Caption, Strings.PairingAnimation_Light_FlashesWhite),
        "airpods-double-tap" => new(PairingLocation.CaseFront, PairingGesture.DoubleTap, null, 2,
            Strings.PairingAnimation_Cue_AirpodsDoubleTap_Caption, Strings.PairingAnimation_Light_FlashesWhite),
        "airpods-max" => new(PairingLocation.EarCup, PairingGesture.PressAndHold, 5, 0,
            Strings.PairingAnimation_Cue_AirpodsMax_Caption, Strings.PairingAnimation_Light_FlashesWhite),
        "beats-case-system-button" => new(PairingLocation.InsideCase, PairingGesture.PressAndHold, null, 0,
            Strings.PairingAnimation_Cue_BeatsCaseSystemButton_Caption, Strings.PairingAnimation_Light_FlashesWhite),
        "beats-studio-buds" => new(PairingLocation.InsideCase, PairingGesture.PressAndHold, null, 0,
            Strings.PairingAnimation_Cue_BeatsStudioBuds_Caption, Strings.PairingAnimation_Light_Flashes),
        "beats-solo-buds" => new(PairingLocation.Earbud, PairingGesture.PressAndHold, null, 0,
            Strings.PairingAnimation_Cue_BeatsSoloBuds_Caption, Strings.PairingAnimation_Light_ToneRepeats),
        "beats-flex" => new(PairingLocation.Module, PairingGesture.PressAndHold, null, 0,
            Strings.PairingAnimation_Cue_BeatsFlex_Caption, Strings.PairingAnimation_Light_Flashes),
        "beats-solo-4" => new(PairingLocation.EarCup, PairingGesture.PressAndHold, null, 0,
            Strings.PairingAnimation_Cue_BeatsSolo4_Caption, Strings.PairingAnimation_Light_Flashes),
        "beats-studio-pro" => new(PairingLocation.EarCup, PairingGesture.PressAndHold, null, 0,
            Strings.PairingAnimation_Cue_BeatsStudioPro_Caption, Strings.PairingAnimation_Light_Flashes),
        "beats-360" => new(PairingLocation.Back, PairingGesture.PressAndHold, null, 0,
            Strings.PairingAnimation_Cue_Beats360_Caption, Strings.PairingAnimation_Light_Flashes),
        "beats-solo-3" => new(PairingLocation.EarCup, PairingGesture.PressAndHold, 5, 0,
            Strings.PairingAnimation_Cue_BeatsSolo3_Caption, Strings.PairingAnimation_Light_FuelGaugeFlash),
        _ => null,
    };
}
