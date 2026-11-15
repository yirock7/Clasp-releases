using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// The "feel" of the glass toggle and slider, switched on by <c>design:GlassMotion.ControlFeel="True"</c> in their styles
/// (no control subclass). It does four small things and nothing while the control is at rest:
/// <list type="bullet">
/// <item>A pressed or dragged toggle knob widens about 25% toward the middle of the track (anchored at its left edge when
/// the switch is off, its right edge when on).</item>
/// <item>A pressed or dragged slider thumb grows about 15% about its centre.</item>
/// <item>The soft shadow shapes (<c>KnobShadow</c>, <c>ThumbShadow</c>) are hidden under reduced transparency and High Contrast.</item>
/// <item>Under reduced motion the toggle's stock slide (the transitions of its ToggleStates group) is taken out so the knob
/// jumps; they are put back when motion is allowed again.</item>
/// </list>
/// On top of that it adds the liquid travel (all numbers in <see cref="MotionTokens"/>): the toggle knob springs from its old
/// place to the new one and stretches while it goes (the template's stock slide is removed from the XAML; the knob is moved
/// by composition Translation/Scale on <c>KnobFlow</c>), and a slider thumb and its fill spring when the value jumps
/// (never during a drag). Nothing here runs per frame.
/// The growth is a composition scale of an inner template element (<c>KnobBody</c>, <c>ThumbBody</c>) through
/// <see cref="Motion.AnimateScale(UIElement, Vector2, TimeSpan, bool, Vector2?, float)"/>: layout, hit areas and the control's
/// own drag maths never see it, and <see cref="EffectsPolicy.ReduceMotion"/> turns it into no growth at all.
/// It listens to the template's visual-state groups (events, no polling), hooks on Loaded and unhooks on Unloaded.
/// Template parts are found by name by walking the visual tree once, with no reflection.
/// </summary>
internal sealed class ControlFeel
{
    // One state object per styled control, alive only as long as the control.
    private static readonly ConditionalWeakTable<FrameworkElement, ControlFeel> States = new();

    private readonly FrameworkElement _control;
    private FrameworkElement? _body;      // the element that grows (KnobBody / ThumbBody)
    private FrameworkElement? _shadow;    // KnobShadow / ThumbShadow
    private FrameworkElement? _flow;      // KnobFlow: carries the knob's liquid travel (toggle only)
    private FrameworkElement? _knob;      // SwitchKnob: slides with the control's own transform (toggle only)
    private FrameworkElement? _track;     // SwitchKnobBounds (toggle) / HorizontalTemplate (slider)
    private FrameworkElement? _sliderThumb;   // HorizontalThumb
    private FrameworkElement? _fill;      // HorizontalDecreaseRect
    private float _knobXBeforeChange = float.NaN; // layout X of the knob when the toggle state started to change
    private bool _thumbDragging;
    private long _hookedAt;
    private VisualStateGroup? _common;
    private VisualStateGroup? _toggleStates;
    private List<VisualTransition>? _removedTransitions;
    private bool _hooked;
    private bool _transitionSyncQueued;
    private bool _waitingForTemplate;
    private bool _commonPressed;
    private bool _dragging;
    private bool _grown;
    private Vector2 _anchor = new(0.5f, 0.5f);

    private ControlFeel(FrameworkElement control) => _control = control;

    internal static void Enable(FrameworkElement control)
    {
        if (States.TryGetValue(control, out _)) return;
        var feel = new ControlFeel(control);
        States.Add(control, feel);
        feel.Start();
    }

    internal static void Disable(FrameworkElement control)
    {
        if (!States.TryGetValue(control, out var feel)) return;
        feel.Stop();
        States.Remove(control);
    }

    private void Start()
    {
        _control.Loaded += OnLoaded;
        _control.Unloaded += OnUnloaded;
        if (_control.IsLoaded) Hook();
    }

    private void Stop()
    {
        _control.Loaded -= OnLoaded;
        _control.Unloaded -= OnUnloaded;
        Unhook();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Hook();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unhook();

    private void Hook()
    {
        if (_hooked) return;
        if (!FindParts())
        {
            // The template is not applied or laid out yet (for example the control sits in a collapsed section): retry when
            // the control first gets a size, with no cap. Unsubscribed as soon as the hook succeeds, and in Unhook.
            if (!_waitingForTemplate)
            {
                _waitingForTemplate = true;
                _control.SizeChanged += OnSizeChanged;
            }
            return;
        }

        StopWaiting();
        _hooked = true;
        if (_common is not null) _common.CurrentStateChanged += OnCommonStateChanged;
        if (_toggleStates is not null)
        {
            _toggleStates.CurrentStateChanging += OnToggleStateChanging;
            _toggleStates.CurrentStateChanged += OnToggleStateChanged;
        }
        if (_control is Slider slider)
        {
            slider.ValueChanged += OnSliderValueChanged;
            if (_sliderThumb is Thumb thumb)
            {
                thumb.DragStarted += OnThumbDragStarted;
                thumb.DragCompleted += OnThumbDragCompleted;
            }
        }
        _hookedAt = Environment.TickCount64;
        EffectsPolicy.Changed += OnEffectsChanged;
        ApplyEffects();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_control.IsLoaded) Hook();
    }

    private void StopWaiting()
    {
        if (!_waitingForTemplate) return;
        _control.SizeChanged -= OnSizeChanged;
        _waitingForTemplate = false;
    }

    private void Unhook()
    {
        StopWaiting();
        if (!_hooked) return;
        _hooked = false;
        if (_common is not null) _common.CurrentStateChanged -= OnCommonStateChanged;
        if (_toggleStates is not null)
        {
            _toggleStates.CurrentStateChanging -= OnToggleStateChanging;
            _toggleStates.CurrentStateChanged -= OnToggleStateChanged;
        }
        if (_control is Slider slider)
        {
            slider.ValueChanged -= OnSliderValueChanged;
            if (_sliderThumb is Thumb thumb)
            {
                thumb.DragStarted -= OnThumbDragStarted;
                thumb.DragCompleted -= OnThumbDragCompleted;
            }
        }
        EffectsPolicy.Changed -= OnEffectsChanged;
        RestoreTransitions();

        // Leave the visual as it started: no animation running, no scale left over.
        if (_body is not null && !Motion.IsShuttingDown)
        {
            var visual = Motion.VisualOf(_body);
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
        }
        ResetTravel(_flow);
        ResetTravel(_sliderThumb);
        ResetTravel(_fill);
        _thumbDragging = false;
        _commonPressed = false;
        _dragging = false;
        _grown = false;
        _body = null;
        _shadow = null;
        _flow = null;
        _knob = null;
        _track = null;
        _sliderThumb = null;
        _fill = null;
        _common = null;
        _toggleStates = null;
    }

    // Leaves a travel element as it started: no spring or stretch running, no offset or scale left over.
    private static void ResetTravel(FrameworkElement? element)
    {
        if (element is null) return;
        if (Motion.IsShuttingDown) return;
        var visual = Motion.VisualOf(element);
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        // Translation must be enabled on this visual first; a knob that never moved never enabled it (this threw on quit).
        Motion.StopTranslation(element, Vector3.Zero);
    }

    // The feel is decoration: if a template part cannot be found or the visual-state API throws (for example a cast that
    // Native AOT cannot make), the control simply keeps working without it instead of failing in a Loaded handler.
    private bool FindParts()
    {
        try
        {
            return FindPartsCore();
        }
        catch (Exception)
        {
            _body = null;
            _shadow = null;
            _flow = null;
            _knob = null;
            _track = null;
            _sliderThumb = null;
            _fill = null;
            _common = null;
            _toggleStates = null;
            return false;
        }
    }

    private bool FindPartsCore()
    {
        // A control that has not been measured yet has no template children: build them now.
        (_control as Control)?.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(_control) == 0) return false;
        var root = VisualTreeHelper.GetChild(_control, 0) as FrameworkElement;
        if (root is null) return false;

        if (_control is ToggleSwitch)
        {
            _body = Find(root, "KnobBody");
            _shadow = Find(root, "KnobShadow");
            _flow = Find(root, "KnobFlow");
            _knob = Find(root, "SwitchKnob");
            _track = Find(root, "SwitchKnobBounds");
            if (_body is null) return false;
        }
        else
        {
            // Slider: the horizontal thumb's own template holds the growing body (the vertical one is collapsed).
            var thumb = Find(root, "HorizontalThumb");
            if (thumb is null) return false;
            (thumb as Control)?.ApplyTemplate();
            _body = Find(thumb, "ThumbBody");
            _shadow = Find(thumb, "ThumbShadow");
            _sliderThumb = thumb;
            _track = Find(root, "HorizontalTemplate");
            _fill = Find(root, "HorizontalDecreaseRect");
            if (_body is null) return false;
        }

        foreach (var group in VisualStateManager.GetVisualStateGroups(root))
        {
            if (group.Name == "CommonStates") _common = group;
            else if (group.Name == "ToggleStates") _toggleStates = group;
        }
        return true;
    }

    private static FrameworkElement? Find(DependencyObject parent, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            var found = Find(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    private void OnEffectsChanged(object? sender, EventArgs e) => ApplyEffects();

    private void ApplyEffects()
    {
        if (_shadow is not null)
            _shadow.Visibility = EffectsPolicy.ReduceTransparency ? Visibility.Collapsed : Visibility.Visible;

        if (EffectsPolicy.ReduceMotion)
        {
            // A growth that is showing goes back to 1 at once.
            if (_grown) SetGrown(false);
        }

        ScheduleTransitionSync();
    }

    // The Changed event can fire synchronously from inside the Reduce motion toggle's own click, while that toggle (or
    // another) may be mid-transition. Clearing or re-adding its Transitions right then can fault natively, so the work is
    // moved to a later UI tick. It reads the policy again when it runs, so repeated flips end in the right state and
    // only one callback is ever queued.
    private void ScheduleTransitionSync()
    {
        if (_transitionSyncQueued) return;
        var queue = _control.DispatcherQueue;
        if (queue is null) return;
        _transitionSyncQueued = true;
        if (!queue.TryEnqueue(SyncTransitions)) _transitionSyncQueued = false;
    }

    private void SyncTransitions()
    {
        _transitionSyncQueued = false;
        // The control may have unloaded since (Unhook already restored its transitions).
        if (!_hooked || !_control.IsLoaded) return;
        if (EffectsPolicy.ReduceMotion) RemoveTransitions();
        else RestoreTransitions();
    }

    // The toggle's stock slide is a theme animation inside the ToggleStates transitions, which knows nothing about Clasp's
    // own Reduce motion setting. With the transitions out, the knob jumps to its new place. They are kept here and put back.
    private void RemoveTransitions()
    {
        if (_toggleStates is null || _removedTransitions is not null) return;
        try
        {
            var removed = new List<VisualTransition>();
            foreach (var transition in _toggleStates.Transitions) removed.Add(transition);
            // Nothing to take out (or already taken out): never store an empty list over a real one.
            if (removed.Count == 0) return;
            _toggleStates.Transitions.Clear();
            _removedTransitions = removed;
        }
        catch (Exception)
        {
            // Keep the stock slide.
        }
    }

    private void RestoreTransitions()
    {
        if (_toggleStates is null || _removedTransitions is null) return;
        var removed = _removedTransitions;
        _removedTransitions = null;
        try
        {
            foreach (var transition in removed) _toggleStates.Transitions.Add(transition);
        }
        catch (Exception)
        {
            // Nothing more to do.
        }
    }

    private void OnCommonStateChanged(object sender, VisualStateChangedEventArgs e)
    {
        _commonPressed = e.NewState?.Name == "Pressed";
        Refresh();
    }

    // Fires before the new state is applied, so the knob is still where the user (or the last slide) left it.
    private void OnToggleStateChanging(object sender, VisualStateChangedEventArgs e)
    {
        if (_knob is null || _track is null) return;
        try
        {
            _knobXBeforeChange = (float)_knob.TransformToVisual(_track).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        }
        catch (Exception)
        {
            _knobXBeforeChange = float.NaN; // not laid out: no travel animation this time
        }
    }

    private void OnToggleStateChanged(object sender, VisualStateChangedEventArgs e)
    {
        string? name = e.NewState?.Name;
        _dragging = name == "Dragging";
        Refresh();
        if (name is "On" or "Off") PlayKnobTravel(toOn: name == "On");
    }

    // The knob has jumped to its new place (the stock slide is taken out of the template); it starts at the old place as a
    // composition offset and springs across, stretching along the way. Skipped under reduced motion, right after load
    // (a toggle that starts On is not a user action) and when the move is tiny.
    private void PlayKnobTravel(bool toOn)
    {
        float from = _knobXBeforeChange;
        _knobXBeforeChange = float.NaN;
        if (_flow is null || _knob is null || _track is null) return;
        if (EffectsPolicy.ReduceMotion || !_control.IsLoaded) return;
        if (Environment.TickCount64 - _hookedAt < 300) return;
        if (float.IsNaN(from)) return;

        float to = toOn ? (float)(_track.ActualWidth - _knob.ActualWidth) : 0f;
        float offset = from - to;
        if (Math.Abs(offset) < 1f) return;

        Motion.SpringTranslationFromOffset(_flow, new Vector3(offset, 0f, 0f), MotionTokens.KnobSpringPeriod, MotionTokens.KnobSpringDamping);
        Motion.StretchX(_flow, MotionTokens.KnobTravelStretch, MotionTokens.KnobStretchDuration,
            new Vector2((float)_flow.ActualWidth / 2f, (float)_flow.ActualHeight / 2f));
    }

    private void OnThumbDragStarted(object sender, DragStartedEventArgs e) => _thumbDragging = true;

    private void OnThumbDragCompleted(object sender, DragCompletedEventArgs e) => _thumbDragging = false;

    // A value that jumps (a click on the track, a PageUp step, a device report) moves the thumb and fill in one layout step.
    // The thumb starts at its old spot and springs across; the filled part of the track follows by springing its length.
    // Continuous dragging is left alone (the thumb must stay under the pointer), and so is anything smaller than
    // MotionTokens.SliderSpringMinDistance. The value, its events and the keyboard are never touched.
    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_thumbDragging || _sliderThumb is null || _track is null || _control is not Slider slider) return;
        if (EffectsPolicy.ReduceMotion || !_control.IsLoaded) return;
        if (Environment.TickCount64 - _hookedAt < 300) return;

        double range = slider.Maximum - slider.Minimum;
        double travel = _track.ActualWidth - _sliderThumb.ActualWidth;
        if (range <= 0 || travel <= 0) return;

        double oldW = (e.OldValue - slider.Minimum) / range * travel;
        double newW = (e.NewValue - slider.Minimum) / range * travel;
        float dx = (float)(oldW - newW);
        if (Math.Abs(dx) < MotionTokens.SliderSpringMinDistance) return;

        Motion.SpringTranslationFromOffset(_sliderThumb, new Vector3(dx, 0f, 0f), MotionTokens.SliderSpringPeriod, MotionTokens.SliderSpringDamping);
        if (_fill is not null && newW >= 8)
        {
            Motion.SpringScaleXFrom(_fill, (float)(oldW / newW), new Vector2(0f, (float)_fill.ActualHeight / 2f),
                MotionTokens.SliderSpringPeriod, MotionTokens.SliderSpringDamping);
        }
    }

    private void Refresh()
    {
        bool wanted = _commonPressed || _dragging;
        if (wanted == _grown) return;
        SetGrown(wanted);
    }

    private void SetGrown(bool grown)
    {
        if (_body is null) return;
        _grown = grown;

        if (_control is ToggleSwitch toggle)
        {
            // The knob widens toward the middle of the track; the anchor chosen at press time is kept for the release.
            if (grown) _anchor = toggle.IsOn ? new Vector2(1f, 0.5f) : new Vector2(0f, 0.5f);
            if (grown)
                Motion.AnimateScale(_body, new Vector2(MotionTokens.KnobPressWiden, 1f), MotionTokens.PressSpringPeriod, spring: true, anchor: _anchor, damping: MotionTokens.PressSpringDamping);
            else
                Motion.AnimateScale(_body, Vector2.One, MotionTokens.ReleaseSpringPeriod, spring: true, anchor: _anchor, damping: MotionTokens.ReleaseDamping);
        }
        else
        {
            if (grown)
                Motion.AnimateScale(_body, new Vector2(MotionTokens.ThumbGrowX, MotionTokens.ThumbGrowY), MotionTokens.PressSpringPeriod, spring: true, damping: MotionTokens.PressSpringDamping);
            else
                Motion.AnimateScale(_body, Vector2.One, MotionTokens.ReleaseSpringPeriod, spring: true, damping: MotionTokens.ReleaseDamping);
        }
    }
}
