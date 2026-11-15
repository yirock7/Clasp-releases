// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Numerics;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Artwork;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// The hinged lid solid as two stacks of thin slices (the outside seen while the lid is closed or part open, the inside once it
/// has swung past its tilt angle). One composition property, <c>Theta</c> in radians, drives every slice through expression
/// animations (vertical scale about the hinge line plus a shift per unit of slice height, the maths of
/// <see cref="LidSolid"/>), so opening, closing and the loop run on the compositor with no per-frame code.
/// </summary>
internal sealed class LidVisual
{
    /// <summary>Slices per stack. More is smoother and costs more visuals; 40 is smooth enough at hero size.</summary>
    private const int SliceCount = 40;

    private const double BudFadeStartDegrees = 6;
    private const double BudFadeSpanDegrees = 14;

    private readonly ArtLidNode _lid;
    private readonly ArtworkRenderer _renderer;
    private readonly CompositionPropertySet _props;
    private readonly Compositor _compositor;
    private readonly double _phi;

    public LidVisual(ArtLidNode lid, ArtworkRenderer renderer)
    {
        _lid = lid;
        _renderer = renderer;
        _phi = lid.Spec.TiltDegrees * Math.PI / 180;

        InsideHost = new Canvas { IsHitTestVisible = false };
        OutsideHost = new Canvas { IsHitTestVisible = false };
        _compositor = Motion.VisualOf(OutsideHost).Compositor;
        _props = _compositor.CreatePropertySet();
        _props.InsertScalar("Theta", 0f);

        for (var i = SliceCount; i >= 0; i--)
        {
            InsideHost.Children.Add(Slice(i, inside: true));
        }

        for (var i = 0; i <= SliceCount; i++)
        {
            OutsideHost.Children.Add(Slice(i, inside: false));
        }

        if (!lid.Transform.IsIdentity)
        {
            ArtworkRenderer.ApplyNode(InsideHost, lid.Transform, 1);
            ArtworkRenderer.ApplyNode(OutsideHost, lid.Transform, 1);
        }

        Bind(InsideHost, "Opacity", $"props.Theta > {F(_phi)} ? 1.0f : 0.0f");
        Bind(OutsideHost, "Opacity", $"props.Theta <= {F(_phi)} ? 1.0f : 0.0f");
    }

    /// <summary>The inside face stack: placed where the lid sits in the drawing, behind the buds.</summary>
    public Canvas InsideHost { get; }

    /// <summary>The outer shell stack: placed above everything except the light.</summary>
    public Canvas OutsideHost { get; }

    public double OpenDegrees => _lid.Spec.OpenDegrees;

    /// <summary>Makes the buds fade in as the lid opens (hidden up to 6 degrees, fully shown from 20), as <see cref="LidSolid.BudOpacity"/>.</summary>
    public void BindBuds(UIElement buds)
    {
        var start = BudFadeStartDegrees * Math.PI / 180;
        var span = BudFadeSpanDegrees * Math.PI / 180;
        Bind(buds, "Opacity", $"Clamp((props.Theta - {F(start)}) / {F(span)}, 0.0f, 1.0f)");
    }

    /// <summary>
    /// The same fade, one host per bud, with a "free" flag per bud (<see cref="SetBudFree"/>): a bud that has left the case shows
    /// whatever the lid does, so each bud needs its own opacity.
    /// </summary>
    public void BindBuds(IReadOnlyList<UIElement> budHosts)
    {
        var start = BudFadeStartDegrees * Math.PI / 180;
        var span = BudFadeSpanDegrees * Math.PI / 180;
        for (var i = 0; i < budHosts.Count; i++)
        {
            _props.InsertScalar(FreeName(i), 0f);
            Bind(budHosts[i], "Opacity", $"Max(Clamp((props.Theta - {F(start)}) / {F(span)}, 0.0f, 1.0f), props.{FreeName(i)})");
        }
    }

    private static string FreeName(int index) => "Free" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Keeps bud <paramref name="index"/> visible whatever the lid angle (true), or hands it back to the lid fade (false).</summary>
    public void SetBudFree(int index, bool free)
    {
        if (Motion.IsShuttingDown)
        {
            return;
        }

        _props.StopAnimation(FreeName(index));
        _props.InsertScalar(FreeName(index), free ? 1f : 0f);
    }

    /// <summary>Keeps bud <paramref name="index"/> visible for <paramref name="duration"/>, then hands it back to the lid fade.</summary>
    public void ReleaseBudFreeAfter(int index, TimeSpan duration)
    {
        if (Motion.IsShuttingDown)
        {
            return;
        }

        var hold = _compositor.CreateScalarKeyFrameAnimation();
        hold.InsertKeyFrame(0f, 1f);
        hold.InsertKeyFrame(0.999f, 1f);
        hold.InsertKeyFrame(1f, 0f);
        hold.Duration = duration;
        _props.StartAnimation(FreeName(index), hold);
    }

    /// <summary>Stops any animation and puts the lid at <paramref name="degrees"/>.</summary>
    public void SetAngle(double degrees)
    {
        if (Motion.IsShuttingDown)
        {
            return;
        }

        _props.StopAnimation("Theta");
        _props.InsertScalar("Theta", (float)(degrees * Math.PI / 180));
    }

    /// <summary>Swings from wherever the lid is to <paramref name="degrees"/> with the standard ease-out, after an optional <paramref name="delay"/> (it holds still meanwhile).</summary>
    public void AnimateTo(double degrees, TimeSpan duration, TimeSpan delay = default)
    {
        if (Motion.IsShuttingDown)
        {
            return;
        }

        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, (float)(degrees * Math.PI / 180), Motion.EaseOut(_compositor));
        animation.Duration = duration;
        if (delay > TimeSpan.Zero)
        {
            animation.DelayTime = delay;
        }

        _props.StartAnimation("Theta", animation);
    }

    /// <summary>Closed, then open at <paramref name="openAt"/>, closed again at <paramref name="closeAt"/> (seconds into each loop), forever.</summary>
    public void StartLoop(double periodSeconds, double openAt, double closeAt)
    {
        if (Motion.IsShuttingDown)
        {
            return;
        }

        const double move = 0.48;
        float P(double t) => (float)Math.Clamp(t / periodSeconds, 0d, 1d);
        var open = (float)(OpenDegrees * Math.PI / 180);
        var ease = Motion.EaseOut(_compositor);

        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0f);
        animation.InsertKeyFrame(P(openAt), 0f);
        animation.InsertKeyFrame(P(openAt + move), open, ease);
        animation.InsertKeyFrame(P(closeAt), open);
        animation.InsertKeyFrame(P(closeAt + move), 0f, ease);
        animation.InsertKeyFrame(1f, 0f);
        animation.Duration = TimeSpan.FromSeconds(periodSeconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        _props.StartAnimation("Theta", animation);
    }

    /// <summary>Stops the lid animation where it is.</summary>
    public void Stop()
    {
        if (Motion.IsShuttingDown)
        {
            return;
        }

        _props.StopAnimation("Theta");
    }

    private Canvas Slice(int i, bool inside)
    {
        var spec = _lid.Spec;
        var shape = LidSolid.SliceShape(spec, i, SliceCount);
        var container = new Canvas { IsHitTestVisible = false };
        Canvas.SetTop(container, spec.HingeY);

        var rim = i == 0 ? _renderer.BrushFor(_lid.RimStroke, 1) : null;
        container.Children.Add(ArtworkRenderer.CreateRect(
            shape.X, shape.YLocal, shape.Width, shape.Depth, shape.Rx, shape.Ry,
            _renderer.BrushFor(_lid.SideFill, 1), rim, 1, ArtMatrix.Identity));

        if (!inside && i > 0)
        {
            var white = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            container.Children.Add(ArtworkRenderer.CreateRect(
                shape.X, shape.YLocal, shape.Width, shape.Depth, shape.Rx, shape.Ry,
                white, null, 0, ArtMatrix.Identity, LidSolid.SliceLightness((double)i / SliceCount, spec.Shine)));
        }

        if (inside && i == 0)
        {
            var face = new Canvas { IsHitTestVisible = false };
            foreach (var child in _lid.InsideChildren)
            {
                if (_renderer.Render(child) is { } element)
                {
                    face.Children.Add(element);
                }
            }

            container.Children.Add(face);
        }

        // y' = hingeY + scaleY * y - h * cos(phi - theta): the container sits on the hinge line, scales about it and shifts by its height.
        var visual = Motion.EnableTranslation(container);
        visual.CenterPoint = Vector3.Zero;
        var sinPhi = Math.Sin(_phi);
        var scale = _compositor.CreateExpressionAnimation($"Vector3(1.0f, Sin({F(_phi)} - props.Theta) / {F(sinPhi)}, 1.0f)");
        scale.SetReferenceParameter("props", _props);
        visual.StartAnimation("Scale", scale);

        if (shape.H > 0)
        {
            var lift = _compositor.CreateExpressionAnimation($"Vector3(0.0f, {F(-shape.H)} * Cos({F(_phi)} - props.Theta), 0.0f)");
            lift.SetReferenceParameter("props", _props);
            visual.StartAnimation("Translation", lift);
        }

        return container;
    }

    private void Bind(UIElement element, string property, string expression)
    {
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("props", _props);
        Motion.VisualOf(element).StartAnimation(property, animation);
    }

    /// <summary>A float literal for an expression: invariant culture, always with a decimal point and an f suffix.</summary>
    private static string F(double value) => value.ToString("0.0######", CultureInfo.InvariantCulture) + "f";
}
