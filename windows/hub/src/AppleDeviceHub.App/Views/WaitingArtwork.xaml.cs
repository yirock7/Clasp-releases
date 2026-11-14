using System.Numerics;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Ble;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// The welcome guide's hero. <see cref="Update"/> says whether a device has been found:
/// not found = the supported shapes crossfade one after another (about 2.5 s each) with a soft scale, driven by one
/// Forever composition animation per shape layer (no timer, no per-frame code); found = the cycle stops and the
/// device's own drawing settles in with a check badge. Reduce motion: no cycling, a plain AirPods case, then a straight
/// switch to the device. <see cref="Pause"/> and <see cref="Resume"/> stop and restart the cycle when the window is
/// hidden or the page is not showing.
/// </summary>
public sealed partial class WaitingArtwork : UserControl
{
    private const double SlotSeconds = 2.5;
    private const double FadeSeconds = 0.6;

    private readonly List<ModelArtwork> _layers = new();
    private DetectedDevice? _device;
    private bool _paused;

    public WaitingArtwork()
    {
        InitializeComponent();
        foreach (ushort modelId in PairingArtworkCycle.Models)
        {
            var layer = new ModelArtwork { Group = AppleModelCatalog.ArtworkOf(modelId), IsHitTestVisible = false };
            layer.SetModel(modelId, null);
            layer.ShowOpen();
            _layers.Add(layer);
            CycleLayers.Children.Add(layer);
        }
        Unloaded += (_, _) => StopCycle();
        Loaded += (_, _) => Apply(settle: false);
        EffectsPolicy.Changed += OnEffectsChanged;
        Unloaded += (_, _) => EffectsPolicy.Changed -= OnEffectsChanged;
        Loaded += (_, _) =>
        {
            EffectsPolicy.Changed -= OnEffectsChanged;
            EffectsPolicy.Changed += OnEffectsChanged;
        };
    }

    /// <summary>True once a device has been found and the art has settled on it.</summary>
    public bool IsFound => _device is not null;

    /// <summary>Tells the art what has been found (null = still looking). Repeating the same device does nothing.</summary>
    public void Update(DetectedDevice? device)
    {
        if (Equals(device, _device)) return;
        _device = device;
        Apply(settle: true);
    }

    /// <summary>Stops the cycle (window hidden, or the page is not showing).</summary>
    public void Pause()
    {
        _paused = true;
        StopCycle();
    }

    /// <summary>Starts the cycle again if nothing has been found.</summary>
    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        Apply(settle: false);
    }

    private void OnEffectsChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Apply(settle: false);
        else DispatcherQueue.TryEnqueue(() => Apply(settle: false));
    }

    private void Apply(bool settle)
    {
        StopCycle();
        if (_device is { } device)
        {
            CycleLayers.Visibility = Visibility.Collapsed;
            FoundArt.SetModel(device.ModelId, null);
            FoundArt.Group = device.Art;
            FoundArt.ShowOpen();
            FoundArt.Visibility = Visibility.Visible;
            Badge.Visibility = Visibility.Visible;
            if (settle) Settle();
            else Rest();
            return;
        }

        FoundArt.Visibility = Visibility.Collapsed;
        Badge.Visibility = Visibility.Collapsed;
        CycleLayers.Visibility = Visibility.Visible;
        if (EffectsPolicy.ReduceMotion)
        {
            // No cycling: one plain AirPods Pro case.
            for (int i = 0; i < _layers.Count; i++)
                Motion.VisualOf(_layers[i]).Opacity = PairingArtworkCycle.Models[i] == PairingArtworkCycle.Resting ? 1f : 0f;
            return;
        }
        if (_paused) { ShowFirst(); return; }
        StartCycle();
    }

    private void ShowFirst()
    {
        for (int i = 0; i < _layers.Count; i++) Motion.VisualOf(_layers[i]).Opacity = i == 0 ? 1f : 0f;
    }

    private void StopCycle()
    {
        if (Motion.IsShuttingDown) return;
        foreach (var layer in _layers)
        {
            var visual = Motion.VisualOf(layer);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
        }
        ShowFirst();
    }

    private void StartCycle()
    {
        if (!Motion.CanAnimate(CycleLayers)) return;
        int n = _layers.Count;
        double period = n * SlotSeconds;
        var span = TimeSpan.FromSeconds(period);
        float P(double t) => (float)Math.Clamp(t / period, 0d, 1d);

        for (int i = 0; i < n; i++)
        {
            var visual = Motion.VisualOf(_layers[i]);
            visual.CenterPoint = new Vector3(100f, 75f, 0f);
            var c = visual.Compositor;
            var ease = Motion.EaseOut(c);

            // Layer i is fully shown from i*slot to (i+1)*slot. It fades in during the FadeSeconds before its slot and
            // out during the FadeSeconds before the next one, so the shapes cross over without ever both being solid.
            double start = i * SlotSeconds, end = (i + 1) * SlotSeconds;
            var fade = c.CreateScalarKeyFrameAnimation();
            var grow = c.CreateVector3KeyFrameAnimation();
            fade.Duration = grow.Duration = span;
            fade.IterationBehavior = grow.IterationBehavior = AnimationIterationBehavior.Forever;

            float on = 1f, off = 0f;
            var small = new Vector3(0.92f, 0.92f, 1f);
            var full = Vector3.One;
            var large = new Vector3(1.04f, 1.04f, 1f);

            if (i == 0)
            {
                // Starts the loop already shown (the last layer has finished fading out by then).
                fade.InsertKeyFrame(0f, on);
                fade.InsertKeyFrame(P(end - FadeSeconds), on);
                fade.InsertKeyFrame(P(end), off, ease);
                fade.InsertKeyFrame(P(period - FadeSeconds), off);
                fade.InsertKeyFrame(1f, on, ease);
                grow.InsertKeyFrame(0f, full);
                grow.InsertKeyFrame(P(end), large, ease);
                grow.InsertKeyFrame(P(period - FadeSeconds), small);
                grow.InsertKeyFrame(1f, full, ease);
            }
            else
            {
                fade.InsertKeyFrame(0f, off);
                fade.InsertKeyFrame(P(start - FadeSeconds), off);
                fade.InsertKeyFrame(P(start), on, ease);
                fade.InsertKeyFrame(P(end - FadeSeconds), on);
                fade.InsertKeyFrame(P(end), off, ease);
                fade.InsertKeyFrame(1f, off);
                grow.InsertKeyFrame(0f, small);
                grow.InsertKeyFrame(P(start - FadeSeconds), small);
                grow.InsertKeyFrame(P(start), full, ease);
                grow.InsertKeyFrame(P(end), large, ease);
                grow.InsertKeyFrame(1f, large);
            }

            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Scale", grow);
        }
    }

    /// <summary>A found device: a gentle scale settle and the check badge popping in. Reduce motion: straight there.</summary>
    private void Settle()
    {
        if (EffectsPolicy.ReduceMotion || !Motion.CanAnimate(FoundArt))
        {
            Rest();
            return;
        }
        var art = Motion.VisualOf(FoundArt);
        art.CenterPoint = new Vector3(100f, 75f, 0f);
        art.Scale = new Vector3(0.92f, 0.92f, 1f);
        var spring = art.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = Vector3.One;
        spring.DampingRatio = MotionTokens.SpringDamping;
        spring.Period = MotionTokens.Release;
        art.StartAnimation("Scale", spring);

        var badge = Motion.VisualOf(Badge);
        badge.CenterPoint = new Vector3(15f, 15f, 0f);
        badge.Scale = Vector3.Zero;
        badge.Opacity = 0f;
        var pop = badge.Compositor.CreateSpringVector3Animation();
        pop.FinalValue = Vector3.One;
        pop.DampingRatio = 0.6f;
        pop.Period = MotionTokens.Release;
        pop.DelayTime = TimeSpan.FromMilliseconds(120);
        badge.StartAnimation("Scale", pop);
        var show = badge.Compositor.CreateScalarKeyFrameAnimation();
        show.InsertKeyFrame(1f, 1f);
        show.DelayTime = TimeSpan.FromMilliseconds(120);
        show.Duration = MotionTokens.Dismiss;
        badge.StartAnimation("Opacity", show);
    }

    private void Rest()
    {
        var art = Motion.VisualOf(FoundArt);
        art.StopAnimation("Scale");
        art.Scale = Vector3.One;
        var badge = Motion.VisualOf(Badge);
        badge.StopAnimation("Scale");
        badge.StopAnimation("Opacity");
        badge.Scale = Vector3.One;
        badge.Opacity = 1f;
    }
}
