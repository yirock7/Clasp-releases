using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// The app icon's aurora edge around a window: a soft coloured glow that fades inward from the outer edge, plus a
/// faint prism rim and a white hairline. Put one as the LAST child of a window's root Grid (it takes no space and no
/// clicks) and give it the window's corner radius (the default is <c>GlassWindowRadius</c>, 8 DIP, which is the
/// Windows 11 window corner and the flyout's glass corner). Cards, buttons and other controls keep their own edges.
/// <list type="bullet">
/// <item>The look (twelve 1 DIP rings, prism rim, hairline) is the implicit style in GlassStyles.xaml; colours and
/// the resting and peak opacities are the <c>AuroraEdge*</c> tokens in GlassTokens.xaml (Light is softer).</item>
/// <item>It is drawn unless <see cref="AuroraEdgePolicy"/> says Off: High Contrast and reduced transparency (Windows
/// transparency effects off, or the app's override) hide it, and it follows <see cref="EffectsPolicy.Changed"/> live.
/// The window then shows its plain glass edge, as before.</item>
/// <item><see cref="PlayOpenGlow"/> (called from <see cref="GlassLens.Materialise"/> when a window opens) brightens the
/// rings to the peak and eases them back to rest over about 850 ms, once, compositor only. Reduced motion shows the
/// resting look with no animation.</item>
/// </list>
/// </summary>
[TemplatePart(Name = "Root", Type = typeof(Grid))]
[TemplatePart(Name = "RingsHost", Type = typeof(Grid))]
[TemplatePart(Name = "RimHost", Type = typeof(Grid))]
[TemplatePart(Name = "GlowProbe", Type = typeof(Border))]
public sealed partial class AuroraEdge : Control
{
    private static readonly TimeSpan GlowDuration = TimeSpan.FromMilliseconds(850);

    private Grid? _root;
    private Grid? _rings;
    private Grid? _rim;
    private Border? _glowProbe;
    private bool _subscribed;
    private bool _cornerWatched;

    public AuroraEdge()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _root = GetTemplateChild("Root") as Grid;
        _rings = GetTemplateChild("RingsHost") as Grid;
        _rim = GetTemplateChild("RimHost") as Grid;
        _glowProbe = GetTemplateChild("GlowProbe") as Border;
        ApplyRadii();
        if (!_cornerWatched)
        {
            RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => ApplyRadii());
            _cornerWatched = true;
        }
        UpdateMode();
    }

    /// <summary>The edge is currently drawn.</summary>
    public static bool IsShown => CurrentMode != AuroraEdgeMode.Off;

    private static AuroraEdgeMode CurrentMode =>
        AuroraEdgePolicy.Decide(EffectsPolicy.HighContrast, EffectsPolicy.ReduceTransparency, EffectsPolicy.ReduceMotion);

    /// <summary>
    /// Opening glow: brighter for a moment, then eased back to the resting level. Does nothing while the edge is off or
    /// under reduced motion (the resting look is already showing). Safe to call repeatedly: it restarts.
    /// </summary>
    public void PlayOpenGlow()
    {
        if (CurrentMode != AuroraEdgeMode.Glowing || _rings is null || _glowProbe is null) return;
        float rest = (float)_rings.Opacity;
        float peak = Math.Max((float)_glowProbe.Opacity, rest);
        if (peak <= rest) return;

        var visual = Motion.VisualOf(_rings);
        var c = visual.Compositor;
        visual.StopAnimation("Opacity");
        var k = c.CreateScalarKeyFrameAnimation();
        k.InsertKeyFrame(0f, peak);
        k.InsertKeyFrame(1f, rest, Motion.EaseOut(c));
        k.Duration = GlowDuration;
        visual.StartAnimation("Opacity", k);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            EffectsPolicy.Changed += OnEffectsChanged;
            _subscribed = true;
        }
        ApplyTemplate();
        UpdateMode();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            EffectsPolicy.Changed -= OnEffectsChanged;
            _subscribed = false;
        }
        if (_rings is not null) Motion.VisualOf(_rings).StopAnimation("Opacity");
    }

    private void OnEffectsChanged(object? sender, EventArgs e) => UpdateMode();

    private void UpdateMode()
    {
        if (_root is null) return;
        bool on = IsShown;
        _root.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // Leaving the animated state mid-glow: drop the animation so the XAML resting opacity shows.
        if (!on && _rings is not null) Motion.VisualOf(_rings).StopAnimation("Opacity");
    }

    /// <summary>Each ring follows the window corner, less its inset (a ring one DIP in is one DIP tighter).</summary>
    private void ApplyRadii()
    {
        GlassLens.FollowCornerRadius(_rings, CornerRadius, 1);
        if (_rim is null) return;
        foreach (var child in _rim.Children)
            if (child is Border border) border.CornerRadius = CornerRadius;
    }
}
