using System.Numerics;
using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// Motion tokens from LIQUID-GLASS-DESIGN.md ("Initial design tokens" and "Motion
/// requirements"). Durations sit inside the brief's ranges.
/// </summary>
public static class MotionTokens
{
    public static readonly TimeSpan Hover = TimeSpan.FromMilliseconds(130);    // brief: 100-160
    public static readonly TimeSpan Press = TimeSpan.FromMilliseconds(90);     // brief: 70-110
    public static readonly TimeSpan Release = TimeSpan.FromMilliseconds(190);  // brief: 160-220, spring
    public static readonly TimeSpan Entrance = TimeSpan.FromMilliseconds(280); // brief: 240-320
    public static readonly TimeSpan Dismiss = TimeSpan.FromMilliseconds(150);  // brief: 120-180
    public static readonly TimeSpan Toggle = TimeSpan.FromMilliseconds(190);   // brief: 160-220
    public static readonly TimeSpan CaseLid = TimeSpan.FromMilliseconds(300);  // brief: 250-350
    public static readonly TimeSpan Materialise = TimeSpan.FromMilliseconds(300); // lens rim and band ramp on open: 250-350

    /// <summary>Maximum fade length when reduced motion is on (no spatial movement).</summary>
    public static readonly TimeSpan ReducedFade = TimeSpan.FromMilliseconds(100);

    public const float PressScale = 0.96f;
    public const float HoverScale = 1.015f;
    public const float EntranceOffset = 10f;   // brief: 8-12 DIP
    public const float EntranceScale = 0.97f;
    public const float SpringDamping = 0.72f;  // ~4% overshoot: small, quick settle

    // ---- Liquid feel: every number for the springs and stretches lives here ----------------------------------------
    // Natural-motion springs run on the compositor. DampingRatio below 1 overshoots a little (1 = none, lower = more
    // wobble); Period is the time of one oscillation (smaller = snappier, larger = softer). They are not durations:
    // a spring keeps settling for a few periods. Reduced motion skips all of them (instant change, no stretch).

    /// <summary>Press-down spring of buttons and cards (to <see cref="PressScale"/>): nearly no overshoot, quick.</summary>
    public static readonly TimeSpan PressSpringPeriod = TimeSpan.FromMilliseconds(45);
    public const float PressSpringDamping = 0.85f;

    /// <summary>Release spring: a slight overshoot past rest, then settles.</summary>
    public static readonly TimeSpan ReleaseSpringPeriod = TimeSpan.FromMilliseconds(80);
    public const float ReleaseDamping = 0.55f;

    /// <summary>Toggle knob travel spring: overshoots a little and settles.</summary>
    public static readonly TimeSpan KnobSpringPeriod = TimeSpan.FromMilliseconds(65); // owner range 50-70 ms
    public const float KnobSpringDamping = 0.65f;                                      // owner range 0.6-0.7

    /// <summary>While the knob travels it stretches along X to this scale (1.2 = 20% wider) and returns to round.</summary>
    public const float KnobTravelStretch = 1.2f;
    public static readonly TimeSpan KnobStretchDuration = TimeSpan.FromMilliseconds(260);

    /// <summary>Pressed or dragged toggle knob widens by this much (a drop under a finger); height is unchanged.</summary>
    public const float KnobPressWiden = 1.2f;

    /// <summary>Pressed or dragged slider thumb: about 1.15x overall, a little wider than tall.</summary>
    public const float ThumbGrowX = 1.2f;
    public const float ThumbGrowY = 1.1f;

    /// <summary>Slider thumb (and value fill) spring when the value jumps (track click, big keyboard step, device update).</summary>
    public static readonly TimeSpan SliderSpringPeriod = TimeSpan.FromMilliseconds(65);
    public const float SliderSpringDamping = 0.7f;
    /// <summary>Moves smaller than this many DIP get no spring: the thumb just follows the value.</summary>
    public const float SliderSpringMinDistance = 1.5f;

    /// <summary>
    /// Segmented-control selection pill as a liquid blob: the edge it moves toward (leading) springs quickly, the edge it
    /// leaves (trailing) springs slower and softer, so the pill stretches toward the destination and then contracts.
    /// </summary>
    public static readonly TimeSpan CapsuleLeadPeriod = TimeSpan.FromMilliseconds(55);
    public const float CapsuleLeadDamping = 0.7f;
    public static readonly TimeSpan CapsuleTrailPeriod = TimeSpan.FromMilliseconds(95);
    public const float CapsuleTrailDamping = 0.8f;

    /// <summary>Flyout / notice entrance: scale spring from <see cref="EntranceScale"/> to 1.</summary>
    public static readonly TimeSpan EntranceSpringPeriod = TimeSpan.FromMilliseconds(80);
    public const float EntranceSpringDamping = 0.7f;
}

/// <summary>
/// Compositor-driven animation helpers. Every animation starts from the property's current
/// value, so starting a new one retargets the old one instead of queueing behind it (rapid
/// clicks never accumulate delayed motion). All helpers honour <see cref="EffectsPolicy.ReduceMotion"/>.
/// Do not also set XAML Opacity/RenderTransform on elements animated here.
/// </summary>
public static class Motion
{
    public static Visual VisualOf(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    /// <summary>
    /// Set once the app begins to quit. Windows unload and controls unhook while the process exits; starting or resetting
    /// composition animations then is pointless and (for Translation) can fault natively, so the helpers below skip it.
    /// </summary>
    public static volatile bool IsShuttingDown;

    /// <summary>True when starting a composition animation on <paramref name="element"/> makes sense: not quitting, and it is in a live tree.</summary>
    public static bool CanAnimate(UIElement element) => !IsShuttingDown && element.XamlRoot is not null;

    // Every Translation animation or reset goes through the four helpers below. A visual only has a "Translation" property
    // after ElementCompositionPreview.SetIsTranslationEnabled(element, true); without it, StartAnimation/StopAnimation/
    // InsertVector3 on "Translation" throw ArgumentException ("The specified property was not found or cannot be animated").

    /// <summary>Turns the composition Translation property on for <paramref name="element"/> and returns its visual.</summary>
    public static Visual EnableTranslation(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return VisualOf(element);
    }

    /// <summary>Starts a Translation animation. Returns false (nothing started) when quitting or the element is not in a live tree.</summary>
    public static bool StartTranslation(UIElement element, CompositionAnimation animation)
    {
        if (!CanAnimate(element)) return false;
        EnableTranslation(element).StartAnimation("Translation", animation);
        return true;
    }

    /// <summary>Stops any Translation animation and, when <paramref name="resetTo"/> is given, sets the offset. Skipped while quitting.</summary>
    public static void StopTranslation(UIElement element, Vector3? resetTo = null)
    {
        if (IsShuttingDown) return;
        var visual = EnableTranslation(element);
        visual.StopAnimation("Translation");
        if (resetTo is { } value) visual.Properties.InsertVector3("Translation", value);
    }

    /// <summary>Sets the Translation offset directly (no animation). Skipped while quitting.</summary>
    public static void SetTranslation(UIElement element, Vector3 value)
    {
        if (IsShuttingDown) return;
        EnableTranslation(element).Properties.InsertVector3("Translation", value);
    }

    public static CompositionEasingFunction EaseOut(Compositor c)
        => c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));

    public static CompositionEasingFunction EaseIn(Compositor c)
        => c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

    /// <summary>
    /// Scale about a relative anchor (default centre). Spring = restrained release settle;
    /// <paramref name="damping"/> only applies to springs (default <see cref="MotionTokens.SpringDamping"/>).
    /// </summary>
    public static void AnimateScale(UIElement element, float target, TimeSpan duration, bool spring = false, Vector2? anchor = null, float damping = MotionTokens.SpringDamping)
        => AnimateScale(element, new Vector2(target, target), duration, spring, anchor, damping);

    /// <summary>
    /// Same as the uniform overload, with separate X and Y targets (a knob that widens but keeps its height).
    /// Composition only: layout, hit areas and drag maths are untouched. Reduced motion snaps back to 1.
    /// </summary>
    public static void AnimateScale(UIElement element, Vector2 target, TimeSpan duration, bool spring = false, Vector2? anchor = null, float damping = MotionTokens.SpringDamping)
    {
        var visual = VisualOf(element);
        var size = element.ActualSize;
        var a = anchor ?? new Vector2(0.5f, 0.5f);
        visual.CenterPoint = new Vector3(size.X * a.X, size.Y * a.Y, 0);
        var final = new Vector3(target.X, target.Y, 1f);

        if (EffectsPolicy.ReduceMotion || duration <= TimeSpan.Zero)
        {
            visual.StopAnimation("Scale");
            visual.Scale = EffectsPolicy.ReduceMotion ? Vector3.One : final;
            return;
        }

        var c = visual.Compositor;
        if (spring)
        {
            var s = c.CreateSpringVector3Animation();
            s.FinalValue = final;
            s.DampingRatio = damping;
            s.Period = duration;
            visual.StartAnimation("Scale", s);
        }
        else
        {
            var k = c.CreateVector3KeyFrameAnimation();
            k.InsertKeyFrame(1f, final, EaseOut(c));
            k.Duration = duration;
            visual.StartAnimation("Scale", k);
        }
    }

    /// <summary>
    /// The element is already laid out at its new spot; it starts displaced by <paramref name="from"/> and springs
    /// to zero (overshoots a little, then settles). Composition Translation only. Reduced motion: no spring.
    /// </summary>
    public static void SpringTranslationFromOffset(UIElement element, Vector3 from, TimeSpan period, float damping)
    {
        StopTranslation(element, Vector3.Zero);
        if (EffectsPolicy.ReduceMotion || from == Vector3.Zero || !CanAnimate(element)) return;
        var visual = VisualOf(element);
        var s = visual.Compositor.CreateSpringVector3Animation();
        s.InitialValue = from;
        s.FinalValue = Vector3.Zero;
        s.DampingRatio = damping;
        s.Period = period;
        StartTranslation(element, s);
    }

    /// <summary>Scale X starts at <paramref name="from"/> and springs to 1 about <paramref name="center"/>. Reduced motion: scale stays 1.</summary>
    public static void SpringScaleXFrom(UIElement element, float from, Vector2 center, TimeSpan period, float damping)
    {
        var visual = VisualOf(element);
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        if (EffectsPolicy.ReduceMotion || from <= 0f || float.IsNaN(from) || float.IsInfinity(from)) return;
        visual.CenterPoint = new Vector3(center.X, center.Y, 0);
        var s = visual.Compositor.CreateSpringVector3Animation();
        s.InitialValue = new Vector3(from, 1f, 1f);
        s.FinalValue = Vector3.One;
        s.DampingRatio = damping;
        s.Period = period;
        visual.StartAnimation("Scale", s);
    }

    /// <summary>
    /// One stretch along X about <paramref name="center"/> (the element's own coordinates): scale rises to
    /// <paramref name="peak"/> early in the move and eases back to 1. Layout and hit areas are untouched.
    /// Reduced motion: scale is reset to 1 and nothing plays.
    /// </summary>
    public static void StretchX(UIElement element, float peak, TimeSpan duration, Vector2 center)
    {
        var visual = VisualOf(element);
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        if (EffectsPolicy.ReduceMotion || duration <= TimeSpan.Zero) return;

        visual.CenterPoint = new Vector3(center.X, center.Y, 0);
        var c = visual.Compositor;
        var k = c.CreateVector3KeyFrameAnimation();
        k.InsertKeyFrame(0.35f, new Vector3(peak, 1f, 1f), EaseOut(c));
        k.InsertKeyFrame(1f, Vector3.One, EaseOut(c));
        k.Duration = duration;
        visual.StartAnimation("Scale", k);
    }

    /// <summary>Animate the composition Translation (layout is unaffected, so hit areas stay put).</summary>
    public static void AnimateTranslation(UIElement element, Vector3 target, TimeSpan duration, bool spring = false, CompositionEasingFunction? easing = null)
    {
        if (EffectsPolicy.ReduceMotion || duration <= TimeSpan.Zero)
        {
            StopTranslation(element, target);
            return;
        }
        if (!CanAnimate(element)) return;
        var visual = EnableTranslation(element);

        var c = visual.Compositor;
        if (spring)
        {
            var s = c.CreateSpringVector3Animation();
            s.FinalValue = target;
            s.DampingRatio = MotionTokens.SpringDamping;
            s.Period = duration;
            StartTranslation(element, s);
        }
        else
        {
            var k = c.CreateVector3KeyFrameAnimation();
            k.InsertKeyFrame(1f, target, easing ?? EaseOut(c));
            k.Duration = duration;
            StartTranslation(element, k);
        }
    }

    /// <summary>
    /// Ramps an element from invisible to full opacity once (ease-out), for the lens rim and band when a
    /// window opens. Compositor-driven, so there is no per-frame work. Does nothing under reduced motion
    /// (the element stays as it is).
    /// </summary>
    public static void Materialise(UIElement element, TimeSpan duration)
    {
        if (EffectsPolicy.ReduceMotion) return;
        var visual = VisualOf(element);
        var c = visual.Compositor;
        visual.StopAnimation("Opacity");
        visual.Opacity = 0f;
        var k = c.CreateScalarKeyFrameAnimation();
        k.InsertKeyFrame(1f, 1f, EaseOut(c));
        k.Duration = duration;
        visual.StartAnimation("Opacity", k);
    }

    /// <summary>Opacity fade. Allowed under reduced motion, but capped to a short crossfade.</summary>
    public static void AnimateOpacity(UIElement element, float target, TimeSpan duration, CompositionEasingFunction? easing = null)
    {
        var visual = VisualOf(element);
        if (EffectsPolicy.ReduceMotion && duration > MotionTokens.ReducedFade) duration = MotionTokens.ReducedFade;
        if (duration <= TimeSpan.Zero)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = target;
            return;
        }
        var c = visual.Compositor;
        var k = c.CreateScalarKeyFrameAnimation();
        k.InsertKeyFrame(1f, target, easing ?? EaseOut(c));
        k.Duration = duration;
        visual.StartAnimation("Opacity", k);
    }

    /// <summary>
    /// Flyout/card entrance: fade, rise from the anchor edge, and scale from 0.96 with a tiny
    /// settle. Reduced motion: a short fade only.
    /// </summary>
    public static void PlayEntrance(UIElement root, TaskbarEdge fromEdge)
    {
        var visual = EnableTranslation(root);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.StopAnimation("Translation");
        visual.Opacity = 0f;

        if (EffectsPolicy.ReduceMotion)
        {
            visual.Scale = Vector3.One;
            SetTranslation(root, Vector3.Zero);
            AnimateOpacity(root, 1f, MotionTokens.ReducedFade);
            return;
        }

        var c = visual.Compositor;
        var size = root.ActualSize;
        var anchor = AnchorFor(fromEdge);
        visual.CenterPoint = new Vector3(size.X * anchor.X, size.Y * anchor.Y, 0);
        visual.Scale = new Vector3(MotionTokens.EntranceScale, MotionTokens.EntranceScale, 1f);
        SetTranslation(root, OffsetToward(fromEdge, MotionTokens.EntranceOffset));

        var ease = EaseOut(c);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(MotionTokens.Entrance.TotalMilliseconds * 0.7);
        visual.StartAnimation("Opacity", fade);

        var move = c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1f, Vector3.Zero, ease);
        move.Duration = MotionTokens.Entrance;
        StartTranslation(root, move);

        // Scale settles with a soft spring (the window has no show animation of its own, so nothing fights it).
        var scale = c.CreateSpringVector3Animation();
        scale.InitialValue = new Vector3(MotionTokens.EntranceScale, MotionTokens.EntranceScale, 1f);
        scale.FinalValue = Vector3.One;
        scale.DampingRatio = MotionTokens.EntranceSpringDamping;
        scale.Period = MotionTokens.EntranceSpringPeriod;
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>Dismissal: quick fade and a small drop toward the anchor edge. Completes when done.</summary>
    public static async Task PlayExitAsync(UIElement root, TaskbarEdge towardEdge)
    {
        var visual = VisualOf(root);
        var c = visual.Compositor;
        var done = new TaskCompletionSource();
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        AnimateOpacity(root, 0f, MotionTokens.Dismiss, EaseIn(c));
        if (!EffectsPolicy.ReduceMotion)
        {
            AnimateTranslation(root, OffsetToward(towardEdge, 6f), MotionTokens.Dismiss, easing: EaseIn(c));
            AnimateScale(root, 0.98f, MotionTokens.Dismiss, anchor: AnchorFor(towardEdge));
        }
        batch.End();
        batch.Completed += (_, _) => done.TrySetResult();
        // Safety net: never leave a surface stuck if the batch does not report completion.
        await Task.WhenAny(done.Task, Task.Delay(MotionTokens.Dismiss + TimeSpan.FromMilliseconds(120)));
    }

    /// <summary>
    /// Called after every pointer or key event handled by <see cref="AttachPressFeedback"/>.
    /// <paramref name="changed"/> is true when the target (rest, hover, pressed) changed;
    /// <paramref name="position"/> is the pointer position in the element's own coordinates,
    /// or null for keyboard, focus, cancel and capture-lost events.
    /// </summary>
    public delegate void PressFeedbackChanged(PressFeedbackState state, bool changed, Windows.Foundation.Point? position);

    /// <summary>
    /// Hover lift, press compression and spring release for any element (pointer and keyboard).
    /// The rules live in <see cref="PressFeedbackState"/> (tested in Core); this method feeds it events
    /// and animates the scale towards its target. Uses handledEventsToo so it works on Buttons, which
    /// mark pointer events handled.
    /// <para>Whether a release would count as a click is decided from the pointer position against
    /// ActualSize (no slack) in a PointerMoved handler, not from Entered/Exited: a pressed Button
    /// captures the pointer, and a hold must survive the pointer leaving. Touch never hovers.</para>
    /// Prefer the XAML attached property <see cref="GlassMotion.PressFeedbackProperty"/>.
    /// </summary>
    public static PressFeedbackState AttachPressFeedback(UIElement element, PressFeedbackChanged? onChanged = null)
    {
        var state = new PressFeedbackState();

        static bool IsTouch(PointerRoutedEventArgs e)
            => e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;

        void Apply(bool changed, Windows.Foundation.Point? position)
        {
            if (changed)
            {
                switch (state.Target)
                {
                    case PressTarget.Pressed:
                        AnimateScale(element, MotionTokens.PressScale, MotionTokens.PressSpringPeriod, spring: true, damping: MotionTokens.PressSpringDamping);
                        break;
                    case PressTarget.Hover when state.Spring:
                        AnimateScale(element, MotionTokens.HoverScale, MotionTokens.ReleaseSpringPeriod, spring: true, damping: MotionTokens.ReleaseDamping);
                        break;
                    case PressTarget.Hover:
                        AnimateScale(element, MotionTokens.HoverScale, MotionTokens.Hover);
                        break;
                    case PressTarget.Rest when state.Spring:
                        AnimateScale(element, 1f, MotionTokens.ReleaseSpringPeriod, spring: true, damping: MotionTokens.ReleaseDamping);
                        break;
                    default:
                        AnimateScale(element, 1f, MotionTokens.Hover);
                        break;
                }
            }
            onChanged?.Invoke(state, changed, position);
        }

        // Layout coordinates: the visual scale used for feedback does not change what Button treats as "over".
        bool Inside(PointerRoutedEventArgs e, out Windows.Foundation.Point p)
        {
            p = e.GetCurrentPoint(element).Position;
            var size = element.ActualSize;
            return p.X >= 0 && p.Y >= 0 && p.X < size.X && p.Y < size.Y;
        }

        // A pointer that is no longer down while we still think it is held (an element that does not
        // capture never sees the release outside its bounds) is released first.
        void DropStaleHold(PointerRoutedEventArgs e)
        {
            if (state.IsPointerHeld && !e.GetCurrentPoint(element).IsInContact)
                Apply(state.PointerCanceled(), null);
        }

        // handledEventsToo everywhere: ButtonBase may mark pointer events handled.
        element.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, e) =>
        {
            DropStaleHold(e);
            Inside(e, out var p);
            Apply(state.PointerEntered(IsTouch(e)), p);
        }), true);
        // Exited also arrives when a captured pointer leaves the bounds; that is not a release.
        element.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, e) =>
        {
            Inside(e, out var p);
            Apply(state.PointerExited(), p);
        }), true);
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            Inside(e, out var p);
            Apply(state.PointerPressed(IsTouch(e)), p);
        }), true);
        element.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            DropStaleHold(e);
            bool inside = Inside(e, out var p);
            Apply(state.PointerMoved(inside), p);
        }), true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            Inside(e, out var p);
            Apply(state.PointerReleased(), p);
        }), true);
        // Capture lost or cancelled mid-press: the press ends, there is no click to wait for.
        PointerEventHandler cancel = (_, _) => Apply(state.PointerCanceled(), null);
        element.AddHandler(UIElement.PointerCaptureLostEvent, cancel, true);
        element.AddHandler(UIElement.PointerCanceledEvent, cancel, true);
        element.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is not (VirtualKey.Space or VirtualKey.Enter) || e.KeyStatus.WasKeyDown) return;
            Apply(state.KeyDown(), null);
        }), true);
        element.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is not (VirtualKey.Space or VirtualKey.Enter)) return;
            Apply(state.KeyUp(), null);
        }), true);
        element.LostFocus += (_, _) => Apply(state.FocusLost(), null);
        return state;
    }

    public static Vector2 AnchorFor(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top => new Vector2(0.5f, 0f),
        TaskbarEdge.Left => new Vector2(0f, 0.5f),
        TaskbarEdge.Right => new Vector2(1f, 0.5f),
        _ => new Vector2(0.5f, 1f),
    };

    /// <summary>A small offset pointing from the surface toward the given screen edge.</summary>
    public static Vector3 OffsetToward(TaskbarEdge edge, float distance) => edge switch
    {
        TaskbarEdge.Top => new Vector3(0, -distance, 0),
        TaskbarEdge.Left => new Vector3(-distance, 0, 0),
        TaskbarEdge.Right => new Vector3(distance, 0, 0),
        _ => new Vector3(0, distance, 0),
    };
}

/// <summary>
/// A selection pill that moves like a drop of liquid. Its left and right edges are two separate compositor springs
/// (kept in a property set); an expression turns them into the visual's Translation.X (left edge) and Scale.X
/// ((right - left) / width, about the element's left edge). The edge it moves toward springs faster than the edge it leaves,
/// so the pill stretches toward the destination and then contracts. Nothing runs in C# per frame, layout is untouched
/// (the element keeps its fixed width), and under reduced motion <see cref="SlideTo"/> places it instantly.
/// </summary>
internal sealed class LiquidSlide
{
    private readonly Visual _visual;
    private readonly CompositionPropertySet _props;
    private float _left;
    private float _width;
    private bool _placed;

    public LiquidSlide(UIElement element)
    {
        _visual = Motion.EnableTranslation(element);
        var c = _visual.Compositor;
        _props = c.CreatePropertySet();
        _props.InsertScalar("L", 0f);
        _props.InsertScalar("R", 1f);
        _props.InsertScalar("W", 1f);

        var x = c.CreateExpressionAnimation("props.L");
        x.SetReferenceParameter("props", _props);
        _visual.StartAnimation("Translation.X", x);

        var sx = c.CreateExpressionAnimation("(props.R - props.L) / props.W");
        sx.SetReferenceParameter("props", _props);
        _visual.StartAnimation("Scale.X", sx);
        _visual.CenterPoint = Vector3.Zero;
    }

    /// <summary>Put the pill at <paramref name="left"/> with <paramref name="width"/>, no motion.</summary>
    public void Place(float left, float width)
    {
        _props.StopAnimation("L");
        _props.StopAnimation("R");
        _props.InsertScalar("W", Math.Max(1f, width));
        _props.InsertScalar("L", left);
        _props.InsertScalar("R", left + width);
        _left = left;
        _width = width;
        _placed = true;
    }

    /// <summary>Spring both edges to the new place. Interrupting continues from where the edges are now.</summary>
    public void SlideTo(float left, float width)
    {
        if (EffectsPolicy.ReduceMotion || !_placed)
        {
            Place(left, width);
            return;
        }

        if (Math.Abs(left - _left) < 0.5f)
        {
            // Same destination asked for again (the view model reports the pick and then the pending state, both of
            // which refresh the pill): leave any running springs alone so the slide is not cut short. Only a changed
            // width needs a snap.
            if (Math.Abs(width - _width) >= 0.5f) Place(left, width);
            return;
        }

        bool right = left > _left;
        _props.InsertScalar("W", Math.Max(1f, width));
        Spring("L", left, right ? MotionTokens.CapsuleTrailPeriod : MotionTokens.CapsuleLeadPeriod,
            right ? MotionTokens.CapsuleTrailDamping : MotionTokens.CapsuleLeadDamping);
        Spring("R", left + width, right ? MotionTokens.CapsuleLeadPeriod : MotionTokens.CapsuleTrailPeriod,
            right ? MotionTokens.CapsuleLeadDamping : MotionTokens.CapsuleTrailDamping);
        _left = left;
        _width = width;
        _placed = true;
    }

    private void Spring(string property, float target, TimeSpan period, float damping)
    {
        var s = _visual.Compositor.CreateSpringScalarAnimation();
        s.FinalValue = target;
        s.DampingRatio = damping;
        s.Period = period;
        _props.StartAnimation(property, s);
    }
}

/// <summary>
/// XAML entry points: <c>design:GlassMotion.PressFeedback="True"</c> and <c>design:GlassMotion.ControlFeel="True"</c>
/// (set by the toggle and slider styles; see <see cref="ControlFeel"/>).
/// </summary>
public sealed class GlassMotion
{
    public static readonly DependencyProperty PressFeedbackProperty = DependencyProperty.RegisterAttached(
        "PressFeedback", typeof(bool), typeof(GlassMotion), new PropertyMetadata(false, OnPressFeedbackChanged));

    public static readonly DependencyProperty ControlFeelProperty = DependencyProperty.RegisterAttached(
        "ControlFeel", typeof(bool), typeof(GlassMotion), new PropertyMetadata(false, OnControlFeelChanged));

    public static bool GetControlFeel(DependencyObject obj) => (bool)obj.GetValue(ControlFeelProperty);

    public static void SetControlFeel(DependencyObject obj, bool value) => obj.SetValue(ControlFeelProperty, value);

    private static void OnControlFeelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is true) ControlFeel.Enable(element);
        else ControlFeel.Disable(element);
    }

    public static bool GetPressFeedback(DependencyObject obj) => (bool)obj.GetValue(PressFeedbackProperty);

    public static void SetPressFeedback(DependencyObject obj, bool value) => obj.SetValue(PressFeedbackProperty, value);

    private static void OnPressFeedbackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && e.NewValue is true && e.OldValue is not true) Motion.AttachPressFeedback(element);
    }
}
