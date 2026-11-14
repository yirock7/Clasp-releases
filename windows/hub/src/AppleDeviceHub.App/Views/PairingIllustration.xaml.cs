using System.Numerics;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Artwork;
using AppleDeviceHub.Core.Ble;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using PathFigure = Microsoft.UI.Xaml.Media.PathFigure;
using LineSegment = Microsoft.UI.Xaml.Media.LineSegment;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// Plays one pairing guide's animation on the device artwork: the lid opens, a ring presses and holds the button (or two
/// ripples tap the case), the status light flashes, then everything rests for about two seconds before the loop repeats.
/// All motion is composition keyframes with Forever iteration, so there is no timer and no per-frame code. Under Reduce
/// motion nothing moves: the artwork is shown open and a ring marks the button. <see cref="Stop"/> halts the loop
/// (used when the guide is collapsed or the window is hidden); <see cref="Show"/> starts it again.
/// </summary>
public sealed partial class PairingIllustration : UserControl
{
    // One loop, in seconds. The last ~2.4 s is the pause before it repeats. A hold the makers time at 5 s plays as a
    // 2.4 s fill (scaled down; the caption gives the real seconds), any other hold as a 1.7 s fill.
    private const double LidOpenAt = 0.3, HoldStart = 1.0;
    private const double ShortHold = 1.7, LongHold = 2.4;

    private PairingAnimationPlan? _plan;
    private CompositionEllipseGeometry? _progress;

    /// <summary>
    /// False (the default) keeps the animation off: the host turns it on only while the help is open and its window
    /// is showing, so a hidden window or collapsed help costs nothing.
    /// </summary>
    public bool Enabled { get; set; }

    public PairingIllustration()
    {
        InitializeComponent();
        AutomationProperties.SetAccessibilityView(Art, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        EffectsPolicy.Changed += OnEffectsChanged;
        Unloaded += (_, _) => { EffectsPolicy.Changed -= OnEffectsChanged; Stop(); };
        Loaded += (_, _) =>
        {
            EffectsPolicy.Changed -= OnEffectsChanged;
            EffectsPolicy.Changed += OnEffectsChanged;
            Replay();
        };
    }

    private void OnEffectsChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Replay();
        else DispatcherQueue.TryEnqueue(Replay);
    }

    /// <summary>Shows <paramref name="plan"/> and starts its loop (or its static state).</summary>
    public void Show(PairingAnimationPlan plan)
    {
        _plan = plan;
        AutomationProperties.SetName(this, plan.Description);
        // The chip says the same words the step list and screen readers get (Cue.Spoken).
        Chip.Visibility = plan.Cue is null ? Visibility.Collapsed : Visibility.Visible;
        ChipAction.Text = plan.Cue?.Caption ?? string.Empty;
        ChipLight.Text = plan.Cue?.LightText ?? string.Empty;
        Replay();
    }

    /// <summary>Halts the loop and leaves the artwork open and still.</summary>
    public void Stop()
    {
        foreach (var e in new UIElement[] { RippleA, RippleB, Marker, TapBadge, BackBadge, BackArrow, BackArrowHalo, Flash })
        {
            if (!Motion.IsShuttingDown) Motion.VisualOf(e).StopAnimation("Opacity");
            if (!Motion.IsShuttingDown) Motion.VisualOf(e).StopAnimation("Scale");
            Motion.VisualOf(e).Opacity = 0f;
        }
        if (_progress is not null)
        {
            if (!Motion.IsShuttingDown) _progress.StopAnimation("TrimEnd");
            _progress = null;
        }
        if (_plan is not null) Art.ShowOpen();
    }

    /// <summary>Restarts from the top, for a new plan, a Reduce motion change or when the guide opens.</summary>
    public void Replay()
    {
        Stop();
        if (_plan is not { } plan) return;
        // The per-model drawing for the guide's model; the group is kept too so unknown models and High Contrast show the older drawing.
        Art.SetModel(plan.ModelId, null);
        Art.Group = plan.Art;
        Art.ShowOpen();
        if (!plan.IsAnimated || !Enabled) return;

        var cue = plan.Cue;
        bool taps = cue?.Gesture == PairingGesture.DoubleTap;
        bool back = cue?.Location == PairingLocation.Back;
        double holdLen = cue?.HoldSeconds is not null ? LongHold : ShortHold;
        double holdEnd = HoldStart + holdLen;
        double flashStart = holdEnd + 0.2;
        double period = flashStart + 4.7;
        double lidCloseAt = flashStart + 2.1;

        var (button, led) = Anchors(plan);
        Place(Marker, button);
        Place(Flash, led);
        Place(RippleA, button);
        Place(RippleB, button);
        Place(TapBadge, new Vector2(button.X + 26, button.Y - 24), 30, 20);
        Place(BackBadge, new Vector2(button.X + (plan.Kind == PairingAnimationKind.RearEarcupButton ? 8f : -8f), button.Y + 29), 76, 20);
        // A button on the back of the case cannot be drawn on a front view: a dashed ring (hidden) and an arrow that goes around the edge say so.
        HoldRing.StrokeDashArray = back ? new DoubleCollection { 2.2, 2.0 } : new DoubleCollection();
        HoldHalo.StrokeDashArray = back ? new DoubleCollection { 0.92, 0.83 } : new DoubleCollection();
        DrawBackArrow(button, left: plan.Kind == PairingAnimationKind.RearEarcupButton, visible: back);
        var progress = _progress = taps ? null : BuildProgress();

        if (EffectsPolicy.ReduceMotion)
        {
            // No motion: a static ring (filled, to show a hold) or the tap count, the caption and, for a back button, the arrow.
            if (taps)
            {
                Motion.VisualOf(RippleA).Scale = new Vector3(1.0f, 1.0f, 1f);
                Motion.VisualOf(RippleA).Opacity = 0.9f;
                Motion.VisualOf(RippleB).Scale = new Vector3(0.55f, 0.55f, 1f);
                Motion.VisualOf(RippleB).Opacity = 0.9f;
                Motion.VisualOf(TapBadge).Opacity = 1f;
            }
            else
            {
                Motion.VisualOf(Marker).Opacity = 1f;
                if (progress is not null) progress.TrimEnd = 1f;
            }
            if (back)
            {
                Motion.VisualOf(BackArrow).Opacity = 1f;
                Motion.VisualOf(BackArrowHalo).Opacity = 1f;
                Motion.VisualOf(BackBadge).Opacity = 1f;
            }
            return;
        }
        if (!Motion.CanAnimate(Overlay)) return;

        Art.StartCaseLoop(period, LidOpenAt, lidCloseAt); // a drawing without a lid just stays open
        var c = Motion.VisualOf(Overlay).Compositor;
        var ease = Motion.EaseOut(c);
        var span = TimeSpan.FromSeconds(period);
        float P(double t) => (float)Math.Clamp(t / period, 0d, 1d);

        void Start(UIElement element, string property, KeyFrameAnimation animation)
        {
            animation.Duration = span;
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            Motion.VisualOf(element).StartAnimation(property, animation);
        }

        ScalarKeyFrameAnimation Opacity(params (double t, float v)[] keys)
        {
            var a = c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0f, 0f);
            foreach (var (t, v) in keys) a.InsertKeyFrame(P(t), v, ease);
            a.InsertKeyFrame(1f, 0f);
            return a;
        }

        Vector3KeyFrameAnimation Scale(params (double t, float v)[] keys)
        {
            var a = c.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0f, new Vector3(keys[0].v, keys[0].v, 1f));
            foreach (var (t, v) in keys) a.InsertKeyFrame(P(t), new Vector3(v, v, 1f), ease);
            a.InsertKeyFrame(1f, new Vector3(keys[^1].v, keys[^1].v, 1f));
            return a;
        }

        if (taps)
        {
            // Two distinct taps: a ripple grows and fades from the front of the case, twice, with "2x" beside it.
            Start(RippleA, "Opacity", Opacity((HoldStart, 0.9f), (HoldStart + 0.6, 0f)));
            Start(RippleA, "Scale", Scale((HoldStart, 0.3f), (HoldStart + 0.6, 1.7f)));
            Start(RippleB, "Opacity", Opacity((HoldStart + 0.8, 0.9f), (HoldStart + 1.4, 0f)));
            Start(RippleB, "Scale", Scale((HoldStart + 0.8, 0.3f), (HoldStart + 1.4, 1.7f)));
            Start(TapBadge, "Opacity", Opacity((HoldStart, 1f), (HoldStart + 1.5, 1f), (HoldStart + 1.9, 0f)));
        }
        else
        {
            // Press and hold: the marker closes in on the button, then the ring fills over the hold and stays until the light flashes.
            Start(Marker, "Opacity", Opacity((HoldStart, 1f), (holdEnd, 1f), (holdEnd + 0.25, 0f)));
            Start(Marker, "Scale", Scale((HoldStart, 1.6f), (HoldStart + 0.4, 1f), (holdEnd, 1f)));
            if (back)
            {
                Start(BackArrow, "Opacity", Opacity((HoldStart, 1f), (holdEnd, 1f), (holdEnd + 0.25, 0f)));
                Start(BackArrowHalo, "Opacity", Opacity((HoldStart, 1f), (holdEnd, 1f), (holdEnd + 0.25, 0f)));
                Start(BackBadge, "Opacity", Opacity((HoldStart, 1f), (holdEnd, 1f), (holdEnd + 0.25, 0f)));
            }
            if (progress is not null)
            {
                var fill = c.CreateScalarKeyFrameAnimation();
                fill.InsertKeyFrame(0f, 0f);
                fill.InsertKeyFrame(P(HoldStart + 0.4), 0f);
                fill.InsertKeyFrame(P(holdEnd), 1f, c.CreateLinearEasingFunction());
                fill.InsertKeyFrame(1f, 1f);
                fill.Duration = span;
                fill.IterationBehavior = AnimationIterationBehavior.Forever;
                progress.StartAnimation("TrimEnd", fill);
            }
        }

        // The light flashes three times (a tone pulse on Solo Buds), then the loop rests.
        Start(Flash, "Opacity", Opacity(
            (flashStart, 0f), (flashStart + 0.2, 1f), (flashStart + 0.45, 0f),
            (flashStart + 0.65, 1f), (flashStart + 0.9, 0f),
            (flashStart + 1.1, 1f), (flashStart + 1.35, 0f)));
    }

    /// <summary>A circle stroke that can be trimmed from 0 to a full ring, hosted on the marker. Null if composition cannot make it.</summary>
    private CompositionEllipseGeometry? BuildProgress()
    {
        try
        {
            var c = Motion.VisualOf(ProgressHost).Compositor;
            var geometry = c.CreateEllipseGeometry();
            geometry.Center = new Vector2(18, 18);
            geometry.Radius = new Vector2(13.75f, 13.75f);
            geometry.TrimStart = 0f;
            geometry.TrimEnd = 0f;
            var color = (HoldRing.Stroke as SolidColorBrush)?.Color ?? Windows.UI.Color.FromArgb(255, 47, 111, 214);
            var shape = c.CreateSpriteShape(geometry);
            shape.StrokeBrush = c.CreateColorBrush(color);
            shape.StrokeThickness = 4f;
            shape.StrokeStartCap = CompositionStrokeCap.Round;
            shape.StrokeEndCap = CompositionStrokeCap.Round;
            shape.CenterPoint = new Vector2(18, 18);
            shape.RotationAngleInDegrees = -90f; // start at 12 o'clock
            var visual = c.CreateShapeVisual();
            visual.Size = new Vector2(36, 36);
            visual.Shapes.Add(shape);
            ElementCompositionPreview.SetElementChildVisual(ProgressHost, visual);
            return geometry;
        }
        catch (Exception ex)
        {
            Shell.LocalLog.Write("Pairing progress ring unavailable: " + ex.Message);
            return null;
        }
    }

    // The arrow bends from the front face over the edge toward the ring. Mirrored for a button on the left.
    private void DrawBackArrow(Vector2 button, bool left, bool visible)
    {
        BackArrow.Data = null;
        BackArrowHalo.Data = null;
        if (!visible) return;
        float s = left ? -1f : 1f;
        Point start = new(button.X - (s * 36), button.Y - 26);
        Point control = new(button.X + (s * 4), button.Y - 46);
        Point end = new(button.X + (s * 20), button.Y - 20);
        // Arrowhead at the end, pointing along the curve's last direction.
        double dx = end.X - control.X, dy = end.Y - control.Y, len = Math.Sqrt((dx * dx) + (dy * dy));
        dx /= len;
        dy /= len;
        Point Wing(double degrees)
        {
            double a = degrees * Math.PI / 180d, cos = Math.Cos(a), sin = Math.Sin(a);
            return new Point(end.X - (6.5 * ((dx * cos) - (dy * sin))), end.Y - (6.5 * ((dx * sin) + (dy * cos))));
        }
        PathGeometry Build()
        {
            var curve = new PathFigure { StartPoint = start, IsClosed = false };
            curve.Segments.Add(new QuadraticBezierSegment { Point1 = control, Point2 = end });
            var head = new PathFigure { StartPoint = Wing(28), IsClosed = false };
            head.Segments.Add(new LineSegment { Point = end });
            head.Segments.Add(new LineSegment { Point = Wing(-28) });
            var g = new PathGeometry();
            g.Figures.Add(curve);
            g.Figures.Add(head);
            return g;
        }
        BackArrowHalo.Data = Build();
        BackArrow.Data = Build();
    }

    private static void Place(FrameworkElement element, Vector2 center, double? width = null, double? height = null)
    {
        double w = width ?? element.Width, h = height ?? element.Height;
        Canvas.SetLeft(element, center.X - w / 2);
        Canvas.SetTop(element, center.Y - h / 2);
        var visual = Motion.VisualOf(element);
        visual.CenterPoint = new Vector3((float)w / 2f, (float)h / 2f, 0f);
        visual.Scale = Vector3.One;
        visual.Opacity = 0f;
    }

    /// <summary>
    /// Where the button ring and the status light go on the 200 x 150 artwork stage. A per-model drawing (the normal case) is
    /// measured from its own geometry (<see cref="PairingAnchors"/>); the fixed table below is only for the older group drawings
    /// (models without a per-model drawing, and High Contrast).
    /// </summary>
    private static (Vector2 Button, Vector2 Light) Anchors(PairingAnimationPlan plan)
    {
        var key = EffectsPolicy.HighContrast ? null : ArtworkCatalog.DrawingKeyFor(plan.ModelId);
        if (key is not null && ArtworkStore.Get(key) is { } document)
        {
            var anchors = PairingAnchors.For(plan.Kind, document);
            return (new Vector2((float)anchors.Button.X, (float)anchors.Button.Y), new Vector2((float)anchors.Light.X, (float)anchors.Light.Y));
        }

        return LegacyAnchors(plan);
    }

    private static (Vector2 Button, Vector2 Light) LegacyAnchors(PairingAnimationPlan plan) => plan.Kind switch
    {
        PairingAnimationKind.CaseSetupButton => (new Vector2(164, 92), new Vector2(100, 99)),
        PairingAnimationKind.CaseDoubleTap => (new Vector2(100, 102), new Vector2(100, 99)),
        PairingAnimationKind.MaxButton => (new Vector2(160, 66), new Vector2(152, 122)),
        PairingAnimationKind.CaseButton => (new Vector2(100, 80), new Vector2(100, 99)),
        PairingAnimationKind.EarbudButton => (new Vector2(76, 57), new Vector2(76, 57)),
        PairingAnimationKind.ModuleButton => (new Vector2(140, 34), new Vector2(140, 48)),
        PairingAnimationKind.RearEarcupButton => (new Vector2(50, 90), new Vector2(50, 110)),
        PairingAnimationKind.EarcupButton => plan.Art == ArtworkGroup.OnEarHeadphones
            ? (new Vector2(148, 104), new Vector2(148, 120))
            : (new Vector2(150, 94), new Vector2(150, 114)),
        _ => (new Vector2(100, 75), new Vector2(100, 75)),
    };
}
