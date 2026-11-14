using System.Numerics;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Ble;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// Provisional, original case artwork. The lid opens with a non-uniform composition scale
/// about its hinge (bottom edge) plus a small lift, the lid underside fades in, then the
/// earbuds settle with a restrained spring. Total ~MotionTokens.CaseLid. Reduced motion:
/// jump straight to the open state. Shown open by default so the art is never blank.
/// The seam glow (the Clasp mark's light) swells as the lid lifts, then rests dimmer.
/// </summary>
public sealed partial class CaseArtwork : UserControl
{
    private const float LidWidth = 160f;
    private const float LidHeight = 34f;
    private const float LidLift = -30f;
    private const float LidOpenScaleY = 0.55f;
    private static readonly Vector3 BudsStartOffset = new(0f, -6f, 0f);
    private const float GlowRest = 0.55f;

    private bool _hasPlayed;

    public CaseArtwork()
    {
        InitializeComponent();
        Motion.EnableTranslation(Lid);
        Motion.EnableTranslation(Buds);
        Loaded += (_, _) =>
        {
            if (!_hasPlayed) ShowOpen();
        };
    }

    /// <summary>
    /// Choose which earbud drawing sits inside the case. Only the buds change: the lid, body, seam glow and
    /// every animation are untouched. Groups without a case fall back to the stem earbuds.
    /// </summary>
    public void SetBuds(ArtworkGroup group)
    {
        StemBuds.Visibility = group is not (ArtworkGroup.ProEarbuds or ArtworkGroup.BudsWithCase or ArtworkGroup.EarHookWithCase)
            ? Visibility.Visible : Visibility.Collapsed;
        ProBuds.Visibility = group == ArtworkGroup.ProEarbuds ? Visibility.Visible : Visibility.Collapsed;
        CaseBuds.Visibility = group == ArtworkGroup.BudsWithCase ? Visibility.Visible : Visibility.Collapsed;
        HookBuds.Visibility = group == ArtworkGroup.EarHookWithCase ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Final state: lid open, earbuds visible.</summary>
    public void ShowOpen() => Apply(open: true);

    /// <summary>Lid closed, earbuds hidden behind it.</summary>
    public void ShowClosed() => Apply(open: false);

    /// <summary>Play the opening once. Calling again restarts from closed (no queued motion).</summary>
    public void PlayOpen()
    {
        _hasPlayed = true;
        if (EffectsPolicy.ReduceMotion)
        {
            ShowOpen();
            return;
        }

        ShowClosed();
        var lid = Motion.VisualOf(Lid);
        var inner = Motion.VisualOf(LidInner);
        var buds = Motion.VisualOf(Buds);
        var glow = Motion.VisualOf(SeamGlow);
        var c = lid.Compositor;
        var ease = Motion.EaseOut(c);
        TimeSpan duration = MotionTokens.CaseLid;

        // Lid: foreshortens about the hinge while lifting; a 2 DIP overshoot settles quickly.
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, new Vector3(1f, LidOpenScaleY, 1f), ease);
        scale.Duration = duration;
        lid.StartAnimation("Scale", scale);

        var lift = c.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(0.8f, new Vector3(0f, LidLift - 2f, 0f), ease);
        lift.InsertKeyFrame(1f, new Vector3(0f, LidLift, 0f), ease);
        lift.Duration = duration;
        Motion.StartTranslation(Lid, lift);

        var innerFade = c.CreateScalarKeyFrameAnimation();
        innerFade.InsertKeyFrame(1f, 1f, ease);
        innerFade.DelayTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3);
        innerFade.Duration = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.6);
        inner.StartAnimation("Opacity", innerFade);

        // Light spills out as the seam opens, then rests a little dimmer.
        var glowIn = c.CreateScalarKeyFrameAnimation();
        glowIn.InsertKeyFrame(0.55f, 1f, ease);
        glowIn.InsertKeyFrame(1f, GlowRest, ease);
        glowIn.Duration = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 2.2);
        glow.StartAnimation("Opacity", glowIn);

        // Earbuds: appear once the lid clears them, then settle into the case.
        TimeSpan budsDelay = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4);
        var budsFade = c.CreateScalarKeyFrameAnimation();
        budsFade.InsertKeyFrame(1f, 1f, ease);
        budsFade.DelayTime = budsDelay;
        budsFade.Duration = TimeSpan.FromMilliseconds(140);
        buds.StartAnimation("Opacity", budsFade);

        var settle = c.CreateSpringVector3Animation();
        settle.FinalValue = Vector3.Zero;
        settle.DampingRatio = MotionTokens.SpringDamping;
        settle.Period = MotionTokens.Release;
        settle.DelayTime = budsDelay;
        Motion.StartTranslation(Buds, settle);
    }

    /// <summary>
    /// Loops "lid opens, stays open, lid closes, rests" with composition keyframes on the same visuals PlayOpen uses
    /// (Forever iteration: no timer, no per-frame code). Times are in seconds from the start of each loop.
    /// Call <see cref="ShowOpen"/> or <see cref="ShowClosed"/> to stop it.
    /// </summary>
    public void StartLidLoop(double periodSeconds, double openAt, double closeAt)
    {
        if (EffectsPolicy.ReduceMotion || !Motion.CanAnimate(Lid)) { ShowOpen(); return; }
        Apply(open: false);
        var lid = Motion.VisualOf(Lid);
        var inner = Motion.VisualOf(LidInner);
        var buds = Motion.VisualOf(Buds);
        var glow = Motion.VisualOf(SeamGlow);
        var c = lid.Compositor;
        var ease = Motion.EaseOut(c);
        const double move = 0.35;
        float P(double t) => (float)Math.Clamp(t / periodSeconds, 0d, 1d);
        var span = TimeSpan.FromSeconds(periodSeconds);

        var closedScale = Vector3.One;
        var openScale = new Vector3(1f, LidOpenScaleY, 1f);
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, closedScale);
        scale.InsertKeyFrame(P(openAt), closedScale);
        scale.InsertKeyFrame(P(openAt + move), openScale, ease);
        scale.InsertKeyFrame(P(closeAt), openScale);
        scale.InsertKeyFrame(P(closeAt + move), closedScale, ease);
        scale.InsertKeyFrame(1f, closedScale);
        scale.Duration = span;
        scale.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        lid.StartAnimation("Scale", scale);

        var lift = c.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(0f, Vector3.Zero);
        lift.InsertKeyFrame(P(openAt), Vector3.Zero);
        lift.InsertKeyFrame(P(openAt + move), new Vector3(0f, LidLift, 0f), ease);
        lift.InsertKeyFrame(P(closeAt), new Vector3(0f, LidLift, 0f));
        lift.InsertKeyFrame(P(closeAt + move), Vector3.Zero, ease);
        lift.InsertKeyFrame(1f, Vector3.Zero);
        lift.Duration = span;
        lift.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        Motion.StartTranslation(Lid, lift);

        void Fade(Microsoft.UI.Composition.Visual visual, float top)
        {
            var a = c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0f, 0f);
            a.InsertKeyFrame(P(openAt + move * 0.4), 0f);
            a.InsertKeyFrame(P(openAt + move * 1.2), top, ease);
            a.InsertKeyFrame(P(closeAt), top);
            a.InsertKeyFrame(P(closeAt + move * 0.6), 0f, ease);
            a.InsertKeyFrame(1f, 0f);
            a.Duration = span;
            a.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", a);
        }
        Fade(inner, 1f);
        Fade(buds, 1f);
        Fade(glow, GlowRest);
    }

    private void Apply(bool open)
    {
        if (Motion.IsShuttingDown) return;
        var lid = Motion.VisualOf(Lid);
        var inner = Motion.VisualOf(LidInner);
        var buds = Motion.VisualOf(Buds);
        var glow = Motion.VisualOf(SeamGlow);

        glow.StopAnimation("Opacity");
        lid.StopAnimation("Scale");
        Motion.StopTranslation(Lid);
        inner.StopAnimation("Opacity");
        buds.StopAnimation("Opacity");
        Motion.StopTranslation(Buds);

        lid.CenterPoint = new Vector3(LidWidth / 2f, LidHeight, 0f);
        lid.Scale = open ? new Vector3(1f, LidOpenScaleY, 1f) : Vector3.One;
        Motion.SetTranslation(Lid, open ? new Vector3(0f, LidLift, 0f) : Vector3.Zero);
        inner.Opacity = open ? 1f : 0f;
        buds.Opacity = open ? 1f : 0f;
        glow.Opacity = open ? GlowRest : 0f;
        Motion.SetTranslation(Buds, open ? Vector3.Zero : BudsStartOffset);
    }
}
