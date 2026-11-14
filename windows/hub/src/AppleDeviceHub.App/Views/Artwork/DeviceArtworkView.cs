// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Numerics;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Artwork;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WM = Microsoft.UI.Xaml.Media;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// One per-model drawing, built from an <see cref="ArtworkDocument"/> and scaled uniformly and centred into the same
/// 200 x 150 stage <see cref="ModelArtwork"/> uses. Drawings with a lid open and close it on the compositor; drawings
/// without one fade between full and dimmed opacity like the older no-case drawings. The last requested state is
/// remembered and re-applied when the view is loaded again.
/// </summary>
internal sealed class DeviceArtworkView
{
    public const double StageWidth = 200;
    public const double StageHeight = 150;

    /// <summary>The time the lid takes to swing open in <see cref="PlayOpen"/>.</summary>
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(480);

    private enum Mode { Open, Closed, Loop }

    private readonly Canvas _stage;
    private readonly Canvas _caseHost;
    private readonly FrameworkElement? _light;
    private readonly List<Bud> _buds;
    private readonly double _baseScale;
    private readonly double _centreX;
    private readonly double _centreY;
    private LidVisual? _lid;
    private Mode _mode = Mode.Open;
    private (double Period, double OpenAt, double CloseAt) _loop;
    private bool _charging;
    private bool _worn;
    private bool _budsOut;
    private readonly bool _splittable;

    // Worn: each bud lifts out of the open case and fades away (~16 drawing units, as the case is about 300 wide).
    private const float BudLiftUnits = 16f;
    private static readonly TimeSpan BudOutDuration = TimeSpan.FromMilliseconds(320);

    // Buds out (split layout): the case shrinks and moves over its ring and each bud stands over its own ring.
    private const double CaseSplitScale = 0.8;
    private const double BudSplitScale = 1.6;
    private const double SplitBudCentreY = 12;   // stage units below the stage centre, so the bud sits level with the case body
    private static readonly TimeSpan SplitDuration = TimeSpan.FromMilliseconds(520);
    private static readonly TimeSpan SplitLidCloseDelay = TimeSpan.FromMilliseconds(220);

    /// <summary>One earbud: its drawing part, the host that carries the lid's fade, and where it sits in the drawing.</summary>
    private sealed class Bud(FrameworkElement part, bool isLeft, int index, ArtBounds box)
    {
        public FrameworkElement Part { get; } = part;
        public bool IsLeft { get; } = isLeft;
        public int Index { get; } = index;
        public ArtBounds Box { get; } = box;
        public bool AppliedOut { get; set; }
    }

    private bool _outLeft;
    private bool _outRight;
    private SplitColumns? _columns;
    private bool _appliedCase;
    private SplitColumns? _appliedColumns;

    private DeviceArtworkView(ArtworkDocument document, ArtworkPalette palette)
    {
        // The lid handler needs the renderer itself (to render the inside face), hence the late-bound variable.
        ArtworkRenderer? self = null;
        self = new ArtworkRenderer(document, palette, node =>
        {
            _lid = new LidVisual(node, self!);
            return _lid.InsideHost;
        });

        var scene = new Canvas { Width = document.Width, Height = document.Height, IsHitTestVisible = false };
        var deferred = new List<UIElement>();
        foreach (var node in document.Nodes)
        {
            var element = self.Render(node);
            if (element is null)
            {
                continue;
            }

            // The light stays on top of the lid's outer shell, which is added last.
            if (node is ArtGroup { Part: ArtPart.Light })
            {
                deferred.Add(element);
            }
            else
            {
                scene.Children.Add(element);
            }
        }

        // Rendering populates Parts, so look these up only after the document nodes have been visited.
        // The buds group's opacity belongs to the lid expression, so the cue moves the two bud parts inside it.
        _buds = [];
        self.Parts.TryGetValue(ArtPart.BudLeft, out var budLeft);
        self.Parts.TryGetValue(ArtPart.BudRight, out var budRight);
        var boxLeft = ArtworkBounds.OfPart(document, ArtPart.BudLeft);
        var boxRight = ArtworkBounds.OfPart(document, ArtPart.BudRight);

        if (_lid is not null)
        {
            scene.Children.Add(_lid.OutsideHost);
            if (self.Parts.TryGetValue(ArtPart.Buds, out var budsPart))
            {
                var buds = budsPart as Canvas;
                // Each bud gets its own host (still inside the buds group, so behind the front lip) that carries the lid fade,
                // so a bud that has left the case can stay visible while the lid closes.
                if (buds is not null && budLeft is not null && budRight is not null && boxLeft is not null && boxRight is not null
                    && buds.Children.IndexOf(budLeft) >= 0 && buds.Children.IndexOf(budRight) >= 0)
                {
                    var hosts = new List<UIElement>(2);
                    foreach (var part in new[] { budLeft, budRight })
                    {
                        var index = buds.Children.IndexOf(part);
                        buds.Children.RemoveAt(index);
                        var host = new Canvas { IsHitTestVisible = false };
                        host.Children.Add(part);
                        buds.Children.Insert(index, host);
                        hosts.Add(host);
                    }

                    // Hosts are in bud order: left (index 0) then right (index 1).
                    _lid.BindBuds(hosts);
                    _buds.Add(new Bud(budLeft, true, 0, boxLeft.Value));
                    _buds.Add(new Bud(budRight, false, 1, boxRight.Value));
                }
                else
                {
                    _lid.BindBuds(budsPart);
                }
            }
        }

        foreach (var element in deferred)
        {
            scene.Children.Add(element);
        }

        if (self.Parts.TryGetValue(ArtPart.Light, out var lightPart)) _light = lightPart;
        if (_buds.Count == 0)
        {
            // No lid, or parts without hosts: the worn cue still moves whichever bud parts exist, but they cannot split out.
            if (budLeft is not null) _buds.Add(new Bud(budLeft, true, 0, default));
            if (budRight is not null) _buds.Add(new Bud(budRight, false, 1, default));
        }
        else
        {
            _splittable = true;
        }

        // Fit to what is actually drawn (shapes, the lid at its widest and most open, the core of the shadow), not to the whole
        // viewBox, so the case fills the stage about as the original hand-drawn one did.
        var content = ArtworkBounds.ContentOf(document);
        var fit = StageFit.Of(content); // shared with the pairing marks (PairingAnchors), so they land on the same spot
        var scale = fit.Scale;
        _baseScale = scale;
        _centreX = content.CentreX;
        _centreY = content.CentreY;
        scene.RenderTransform = new WM.MatrixTransform
        {
            Matrix = new WM.Matrix(scale, 0, 0, scale, (StageWidth / 2) - (scale * _centreX), (StageHeight / 2) - (scale * _centreY)),
        };

        // The whole case drawing sits in one host so it can shrink and move over its ring without touching the scene's own transform.
        _caseHost = new Canvas { Width = StageWidth, Height = StageHeight, IsHitTestVisible = false };
        _caseHost.Children.Add(scene);
        _stage = new Canvas { Width = StageWidth, Height = StageHeight, IsHitTestVisible = false };
        _stage.Children.Add(_caseHost);
        _stage.Loaded += (_, _) => Reapply();
        _stage.Unloaded += (_, _) => Stop();
    }

    /// <summary>The element to put in the page.</summary>
    public FrameworkElement Root => _stage;

    public bool HasLid => _lid is not null;

    /// <summary>Updates the part-level cues from freshness-checked observations.</summary>
    public void SetStatusCues(bool charging, bool worn)
    {
        if (_charging == charging && _worn == worn) return;
        _charging = charging;
        _worn = worn;
        if (_stage.IsLoaded) ApplyStatusCues(animate: true);
    }

    /// <summary>True when the drawing has a lid and two separate buds, so the buds can be pulled out of the case (<see cref="SetBudsOut"/>).</summary>
    public bool CanSplit => _splittable;

    /// <summary>The case's column offset and scale while split (for things that ride on the case, such as the charging badge); null otherwise.</summary>
    public (double X, double Scale)? CaseOffset => SplitActive ? (_columns!.Value.CaseX, CaseSplitScale) : null;

    private bool SplitActive => _splittable && _columns is not null && (_outLeft || _outRight);

    private bool BothOut => SplitActive && _outLeft && _outRight;

    /// <summary>
    /// Sets which buds are out of the case. With <see cref="SetSplitColumns"/> set, those buds glide to their columns and the case moves to its own.
    /// Reduced motion, a closed lid or a view that is not on screen jump straight there.
    /// </summary>
    public void SetBudsOut(bool left, bool right)
    {
        if (_outLeft == left && _outRight == right) return;
        _outLeft = left;
        _outRight = right;
        if (_stage.IsLoaded) ApplySplit(animate: true);
    }

    /// <summary>Where the three columns sit, or null to keep a single case (no split).</summary>
    public void SetSplitColumns(SplitColumns? columns)
    {
        if (_columns == columns) return;
        _columns = columns;
        if (_stage.IsLoaded) ApplySplit(animate: false);
    }

    /// <summary>Stops and reapplies the current state when system motion preferences change.</summary>
    public void RefreshEffects()
    {
        if (Motion.IsShuttingDown) return;
        Stop();
        if (_mode == Mode.Loop && _lid is not null && !EffectsPolicy.ReduceMotion && Motion.CanAnimate(_stage))
        {
            StartCaseLoop(_loop.Period, _loop.OpenAt, _loop.CloseAt);
            return;
        }

        ApplyStatic();
    }

    /// <summary>Builds the view, or returns null when the drawing cannot be turned into shapes (for example an unknown colour token).</summary>
    public static DeviceArtworkView? TryCreate(ArtworkDocument document, string? colorwayId)
    {
        try
        {
            return new DeviceArtworkView(document, document.PaletteFor(colorwayId));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Artwork view could not be built: {ex.Message}");
            return null;
        }
    }

    /// <summary>Final state: lid open, or full opacity.</summary>
    public void ShowOpen()
    {
        _mode = Mode.Open;
        ApplyStatic();
    }

    /// <summary>Closed: lid down, or dimmed (see <see cref="ModelArtwork.ClosedOpacity"/>).</summary>
    public void ShowClosed()
    {
        _mode = Mode.Closed;
        ApplyStatic();
    }

    /// <summary>Plays the opening once from closed. Reduced motion jumps straight to open.</summary>
    public void PlayOpen()
    {
        _mode = Mode.Open;
        if (Motion.IsShuttingDown)
        {
            return;
        }

        if (EffectsPolicy.ReduceMotion || !Motion.CanAnimate(_stage))
        {
            ApplyStatic();
            return;
        }

        if (BothOut)
        {
            // Both buds are out of the case, so there is nothing to open it for: it stays closed over its ring.
            ApplyStatic();
            return;
        }

        if (_lid is { } lid)
        {
            lid.SetAngle(0);
            lid.AnimateTo(lid.OpenDegrees, OpenDuration);
            ApplyStatusCues(animate: true);
            return;
        }

        var visual = Motion.VisualOf(_stage);
        visual.StopAnimation("Opacity");
        visual.Opacity = ModelArtwork.ClosedOpacity;
        var c = visual.Compositor;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, Motion.EaseOut(c));
        fade.Duration = MotionTokens.CaseLid;
        visual.StartAnimation("Opacity", fade);
        ApplyStatusCues(animate: true);
    }

    /// <summary>Loops closed, open, closed (times in seconds into each loop). Drawings without a lid, and reduced motion, show open and still.</summary>
    public void StartCaseLoop(double periodSeconds, double openAt, double closeAt)
    {
        _loop = (periodSeconds, openAt, closeAt);
        if (_lid is null)
        {
            ShowOpen();
            return;
        }

        _mode = Mode.Loop;
        if (EffectsPolicy.ReduceMotion || !Motion.CanAnimate(_stage))
        {
            ApplyStatic();
            ApplyStatusCues(animate: false);
            return;
        }

        _lid.SetAngle(0);
        _lid.StartLoop(periodSeconds, openAt, closeAt);
        ApplyStatusCues(animate: true);
    }

    /// <summary>Stops every animation where it is.</summary>
    public void Stop()
    {
        if (Motion.IsShuttingDown)
        {
            return;
        }

        _lid?.Stop();
        Motion.VisualOf(_stage).StopAnimation("Opacity");
        if (_light is not null) Motion.VisualOf(_light).StopAnimation("Opacity");
        SnapBuds();
    }

    private void Reapply()
    {
        if (_mode == Mode.Loop && _lid is not null && !EffectsPolicy.ReduceMotion)
        {
            StartCaseLoop(_loop.Period, _loop.OpenAt, _loop.CloseAt);
        }
        else
        {
            ApplyStatic();
        }
    }

    private void ApplyStatic()
    {
        if (Motion.IsShuttingDown)
        {
            return;
        }

        // With both buds out of the case there is nothing left inside it, so the lid stays shut over its ring.
        var open = _mode != Mode.Closed && !BothOut;
        if (_lid is { } lid)
        {
            lid.SetAngle(open ? lid.OpenDegrees : 0);
        }
        else
        {
            var visual = Motion.VisualOf(_stage);
            visual.StopAnimation("Opacity");
            visual.Opacity = open ? 1f : ModelArtwork.ClosedOpacity;
        }

        ApplyStatusCues(animate: !EffectsPolicy.ReduceMotion, force: true);
    }

    // force: put every bud and the case at their end state first (even when nothing changed), then animate if the worn cue changed.
    private void ApplyStatusCues(bool animate, bool force = false)
    {
        ApplyChargingCue(animate);
        ApplyWornCue(animate, force);
    }

    private void ApplyChargingCue(bool animate)
    {
        if (_light is null || Motion.IsShuttingDown) return;
        var visual = Motion.VisualOf(_light);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1f;
        if (!_charging || !animate || EffectsPolicy.ReduceMotion || !_stage.IsLoaded || !Motion.CanAnimate(_light)) return;

        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0f, 0.56f);
        pulse.InsertKeyFrame(0.5f, 1f);
        pulse.InsertKeyFrame(1f, 0.56f);
        pulse.Duration = TimeSpan.FromMilliseconds(1200);
        pulse.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", pulse);
    }

    // The worn cue (buds lift out and fade) is for drawings that keep one case; once the buds split out they are drawn, not faded.
    private bool BudsShouldBeOut => _worn && !SplitActive && (_lid is null || _mode == Mode.Open);

    private bool IsOut(Bud bud) => bud.IsLeft ? _outLeft : _outRight;

    /// <summary>
    /// Stops any bud and case motion and puts everything at its end state: split buds at their columns and the case over its ring,
    /// otherwise buds out of the case when worn and at rest when not.
    /// </summary>
    private void SnapBuds()
    {
        if (_buds.Count == 0 || Motion.IsShuttingDown) return;
        var split = SplitActive;
        _budsOut = BudsShouldBeOut;
        SnapCase(split);
        foreach (var bud in _buds)
        {
            var isOut = split && IsOut(bud);
            bud.AppliedOut = isOut;
            var visual = Motion.VisualOf(bud.Part);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            Motion.StopTranslation(bud.Part);
            var rest = (float)bud.Part.Opacity;
            if (isOut)
            {
                var (shift, scale) = SplitTargetOf(bud);
                visual.CenterPoint = new Vector3((float)bud.Box.CentreX, (float)bud.Box.CentreY, 0f);
                visual.Scale = new Vector3(scale, scale, 1f);
                visual.Opacity = rest;
                Motion.SetTranslation(bud.Part, shift);
                _lid?.SetBudFree(bud.Index, true);
            }
            else
            {
                visual.Scale = Vector3.One;
                visual.Opacity = _budsOut ? 0f : rest;
                Motion.SetTranslation(bud.Part, _budsOut ? new Vector3(0f, -BudLiftUnits, 0f) : Vector3.Zero);
                _lid?.SetBudFree(bud.Index, false);
            }
        }
    }

    private void SnapCase(bool split)
    {
        var visual = Motion.VisualOf(_caseHost);
        visual.StopAnimation("Scale");
        visual.CenterPoint = new Vector3((float)(StageWidth / 2), (float)(StageHeight / 2), 0f);
        var scale = split ? (float)CaseSplitScale : 1f;
        visual.Scale = new Vector3(scale, scale, 1f);
        Motion.StopTranslation(_caseHost, split ? new Vector3((float)_columns!.Value.CaseX, 0f, 0f) : Vector3.Zero);
        _appliedCase = split;
        _appliedColumns = split ? _columns : null;
    }

    /// <summary>
    /// Where a split bud sits: the Translation (in drawing units, inside the scene) that carries its centre to its column, and the
    /// extra scale it grows by. The scene is scaled about the stage centre by the case scale and then moved to the case column.
    /// </summary>
    private (Vector3 Shift, float Scale) SplitTargetOf(Bud bud)
    {
        var columns = _columns!.Value;
        var k = CaseSplitScale * _baseScale;
        var targetX = bud.IsLeft ? columns.LeftX : columns.RightX;
        var x = (targetX - (k * (bud.Box.CentreX - _centreX)) - columns.CaseX) / k;
        var y = (SplitBudCentreY - (k * (bud.Box.CentreY - _centreY))) / k;
        return (new Vector3((float)x, (float)y, 0f), (float)BudSplitScale);
    }

    /// <summary>Moves the buds and the case to the requested split state, animating when that makes sense, otherwise jumping.</summary>
    private void ApplySplit(bool animate)
    {
        if (!_splittable || Motion.IsShuttingDown) return;
        var split = SplitActive;
        var sameColumns = !split || !_appliedCase || _appliedColumns == _columns;
        var motion = animate && sameColumns && _stage.IsLoaded && !EffectsPolicy.ReduceMotion && Motion.CanAnimate(_stage);
        var lidOpen = _mode == Mode.Open;

        var caseChanged = split != _appliedCase || (split && !sameColumns);
        var anyBudChanged = false;
        foreach (var bud in _buds)
        {
            if ((split && IsOut(bud)) != bud.AppliedOut) anyBudChanged = true;
        }

        if ((!caseChanged && !anyBudChanged) || !motion)
        {
            // Nothing moved (only the layout changed, say a resize), or no motion wanted: jump to the end state.
            if (_lid is { } still && _mode != Mode.Loop) still.SetAngle(_mode != Mode.Closed && !BothOut ? still.OpenDegrees : 0);
            SnapBuds();
            return;
        }

        var wasBothOut = _buds.Count == 2 && _buds[0].AppliedOut && _buds[1].AppliedOut;

        var caseVisual = Motion.VisualOf(_caseHost);
        var compositor = caseVisual.Compositor;
        var ease = Motion.EaseOut(compositor);
        var scale = split ? (float)CaseSplitScale : 1f;
        var grow = compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), ease);
        grow.Duration = SplitDuration;
        caseVisual.StartAnimation("Scale", grow);
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, split ? new Vector3((float)_columns!.Value.CaseX, 0f, 0f) : Vector3.Zero, ease);
        slide.Duration = SplitDuration;
        Motion.StartTranslation(_caseHost, slide);
        _appliedCase = split;
        _appliedColumns = split ? _columns : null;

        foreach (var bud in _buds)
        {
            var wantOut = split && IsOut(bud);
            if (wantOut == bud.AppliedOut) continue;
            bud.AppliedOut = wantOut;
            AnimateBud(bud, wantOut, lidOpen);
        }

        // The lid follows: it closes once both buds are clear of it and opens again as the first bud heads back.
        if (lidOpen && _lid is { } lid)
        {
            if (BothOut && !wasBothOut) lid.AnimateTo(0, MotionTokens.CaseLid, SplitLidCloseDelay);
            else if (!BothOut && wasBothOut) lid.AnimateTo(lid.OpenDegrees, OpenDuration);
        }
    }

    private void AnimateBud(Bud bud, bool wantOut, bool lidOpen)
    {
        var visual = Motion.VisualOf(bud.Part);
        var compositor = visual.Compositor;
        var ease = Motion.EaseOut(compositor);
        var rest = (float)bud.Part.Opacity;
        var lift = new Vector3(0f, -(float)(bud.Box.Height * 0.5), 0f);
        var (shift, scale) = SplitTargetOf(bud);
        visual.CenterPoint = new Vector3((float)bud.Box.CentreX, (float)bud.Box.CentreY, 0f);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = rest;

        if (!lidOpen)
        {
            // The lid is shut, so the bud has no case to leave or enter: it appears at, or vanishes from, its column.
            Motion.StopTranslation(bud.Part, wantOut ? shift : Vector3.Zero);
            visual.Scale = wantOut ? new Vector3(scale, scale, 1f) : Vector3.One;
            _lid?.SetBudFree(bud.Index, wantOut);
            if (wantOut)
            {
                var appear = compositor.CreateScalarKeyFrameAnimation();
                appear.InsertKeyFrame(0f, 0f);
                appear.InsertKeyFrame(1f, rest, ease);
                appear.Duration = BudOutDuration;
                visual.StartAnimation("Opacity", appear);
            }

            return;
        }

        var travel = compositor.CreateVector3KeyFrameAnimation();
        var size = compositor.CreateVector3KeyFrameAnimation();
        if (wantOut)
        {
            // Lift straight up out of the cavity, then glide to the column while growing.
            _lid?.SetBudFree(bud.Index, true);
            travel.InsertKeyFrame(0.38f, lift, ease);
            travel.InsertKeyFrame(1f, shift, ease);
            size.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), ease);
        }
        else
        {
            // Glide back above the case, then drop in; the lid fade takes the bud over once it is seated.
            _lid?.ReleaseBudFreeAfter(bud.Index, SplitDuration);
            travel.InsertKeyFrame(0.62f, lift, ease);
            travel.InsertKeyFrame(1f, Vector3.Zero, ease);
            size.InsertKeyFrame(1f, Vector3.One, ease);
        }

        travel.Duration = SplitDuration;
        size.Duration = SplitDuration;
        visual.StartAnimation("Scale", size);
        Motion.StartTranslation(bud.Part, travel);
    }

    private void ApplyWornCue(bool animate, bool force)
    {
        if (_buds.Count == 0 || Motion.IsShuttingDown) return;
        var wasOut = _budsOut;
        if (!force && wasOut == BudsShouldBeOut) return;
        SnapBuds();
        if (wasOut == _budsOut || !animate || EffectsPolicy.ReduceMotion || !_stage.IsLoaded) return;

        // Animate from the opposite end state to the new one, on the compositor.
        foreach (var budItem in _buds)
        {
            var bud = budItem.Part;
            if (budItem.AppliedOut || !Motion.CanAnimate(bud)) continue;
            var visual = Motion.VisualOf(bud);
            var compositor = visual.Compositor;
            var ease = Motion.EaseOut(compositor);
            var restOpacity = (float)bud.Opacity;
            var lifted = new Vector3(0f, -BudLiftUnits, 0f);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, _budsOut ? restOpacity : 0f);
            fade.InsertKeyFrame(1f, _budsOut ? 0f : restOpacity, ease);
            fade.Duration = BudOutDuration;
            visual.StartAnimation("Opacity", fade);

            var lift = compositor.CreateVector3KeyFrameAnimation();
            lift.InsertKeyFrame(0f, _budsOut ? Vector3.Zero : lifted);
            lift.InsertKeyFrame(1f, _budsOut ? lifted : Vector3.Zero, ease);
            lift.Duration = BudOutDuration;
            Motion.StartTranslation(bud, lift);
        }
    }
}

/// <summary>Where the three hero columns sit, in stage units from the stage centre: the left bud, the right bud and the case.</summary>
internal readonly record struct SplitColumns(double LeftX, double RightX, double CaseX);
