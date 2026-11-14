using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// Reusable glass card/flyout material: layered tint, inner top highlight and a luminous
/// 1 DIP edge, with an opaque fallback of identical geometry. The look lives in the
/// implicit style in GlassStyles.xaml (variants: GlassNestedSurfaceStyle,
/// GlassWindowSurfaceStyle, GlassReadableSurfaceStyle). DefaultStyleKey is deliberately
/// left inherited; the implicit style supplies the template.
/// <para>The template also has a <c>PointerRim</c>: a 1 DIP radial highlight on the edge that is
/// invisible at rest. <see cref="TracksPointerLight"/> makes it follow a mouse or pen, and
/// <see cref="PlayEntranceGlint"/> flashes it once as a window opens. Both are light, not movement,
/// and both are off in the Opaque state (reduced transparency and High Contrast).</para>
/// <para>The "fake lens" (see <see cref="GlassLens"/>): in the LensOn state a 3 DIP lit rim (LensRimHost) and a
/// darker inner edge band (LensBandHost) replace the flat 1 DIP edge, inner rim and inner shade, and
/// <see cref="PlayMaterialise"/> ramps them in when a window opens. In that state the old entrance glint is
/// not played: one opening effect, not two.</para>
/// </summary>
[TemplateVisualState(GroupName = "MaterialStates", Name = "Glass")]
[TemplateVisualState(GroupName = "MaterialStates", Name = "Opaque")]
[TemplateVisualState(GroupName = "LensStates", Name = "LensOff")]
[TemplateVisualState(GroupName = "LensStates", Name = "LensOn")]
[TemplatePart(Name = "PointerRim", Type = typeof(Border))]
[TemplatePart(Name = "LensBandHost", Type = typeof(Grid))]
[TemplatePart(Name = "LensRimHost", Type = typeof(Grid))]
public partial class GlassSurface : ContentControl
{
    /// <summary>Fill used when <see cref="EffectsPolicy.ReduceTransparency"/> is on.</summary>
    public static readonly DependencyProperty OpaqueFillProperty = DependencyProperty.Register(
        nameof(OpaqueFill), typeof(Brush), typeof(GlassSurface), new PropertyMetadata(null));

    /// <summary>When true, a rim highlight follows a mouse or pen over the surface. Default false: no handlers, no work.</summary>
    public static readonly DependencyProperty TracksPointerLightProperty = DependencyProperty.Register(
        nameof(TracksPointerLight), typeof(bool), typeof(GlassSurface),
        new PropertyMetadata(false, (d, _) => ((GlassSurface)d).UpdateTrackingHandlers()));

    /// <summary>
    /// Whether the fake lens (shaped rim and inner edge band) may be drawn on this surface. Default true. The
    /// borderless tile style turns it off: tiles group content without an outline, so they get no lens edge.
    /// </summary>
    public static readonly DependencyProperty LensEdgeProperty = DependencyProperty.Register(
        nameof(LensEdge), typeof(bool), typeof(GlassSurface),
        new PropertyMetadata(true, (d, _) => ((GlassSurface)d).UpdateMaterial(false)));

    private static readonly TimeSpan GlintDuration = TimeSpan.FromMilliseconds(420);

    /// <summary>Size of the soft pool of rim light, in DIPs, so tall and wide surfaces look alike.</summary>
    private const double RimPoolRadius = 170;

    private bool _subscribed;
    private bool _isLoaded;
    private bool _handlersAttached;
    private bool _pointerOver;
    private int _glintGeneration;
    private bool _glinting;
    private Point _lastPointer;
    private Border? _rim;
    private RadialGradientBrush? _rimBrush;
    private Grid? _lensBand;
    private Grid? _lensRim;
    private bool _lensOn;
    private bool _cornerWatched;

    // Ring spacing in the template (DIP): band rings are 3 DIP apart, rim rings 1 DIP apart.
    private const double LensBandStep = 3;
    private const double LensRimStep = 1;
    private PointerEventHandler? _enteredHandler;
    private PointerEventHandler? _movedHandler;
    private PointerEventHandler? _exitedHandler;

    public GlassSurface()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Brush? OpaqueFill
    {
        get => (Brush?)GetValue(OpaqueFillProperty);
        set => SetValue(OpaqueFillProperty, value);
    }

    public bool LensEdge
    {
        get => (bool)GetValue(LensEdgeProperty);
        set => SetValue(LensEdgeProperty, value);
    }

    public bool TracksPointerLight
    {
        get => (bool)GetValue(TracksPointerLightProperty);
        set => SetValue(TracksPointerLightProperty, value);
    }

    /// <summary>True while the opaque (reduced transparency / high contrast) material is shown.</summary>
    public bool IsOpaque { get; private set; }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _rim = GetTemplateChild("PointerRim") as Border;
        _rimBrush = _rim?.BorderBrush as RadialGradientBrush;
        // Composition opacity is driven from here (0 at rest); do not set XAML Opacity on the rim.
        if (_rim is not null) Motion.VisualOf(_rim).Opacity = 0f;
        _lensBand = GetTemplateChild("LensBandHost") as Grid;
        _lensRim = GetTemplateChild("LensRimHost") as Grid;
        _lensOn = false;
        ApplyLensRadii();
        if (!_cornerWatched)
        {
            // The rings follow the panel's own corner radius, now and if it is changed later.
            RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => ApplyLensRadii());
            _cornerWatched = true;
        }
        _pointerOver = false;
        UpdateMaterial(false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (!_subscribed)
        {
            EffectsPolicy.Changed += OnEffectsChanged;
            _subscribed = true;
        }
        UpdateMaterial(false);
        UpdateTrackingHandlers();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (_subscribed)
        {
            EffectsPolicy.Changed -= OnEffectsChanged;
            _subscribed = false;
        }
        UpdateTrackingHandlers();
        _pointerOver = false;
        _glintGeneration++;
        _glinting = false;
        if (_rim is not null) Motion.AnimateOpacity(_rim, 0f, TimeSpan.Zero);
    }

    private void OnEffectsChanged(object? sender, EventArgs e) => UpdateMaterial(false);

    private void UpdateMaterial(bool useTransitions)
    {
        IsOpaque = EffectsPolicy.ReduceTransparency;
        VisualStateManager.GoToState(this, IsOpaque ? "Opaque" : "Glass", useTransitions);
        bool lens = !IsOpaque && LensEdge && GlassLens.IsActive && _lensBand is not null && _lensRim is not null;
        if (lens != _lensOn)
        {
            _lensOn = lens;
            VisualStateManager.GoToState(this, lens ? "LensOn" : "LensOff", false);
            if (lens)
            {
                // Hidden hosts may have been left at 0 by an interrupted ramp.
                Motion.VisualOf(_lensBand!).Opacity = 1f;
                Motion.VisualOf(_lensRim!).Opacity = 1f;
            }
        }
        if (IsOpaque && _rim is not null)
        {
            _glintGeneration++;
            _glinting = false;
            Motion.AnimateOpacity(_rim, 0f, TimeSpan.Zero);
        }
    }

    // ---- Fake lens ----------------------------------------------------------------------------

    private void ApplyLensRadii()
    {
        GlassLens.FollowCornerRadius(_lensBand, CornerRadius, LensBandStep);
        GlassLens.FollowCornerRadius(_lensRim, CornerRadius, LensRimStep);
    }

    /// <summary>Ramps the lens rim and band from 0 to full (about 300 ms). No-op without the lens or under reduced motion.</summary>
    internal void PlayMaterialise()
    {
        if (!_lensOn || _lensBand is null || _lensRim is null) return;
        Motion.Materialise(_lensBand, MotionTokens.Materialise);
        Motion.Materialise(_lensRim, MotionTokens.Materialise);
    }

    // ---- Pointer rim light ----------------------------------------------------------------

    private bool CanShowRim => _rim is not null && _rimBrush is not null && !IsOpaque;

    /// <summary>Attach the pointer handlers only while the property is true and the surface is loaded.</summary>
    private void UpdateTrackingHandlers()
    {
        bool wanted = TracksPointerLight && _isLoaded;
        if (wanted == _handlersAttached) return;

        if (wanted)
        {
            _enteredHandler = new PointerEventHandler(OnRimPointerEntered);
            _movedHandler = new PointerEventHandler(OnRimPointerMoved);
            _exitedHandler = new PointerEventHandler(OnRimPointerExited);
            // handledEventsToo: buttons and scrollers inside the surface mark pointer events handled.
            AddHandler(PointerEnteredEvent, _enteredHandler, true);
            AddHandler(PointerMovedEvent, _movedHandler, true);
            AddHandler(PointerExitedEvent, _exitedHandler, true);
        }
        else
        {
            if (_enteredHandler is not null) RemoveHandler(PointerEnteredEvent, _enteredHandler);
            if (_movedHandler is not null) RemoveHandler(PointerMovedEvent, _movedHandler);
            if (_exitedHandler is not null) RemoveHandler(PointerExitedEvent, _exitedHandler);
            _enteredHandler = _movedHandler = _exitedHandler = null;
            _pointerOver = false;
            if (!_glinting && _rim is not null) Motion.AnimateOpacity(_rim, 0f, MotionTokens.Hover);
        }
        _handlersAttached = wanted;
    }

    private static bool IsLightPointer(PointerRoutedEventArgs e)
        => e.Pointer.PointerDeviceType is PointerDeviceType.Mouse or PointerDeviceType.Pen;

    private void OnRimPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!IsLightPointer(e)) return;
        ShowRimAt(e);
    }

    private void OnRimPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsLightPointer(e)) return;
        // An Exited that bubbled up from a child while the pointer is still here is healed by the next move.
        if (!_pointerOver) ShowRimAt(e);
        else
        {
            _lastPointer = e.GetCurrentPoint(this).Position;
            if (CanShowRim && !_glinting) SetRimGeometry(_lastPointer, RimPoolRadius, RimPoolRadius);
        }
    }

    private void OnRimPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerOver) return;
        _pointerOver = false;
        if (_rim is null || _glinting) return;
        Motion.AnimateOpacity(_rim, 0f, MotionTokens.Hover);
    }

    private void ShowRimAt(PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        if (!CanShowRim) return;
        _lastPointer = e.GetCurrentPoint(this).Position;
        if (!_glinting) SetRimGeometry(_lastPointer, RimPoolRadius, RimPoolRadius);
        if (!_glinting) Motion.AnimateOpacity(_rim!, 1f, MotionTokens.Hover);
    }

    /// <summary>Centre the radial rim light (relative coordinates, so it stays on the edge) with a pool size in DIPs.</summary>
    private void SetRimGeometry(Point centre, double radiusXDips, double radiusYDips)
    {
        double w = ActualWidth, h = ActualHeight;
        if (_rimBrush is null || w <= 0 || h <= 0) return;
        var c = new Point(Math.Clamp(centre.X / w, 0, 1), Math.Clamp(centre.Y / h, 0, 1));
        _rimBrush.Center = c;
        _rimBrush.GradientOrigin = c;
        _rimBrush.RadiusX = radiusXDips / w;
        _rimBrush.RadiusY = radiusYDips / h;
    }

    // ---- Opening glint ----------------------------------------------------------------------

    /// <summary>
    /// The rim brightens once, centred at the top, and settles (about 420 ms). Skipped under
    /// reduced motion and in the Opaque state. If a mouse is over the surface when it ends, the rim
    /// is left at its pointer state instead of 0.
    /// </summary>
    public void PlayEntranceGlint()
    {
        if (!CanShowRim || EffectsPolicy.ReduceMotion) return;
        if (_lensOn) return; // the lens ramp (PlayMaterialise) is the opening effect while the lens is drawn

        double w = ActualWidth;
        SetRimGeometry(new Point(w / 2, 0), Math.Max(w * 0.6, RimPoolRadius), 200);

        var visual = Motion.VisualOf(_rim!);
        var c = visual.Compositor;
        int generation = ++_glintGeneration;
        _glinting = true;

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var k = c.CreateScalarKeyFrameAnimation();
        k.InsertKeyFrame(0.35f, 1f, Motion.EaseOut(c));
        k.InsertKeyFrame(1f, _pointerOver && TracksPointerLight ? 1f : 0f, Motion.EaseIn(c));
        k.Duration = GlintDuration;
        visual.StartAnimation("Opacity", k);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation != _glintGeneration) return;
            _glinting = false;
            if (_rim is null) return;
            // Settle on the pointer state as it is now (it may have changed during the glint).
            bool lit = _pointerOver && TracksPointerLight && CanShowRim;
            if (lit) SetRimGeometry(_lastPointer, RimPoolRadius, RimPoolRadius);
            Motion.AnimateOpacity(_rim, lit ? 1f : 0f, MotionTokens.Hover);
        };
    }
}
