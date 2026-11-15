using System.Globalization;
using AppleDeviceHub.Core.Resources;
using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// One place that decides how much visual effect the app may use. Combines the Windows
/// settings (Animation effects, Transparency effects, High contrast) with the app's own
/// overrides from Settings. Everything that animates or draws translucency asks here.
/// FROZEN for phase one: views read it; only the orchestrator edits it.
/// </summary>
public static class EffectsPolicy
{
    private static UISettings? s_ui;
    private static AccessibilitySettings? s_a11y;
    private static DispatcherQueue? s_queue;
    private static bool s_overrideReduceMotion;
    private static bool s_overrideReduceTransparency;

    /// <summary>Windows "Animation effects" setting.</summary>
    public static bool SystemAnimationsEnabled { get; private set; } = true;

    /// <summary>Windows "Transparency effects" setting.</summary>
    public static bool SystemTransparencyEnabled { get; private set; } = true;

    /// <summary>A Windows contrast theme is active.</summary>
    public static bool HighContrast { get; private set; }

    /// <summary>No scale, translation, or spring motion; state changes are immediate (short fades allowed).</summary>
    public static bool ReduceMotion => s_overrideReduceMotion || !SystemAnimationsEnabled;

    /// <summary>Use opaque surfaces and no desktop backdrop. Always true in high contrast.</summary>
    public static bool ReduceTransparency => s_overrideReduceTransparency || !SystemTransparencyEnabled || HighContrast;

    /// <summary>Raised on the UI thread whenever any of the values above may have changed.</summary>
    public static event EventHandler? Changed;

    public static void Initialize(DispatcherQueue uiQueue)
    {
        s_queue = uiQueue;
        try
        {
            s_ui = new UISettings();
            s_a11y = new AccessibilitySettings();
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                s_ui.AnimationsEnabledChanged += (_, _) => Post();
            s_ui.AdvancedEffectsEnabledChanged += (_, _) => Post();
            s_a11y.HighContrastChanged += (_, _) => Post();
        }
        catch
        {
            // Settings unavailable: keep defaults (effects on). Refresh() retries the read.
        }
        Read();
    }

    /// <summary>Apply the app's Settings overrides. Call on the UI thread.</summary>
    public static void SetOverrides(bool reduceMotion, bool reduceTransparency)
    {
        if (s_overrideReduceMotion == reduceMotion && s_overrideReduceTransparency == reduceTransparency) return;
        s_overrideReduceMotion = reduceMotion;
        s_overrideReduceTransparency = reduceTransparency;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Re-read the Windows settings (also called when a surface opens, because unpackaged apps
    /// may not receive every change event). Raises <see cref="Changed"/> only on a real change.
    /// </summary>
    public static void Refresh()
    {
        if (Read()) Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>A one-line description for Settings and the design review.</summary>
    public static string Describe() => string.Format(
        CultureInfo.CurrentCulture,
        Strings.Design_Effects_Summary,
        SystemAnimationsEnabled ? Strings.Design_Effects_On : Strings.Design_Effects_Off,
        SystemTransparencyEnabled ? Strings.Design_Effects_On : Strings.Design_Effects_Off,
        HighContrast ? Strings.Design_Effects_On : Strings.Design_Effects_Off,
        ReduceMotion ? Strings.Design_Effects_ReducedMotion : Strings.Design_Effects_FullMotion,
        ReduceTransparency ? Strings.Design_Effects_OpaqueSurfaces : Strings.Design_Effects_GlassSurfaces);

    private static void Post() => s_queue?.TryEnqueue(Refresh);

    private static bool Read()
    {
        try
        {
            bool animations = s_ui?.AnimationsEnabled ?? true;
            bool transparency = s_ui?.AdvancedEffectsEnabled ?? true;
            bool contrast = s_a11y?.HighContrast ?? false;
            bool changed = animations != SystemAnimationsEnabled
                || transparency != SystemTransparencyEnabled
                || contrast != HighContrast;
            SystemAnimationsEnabled = animations;
            SystemTransparencyEnabled = transparency;
            HighContrast = contrast;
            return changed;
        }
        catch
        {
            return false;
        }
    }
}
