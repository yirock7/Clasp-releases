using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// Pill-shaped glass button. A real <see cref="Button"/> (Click, Command, keyboard, UIA
/// Invoke all native). Adds:
/// <list type="bullet">
/// <item><see cref="IsPending"/> / <see cref="HasError"/> visual states (CommandStates group).</item>
/// <item>A subtle light clipped to the pill, invisible at rest. A mouse or pen hovering makes it follow the
/// pointer; pressing blooms it from the press point at once (touch too) and releasing fades it, unless a
/// mouse is still over the button. Sliding off while held hides it. Reduced transparency and High Contrast
/// keep it off.</item>
/// <item>Hover lift (mouse and pen only), press compression, rise-back when a held pointer slides off, and
/// spring release via <see cref="Motion.AttachPressFeedback"/>.</item>
/// </list>
/// The template lives in GlassStyles.xaml (implicit style); DefaultStyleKey stays inherited.
/// </summary>
[TemplateVisualState(GroupName = "CommandStates", Name = "Idle")]
[TemplateVisualState(GroupName = "CommandStates", Name = "Pending")]
[TemplateVisualState(GroupName = "CommandStates", Name = "Error")]
[TemplatePart(Name = "PointerLight", Type = typeof(Border))]
[TemplatePart(Name = "Lens", Type = typeof(Border))]
[TemplatePart(Name = "BottomShade", Type = typeof(Border))]
[TemplatePart(Name = "RimLight", Type = typeof(Border))]
[TemplatePart(Name = "OpaqueEdge", Type = typeof(Border))]
[TemplatePart(Name = "LensBandHost", Type = typeof(Grid))]
[TemplatePart(Name = "LensRimHost", Type = typeof(Grid))]
public partial class GlassButton : Button
{
    public static readonly DependencyProperty IsPendingProperty = DependencyProperty.Register(
        nameof(IsPending), typeof(bool), typeof(GlassButton), new PropertyMetadata(false, OnCommandStateChanged));

    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register(
        nameof(HasError), typeof(bool), typeof(GlassButton), new PropertyMetadata(false, OnCommandStateChanged));

    public static readonly DependencyProperty IsAccentProperty = DependencyProperty.Register(
        nameof(IsAccent), typeof(bool), typeof(GlassButton), new PropertyMetadata(false, OnIsAccentChanged));

    /// <summary>
    /// A quiet button (navigation rows) that stays flat: no convex highlight, bottom shade or bright rim.
    /// Default false: the button is a glass capsule.
    /// </summary>
    public static readonly DependencyProperty IsFlatProperty = DependencyProperty.Register(
        nameof(IsFlat), typeof(bool), typeof(GlassButton), new PropertyMetadata(false, (d, _) => ((GlassButton)d).UpdateCapsuleLayers()));

    /// <summary>Relative radius of the pointer light on a flat button: more than the full width, so it never ends inside the pill.</summary>
    private const double FlatLightRadiusX = 1.5;

    private static readonly Uri AccentOverridesUri = new("ms-appx:///Design/GlassAccentOverrides.xaml");

    private ResourceDictionary? _accentOverrides;
    private Border? _pointerLight;
    private Border? _lens;
    private Border? _bottomShade;
    private Border? _rimLight;
    private Border? _opaqueEdge;
    private Grid? _lensBand;
    private Grid? _lensRim;
    private bool _lensOn;
    private bool _cornerWatched;
    private bool _effectsSubscribed;
    private RadialGradientBrush? _lightBrush;
    private double _lightRadiusX = double.NaN;
    private bool _lightVisible;
    private bool _wasPressed;

    public GlassButton()
    {
        // The press state machine also drives the light (handledEventsToo is inside AttachPressFeedback).
        Motion.AttachPressFeedback(this, OnFeedbackChanged);
        IsEnabledChanged += (_, _) =>
        {
            if (!IsEnabled) HideLight(MotionTokens.Hover);
            UpdateCapsuleLayers();
        };
        // Subscribe only while loaded, so a button that leaves the tree is not kept alive by the static event.
        Loaded += (_, _) =>
        {
            if (!_effectsSubscribed)
            {
                EffectsPolicy.Changed += OnEffectsChanged;
                _effectsSubscribed = true;
            }
            UpdateCapsuleLayers();
        };
        Unloaded += (_, _) =>
        {
            if (_effectsSubscribed)
            {
                EffectsPolicy.Changed -= OnEffectsChanged;
                _effectsSubscribed = false;
            }
        };
    }

    public bool IsFlat
    {
        get => (bool)GetValue(IsFlatProperty);
        set => SetValue(IsFlatProperty, value);
    }

    private void OnEffectsChanged(object? sender, EventArgs e) => UpdateCapsuleLayers();

    /// <summary>
    /// The convex-glass layers show only in the glass material, on an enabled, non-flat button. Reduced
    /// transparency and High Contrast (<see cref="EffectsPolicy.ReduceTransparency"/>) keep them collapsed,
    /// so those states look as they did before the layers existed. No animation: a plain visibility switch.
    /// </summary>
    private void UpdateCapsuleLayers()
    {
        var visibility = !EffectsPolicy.ReduceTransparency && !IsFlat && IsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_lens is not null) _lens.Visibility = visibility;
        if (_bottomShade is not null) _bottomShade.Visibility = visibility;
        if (_rimLight is not null) _rimLight.Visibility = visibility;
        // Without the glass layers a plain button has no visible boundary on a light card: outline it (not accent, not nav rows).
        if (_opaqueEdge is not null)
            _opaqueEdge.Visibility = EffectsPolicy.ReduceTransparency && !IsFlat && !IsAccent && IsEnabled ? Visibility.Visible : Visibility.Collapsed;

        // The fake lens (see GlassLens): a band and a second rim ring on the same buttons that show the capsule layers.
        bool lens = visibility == Visibility.Visible && GlassLens.IsActive && _lensBand is not null && _lensRim is not null;
        var lensVisibility = lens ? Visibility.Visible : Visibility.Collapsed;
        if (_lensBand is not null) _lensBand.Visibility = lensVisibility;
        if (_lensRim is not null) _lensRim.Visibility = lensVisibility;
        if (lens && !_lensOn)
        {
            // A ramp that was cut short must not leave the hosts at 0.
            Motion.VisualOf(_lensBand!).Opacity = 1f;
            Motion.VisualOf(_lensRim!).Opacity = 1f;
        }
        _lensOn = lens;
    }

    /// <summary>Ramps the lens rim and band from 0 to full (about 300 ms). No-op without the lens or under reduced motion.</summary>
    internal void PlayMaterialise()
    {
        if (!_lensOn || _lensBand is null || _lensRim is null) return;
        Motion.Materialise(_lensBand, MotionTokens.Materialise);
        Motion.Materialise(_lensRim, MotionTokens.Materialise);
        if (_rimLight is not null) Motion.Materialise(_rimLight, MotionTokens.Materialise);
    }

    private void ApplyLensRadii()
    {
        GlassLens.FollowCornerRadius(_lensBand, CornerRadius, 2);
        GlassLens.FollowCornerRadius(_lensRim, CornerRadius, 1, 1);
    }

    /// <summary>A command started by this button is waiting for confirmation (shows a small ring; label stays).</summary>
    public bool IsPending
    {
        get => (bool)GetValue(IsPendingProperty);
        set => SetValue(IsPendingProperty, value);
    }

    /// <summary>The last command failed (error glyph + error edge). Pending wins while both are set.</summary>
    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    /// <summary>
    /// Set by GlassAccentButtonStyle. Merges GlassAccentOverrides.xaml into this button's own
    /// Resources so the template's ThemeResource lookups find darkening hover/press brushes and
    /// a subtler sheen/light on the blue fill (lightweight styling at the control level).
    /// </summary>
    public bool IsAccent
    {
        get => (bool)GetValue(IsAccentProperty);
        set => SetValue(IsAccentProperty, value);
    }

    private static void OnIsAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (GlassButton)d;
        if (e.NewValue is true)
        {
            button._accentOverrides ??= new ResourceDictionary { Source = AccentOverridesUri };
            if (!button.Resources.MergedDictionaries.Contains(button._accentOverrides))
                button.Resources.MergedDictionaries.Add(button._accentOverrides);
        }
        else if (button._accentOverrides is not null)
        {
            button.Resources.MergedDictionaries.Remove(button._accentOverrides);
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _pointerLight = GetTemplateChild("PointerLight") as Border;
        _lightBrush = _pointerLight?.Background as RadialGradientBrush;
        _lightRadiusX = _lightBrush?.RadiusX ?? double.NaN;
        _lens = GetTemplateChild("Lens") as Border;
        _bottomShade = GetTemplateChild("BottomShade") as Border;
        _rimLight = GetTemplateChild("RimLight") as Border;
        _opaqueEdge = GetTemplateChild("OpaqueEdge") as Border;
        _lensBand = GetTemplateChild("LensBandHost") as Grid;
        _lensRim = GetTemplateChild("LensRimHost") as Grid;
        _lensOn = false;
        ApplyLensRadii();
        if (!_cornerWatched)
        {
            RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => ApplyLensRadii());
            _cornerWatched = true;
        }
        UpdateCapsuleLayers();
        if (_pointerLight is not null)
        {
            // Resting surface is quiet: the light is fully transparent until hover.
            Motion.VisualOf(_pointerLight).Opacity = 0f;
        }
        _lightVisible = false;
        _wasPressed = false;
        UpdateCommandState(false);
    }

    private static void OnCommandStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GlassButton)d).UpdateCommandState(true);

    private void UpdateCommandState(bool useTransitions)
    {
        string state = IsPending ? "Pending" : HasError ? "Error" : "Idle";
        VisualStateManager.GoToState(this, state, useTransitions);
        AutomationProperties.SetItemStatus(this, IsPending ? Strings.Design_Button_StatusPending : HasError ? Strings.Design_Button_StatusFailed : string.Empty);
    }

    /// <summary>
    /// Light follows the shared press state. Visible while a mouse or pen hovers, and while a pointer
    /// holds the button down inside its bounds (centred on the press point, full strength at once, also
    /// for touch). Hidden when a held pointer slides outside, on release unless a mouse is still over
    /// it, and always under reduced transparency / High Contrast (the template also keeps the
    /// High Contrast colour transparent and collapses the light when disabled).
    /// </summary>
    private void OnFeedbackChanged(PressFeedbackState state, bool changed, Point? position)
    {
        if (_pointerLight is null) return;

        PressTarget target = state.Target;
        // A keyboard press leaves the light as it is.
        if (target == PressTarget.Pressed && !state.IsPointerHeld)
        {
            _wasPressed = true;
            return;
        }

        bool lightAllowed = IsEnabled && !EffectsPolicy.ReduceTransparency;
        bool pointerPress = target == PressTarget.Pressed;
        bool want = lightAllowed && (pointerPress || target == PressTarget.Hover);

        if (want)
        {
            if (pointerPress)
            {
                if (changed)
                {
                    if (position is Point p) MoveLight(p);
                    _lightVisible = true;
                    Motion.AnimateOpacity(_pointerLight, 1f, TimeSpan.Zero);
                }
            }
            else
            {
                if (position is Point p) MoveLight(p);
                if (!_lightVisible)
                {
                    _lightVisible = true;
                    Motion.AnimateOpacity(_pointerLight, 1f, MotionTokens.Hover);
                }
            }
        }
        else
        {
            HideLight(_wasPressed ? MotionTokens.Release : MotionTokens.Hover);
        }
        _wasPressed = pointerPress;
    }

    private void HideLight(TimeSpan fade)
    {
        if (_pointerLight is null || !_lightVisible) return;
        _lightVisible = false;
        Motion.AnimateOpacity(_pointerLight, 0f, fade);
    }

    /// <summary>Centre the radial light on a point in the button (relative coordinates, so it stays clipped to the pill).</summary>
    private void MoveLight(Point p)
    {
        if (_lightBrush is null) return;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var centre = new Point(Math.Clamp(p.X / w, 0, 1), Math.Clamp(p.Y / h, 0, 1));
        _lightBrush.Center = centre;
        _lightBrush.GradientOrigin = centre;
        // The template's light fades out 0.6 widths from the pointer and is flat after that. On a wide flat button
        // (no lens, shade or rim around it) that ending reads as a lighter box over the pointer's side of the pill.
        // Flat buttons get a light that is still fading past the far end, so it covers the whole pill without an edge.
        if (!double.IsNaN(_lightRadiusX))
            _lightBrush.RadiusX = IsFlat ? Math.Max(_lightRadiusX, FlatLightRadiusX) : _lightRadiusX;
    }
}

/// <summary>
/// Circular glass icon button (44 DIP hit area by default). Always give it
/// <c>AutomationProperties.Name</c>; if it is missing, the tooltip text is used.
/// </summary>
public partial class GlassIconButton : GlassButton
{
    public GlassIconButton()
    {
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(AutomationProperties.GetName(this))
                && ToolTipService.GetToolTip(this) is string tip)
            {
                AutomationProperties.SetName(this, tip);
            }
        };
    }
}
