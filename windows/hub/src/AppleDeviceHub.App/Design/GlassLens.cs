using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// The "fake lens": the edges of glass panels and capsule buttons are drawn as thick, curved glass
/// (a shaped, lit rim plus a darker inner edge band) while the centre stays clear. It is built only
/// from XAML borders and brushes; nothing reads the screen, and nothing runs per frame or while idle.
/// <list type="bullet">
/// <item>The look itself lives in the GlassSurface and GlassButton templates (layers LensBandHost and
/// LensRimHost) and the GlassLens* tokens in GlassTokens.xaml.</item>
/// <item>The layers are shown when <see cref="IsActive"/>. Reduced transparency and High Contrast turn
/// them off (every layer collapses), exactly like the other glass layers.</item>
/// <item>When a window opens, <see cref="Materialise"/> ramps the layers from 0 to full once (about
/// 300 ms, ease-out, compositor only). It replaces the old one-off rim glint of GlassSurface; reduced
/// motion skips it and the layers simply appear.</item>
/// </list>
/// </summary>
public static class GlassLens
{
    /// <summary>The lens layers are in use: reduced transparency and High Contrast turn them off.</summary>
    public static bool IsActive => !EffectsPolicy.ReduceTransparency;

    /// <summary>
    /// Plays the opening ramp on every glass surface and capsule button under <paramref name="root"/>.
    /// One walk of the visual tree per opening; returns without doing anything under reduced motion.
    /// </summary>
    public static void Materialise(DependencyObject root)
    {
        if (!IsActive || EffectsPolicy.ReduceMotion) return;
        Walk(root);
    }

    private static void Walk(DependencyObject node)
    {
        switch (node)
        {
            case GlassSurface surface: surface.PlayMaterialise(); break;
            case GlassButton button: button.PlayMaterialise(); break;
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
    }

    /// <summary>
    /// Copies <paramref name="radius"/> shrunk by <paramref name="inset"/> onto each ring of a lens host,
    /// so an inset ring follows the corner of the panel (a ring <paramref name="step"/> DIP further in has a
    /// corner radius <paramref name="step"/> DIP smaller, never below square).
    /// </summary>
    internal static void FollowCornerRadius(Microsoft.UI.Xaml.Controls.Panel? host, CornerRadius radius, double step, double startInset = 0)
    {
        if (host is null) return;
        int ring = 0;
        foreach (var child in host.Children)
        {
            if (child is not Microsoft.UI.Xaml.Controls.Border border) continue;
            double inset = startInset + ring * step;
            border.CornerRadius = new CornerRadius(
                Math.Max(radius.TopLeft - inset, 0), Math.Max(radius.TopRight - inset, 0),
                Math.Max(radius.BottomRight - inset, 0), Math.Max(radius.BottomLeft - inset, 0));
            ring++;
        }
    }
}
