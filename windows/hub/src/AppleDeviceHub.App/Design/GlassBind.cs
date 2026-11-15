using AppleDeviceHub.Core.Resources;
using Microsoft.UI.Xaml;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// Small pure functions for x:Bind function bindings (no converters, no resource lookups):
/// <c>Visibility="{x:Bind design:GlassBind.Visible(ViewModel.IsDemo), Mode=OneWay}"</c>.
/// </summary>
public static class GlassBind
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    /// <summary>Opacity for decoration that belongs to a control: full while it is enabled, dimmed while it is not.</summary>
    public static double DimIfDisabled(bool enabled) => enabled ? 1.0 : 0.4;

    /// <summary>Visible only when both flags are false (the "neither" member of a three-way state).</summary>
    public static Visibility VisibleIfNeither(bool a, bool b) => !a && !b ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Visible when either flag is true.</summary>
    public static Visibility VisibleIfEither(bool a, bool b) => a || b ? Visibility.Visible : Visibility.Collapsed;

    public static double ToDouble(int value) => value;

    /// <summary>0..1 level to 0..100 for ProgressBar/Slider values.</summary>
    public static double Percent(double level01) => Math.Clamp(level01, 0, 1) * 100.0;

    /// <summary>"62" for a 0..100 slider value (whole numbers only).</summary>
    public static string WholeNumber(double value) => ((int)Math.Round(value)).ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Why the Adaptive strength slider is unavailable, or "" when it is usable.</summary>
    public static string StrengthCaption(bool enabled, bool known)
        => !known ? Strings.Design_Bind_StrengthNotReported : enabled ? "" : Strings.Design_Bind_StrengthAdaptiveOnly;

    /// <summary>Visibility for <see cref="StrengthCaption"/> (x:Bind arguments cannot nest functions).</summary>
    public static Visibility StrengthCaptionVisibility(bool enabled, bool known)
        => known && enabled ? Visibility.Collapsed : Visibility.Visible;
}
