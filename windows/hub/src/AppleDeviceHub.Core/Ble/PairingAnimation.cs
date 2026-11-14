// SPDX-License-Identifier: GPL-3.0-or-later

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
    public string Spoken => $"{Caption}. {LightText}.";
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
        var description = cue is null ? DescribeKind(kind) : DescribeKind(kind) + ". " + cue.Spoken;
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
        PairingAnimationKind.CaseSetupButton => "Open the lid, then press and hold the button on the back of the case until the light flashes white",
        PairingAnimationKind.CaseDoubleTap => "Open the lid, then double-tap the front of the case until the light flashes white",
        PairingAnimationKind.MaxButton => "Press and hold the noise control button until the light flashes white",
        PairingAnimationKind.CaseButton => "Open the lid, then press and hold the button on the case until the light flashes",
        PairingAnimationKind.EarbudButton => "With the earbuds in the open case, press and hold the button on an earbud until a tone repeats",
        PairingAnimationKind.EarcupButton => "Press and hold the button on the earcup until the light flashes",
        PairingAnimationKind.ModuleButton => "Press and hold the power button on the control module until the light flashes",
        PairingAnimationKind.RearEarcupButton => "Press and hold the power button on the back of the earcup until the light flashes",
        _ => "Your headphones or earbuds",
    };

    /// <summary>
    /// The caption, location, gesture and light for a guide, or null for the fallback guide (no animation, so no marker).
    /// Hold times are only given where Apple or Beats publish one; otherwise the caption says "until" the light or tone.
    /// </summary>
    public static PairingCue? CueOf(string guideKey) => guideKey switch
    {
        "airpods-setup-button" => new(PairingLocation.Back, PairingGesture.PressAndHold, 5, 0,
            "Hold the setup button on the back of the case \u00B7 5 s", "The light flashes white"),
        "airpods-double-tap" => new(PairingLocation.CaseFront, PairingGesture.DoubleTap, null, 2,
            "Double-tap the front of the case", "The light flashes white"),
        "airpods-max" => new(PairingLocation.EarCup, PairingGesture.PressAndHold, 5, 0,
            "Hold the noise control button \u00B7 5 s", "The light flashes white"),
        "beats-case-system-button" => new(PairingLocation.InsideCase, PairingGesture.PressAndHold, null, 0,
            "Hold the system button on the case", "The light flashes white"),
        "beats-studio-buds" => new(PairingLocation.InsideCase, PairingGesture.PressAndHold, null, 0,
            "Hold the small round button on the case", "The light flashes"),
        "beats-solo-buds" => new(PairingLocation.Earbud, PairingGesture.PressAndHold, null, 0,
            "Hold the button above the b on a bud", "A tone repeats"),
        "beats-flex" => new(PairingLocation.Module, PairingGesture.PressAndHold, null, 0,
            "Hold the power button on the right module", "The light flashes"),
        "beats-solo-4" => new(PairingLocation.EarCup, PairingGesture.PressAndHold, null, 0,
            "Hold the power button on the right earcup", "The light flashes"),
        "beats-studio-pro" => new(PairingLocation.EarCup, PairingGesture.PressAndHold, null, 0,
            "Hold the system button on the right earcup", "The light flashes"),
        "beats-360" => new(PairingLocation.Back, PairingGesture.PressAndHold, null, 0,
            "Hold the power button on the back of the left earcup", "The light flashes"),
        "beats-solo-3" => new(PairingLocation.EarCup, PairingGesture.PressAndHold, 5, 0,
            "Hold the power button \u00B7 5 s", "The Fuel Gauge lights flash"),
        _ => null,
    };
}
