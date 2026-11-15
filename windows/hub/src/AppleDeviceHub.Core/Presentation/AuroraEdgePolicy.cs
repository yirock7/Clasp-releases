namespace AppleDeviceHub.Core.Presentation;

/// <summary>How the aurora edge around a window is shown.</summary>
public enum AuroraEdgeMode
{
    /// <summary>Not drawn at all: the window keeps its plain glass edge.</summary>
    Off,

    /// <summary>Drawn at its resting look, with no opening glow.</summary>
    Resting,

    /// <summary>Drawn at its resting look, and brighter for a moment each time the window opens.</summary>
    Glowing,
}

/// <summary>
/// Decides whether the aurora edge is shown. It is off in High Contrast and whenever the app is drawing
/// opaque surfaces (Windows transparency effects off, or the app's own reduced-transparency override);
/// reduced motion keeps the resting look but skips the opening glow.
/// </summary>
public static class AuroraEdgePolicy
{
    public static AuroraEdgeMode Decide(bool highContrast, bool reduceTransparency, bool reduceMotion)
    {
        if (highContrast || reduceTransparency) return AuroraEdgeMode.Off;
        return reduceMotion ? AuroraEdgeMode.Resting : AuroraEdgeMode.Glowing;
    }

    /// <summary>
    /// Opacity of ring <paramref name="index"/> (0 = outermost) out of <paramref name="rings"/>: a (1 - t)^2.2 taper,
    /// so the colour fades to nothing by the inner edge without a visible step.
    /// </summary>
    public static double RingOpacity(int index, int rings)
    {
        if (rings <= 0 || index < 0 || index >= rings) return 0;
        return Math.Pow(1.0 - (double)index / rings, 2.2);
    }
}
