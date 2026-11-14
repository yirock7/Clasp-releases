using AppleDeviceHub.App.Design;
using AppleDeviceHub.Core.Artwork;
using AppleDeviceHub.Core.Ble;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// The hero drawing for the current model group. Groups with a case (earbuds) delegate to
/// <see cref="CaseArtwork"/> unchanged and only pick which buds sit inside it. Groups without a case
/// (headphones, hooks, neckband, generic) draw one original shape set: closed is 72% opacity (100% under High Contrast or Reduce Transparency), open is
/// 100%, and <see cref="PlayOpen"/> fades between them over <see cref="MotionTokens.CaseLid"/>
/// (reduced motion: straight to 100%).
/// The last requested state is remembered, so a group change applies it to the new drawing with no
/// animation and never leaves a half-open lid or a hidden drawing. Before any request the state is
/// open, as <see cref="CaseArtwork"/> is on load.
/// </summary>
public sealed partial class ModelArtwork : UserControl
{
    private const float DimmedOpacity = 0.72f;

    // The dim is a soft translucency cue; High Contrast and Reduce Transparency want the drawing fully opaque.
    internal static float ClosedOpacity => EffectsPolicy.ReduceTransparency ? 1f : DimmedOpacity;

    private ArtworkGroup _group = ArtworkGroup.Generic;
    private bool _open = true;
    private ushort? _modelId;
    private string? _colorway;
    private DeviceArtworkView? _view;
    private string? _viewKey;
    private string? _viewColorway;
    private bool _chargingCue;
    private bool _wornCue;
    private bool _budLeftOut;
    private bool _budRightOut;
    private SplitColumns? _splitColumns;
    private (double Period, double OpenAt, double CloseAt)? _caseLoop;

    public ModelArtwork()
    {
        InitializeComponent();
        Motion.VisualOf(ChargingBadge).Opacity = 0f;
        ApplyGroup(previous: null);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EffectsPolicy.Changed -= OnEffectsChanged;
        EffectsPolicy.Changed += OnEffectsChanged;
        if (SyncDeviceView()) RefreshDrawing();
        else if (_view is not null) _view.RefreshEffects();
        else ResumeLegacyLoop();
        ApplyChargingBadge(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectsPolicy.Changed -= OnEffectsChanged;
        ApplyChargingBadge(animate: false);
        if (_view is not null) _view.Stop();
        else ApplyLegacyStatic();
    }

    // ClosedOpacity and active loops must be re-applied when Windows' motion or contrast policy changes.
    private void OnEffectsChanged(object? sender, EventArgs e)
    {
        ApplyChargingBadge(animate: false);
        // High Contrast turns the per-model drawing off (and back on).
        if (SyncDeviceView()) RefreshDrawing();
        else if (_view is not null) _view.RefreshEffects();
        else if (EffectsPolicy.ReduceMotion) ApplyState();
        else if (_caseLoop is not null) ResumeLegacyLoop();
        else if (!_group.HasCase()) ApplyState();
    }

    /// <summary>
    /// The model id whose per-model drawing to show (see <see cref="ArtworkCatalog"/>). Models without one, and High Contrast,
    /// keep the group drawing. Setting the same value does nothing.
    /// </summary>
    public ushort? ModelId
    {
        get => _modelId;
        set
        {
            if (value == _modelId) return;
            _modelId = value;
            if (SyncDeviceView()) RefreshDrawing();
        }
    }

    /// <summary>Sets the model and colourway together, building the drawing once (the two setters would build it twice).</summary>
    public void SetModel(ushort? modelId, string? colorway)
    {
        if (modelId == _modelId && string.Equals(colorway, _colorway, StringComparison.Ordinal)) return;
        _modelId = modelId;
        _colorway = colorway;
        if (SyncDeviceView()) RefreshDrawing();
    }

    /// <summary>A colourway id declared by the model's drawing; unknown or null shows the default colours.</summary>
    public string? Colorway
    {
        get => _colorway;
        set
        {
            if (string.Equals(value, _colorway, StringComparison.Ordinal)) return;
            _colorway = value;
            if (SyncDeviceView()) RefreshDrawing();
        }
    }

    /// <summary>Applies current, already freshness-checked runtime cues without changing the model drawing.</summary>
    internal void SetStatusCues(bool charging, bool worn)
    {
        if (_chargingCue == charging && _wornCue == worn) return;
        _chargingCue = charging;
        _wornCue = worn;
        _view?.SetStatusCues(charging, worn);
        ApplyChargingBadge(animate: true);
    }

    /// <summary>
    /// Pulls the earbuds that are out of the case over their own battery rings and moves the case over its ring (see <see cref="SetSplitColumns"/>).
    /// Only per-model drawings with a lid and two buds can do this; others ignore it.
    /// </summary>
    internal void SetBudsOut(bool left, bool right)
    {
        if (_budLeftOut == left && _budRightOut == right) return;
        _budLeftOut = left;
        _budRightOut = right;
        _view?.SetBudsOut(left, right);
        PlaceChargingBadge();
    }

    /// <summary>
    /// Where the three columns are, as offsets from the middle of the drawing in stage units (the 200 x 150 stage): left bud, right bud, case.
    /// Null keeps the single case whatever <see cref="SetBudsOut"/> says.
    /// </summary>
    internal void SetSplitColumns(double leftX, double rightX, double caseX)
    {
        var columns = new SplitColumns(leftX, rightX, caseX);
        if (_splitColumns == columns) return;
        _splitColumns = columns;
        _view?.SetSplitColumns(columns);
        PlaceChargingBadge();
    }

    internal void ClearSplitColumns()
    {
        if (_splitColumns is null) return;
        _splitColumns = null;
        _view?.SetSplitColumns(null);
        PlaceChargingBadge();
    }

    // The charging badge belongs to the case: it rides along when the case moves over its ring.
    private void PlaceChargingBadge()
    {
        if (Motion.IsShuttingDown) return;
        var shift = _view?.CaseOffset is { } offset
            ? new System.Numerics.Vector3((float)(offset.X - 40), -14f, 0f)
            : System.Numerics.Vector3.Zero;
        Motion.StopTranslation(ChargingBadge, shift);
    }

    // Faded on the compositor over 150 ms; reduced motion, or an unloaded control, sets it at once.
    private void ApplyChargingBadge(bool animate)
    {
        if (Motion.IsShuttingDown) return;
        var target = _chargingCue ? 1f : 0f;
        if (animate && IsLoaded && !EffectsPolicy.ReduceMotion && Motion.CanAnimate(ChargingBadge))
        {
            Motion.AnimateOpacity(ChargingBadge, target, TimeSpan.FromMilliseconds(150));
            return;
        }

        var visual = Motion.VisualOf(ChargingBadge);
        visual.StopAnimation("Opacity");
        visual.Opacity = target;
    }

    /// <summary>Stops composition motion while the containing flyout is hidden.</summary>
    internal void SuspendCueAnimations()
    {
        if (_view is not null) _view.Stop();
        else ApplyLegacyStatic();
        ApplyChargingBadge(animate: false);
    }

    /// <summary>Gallery-only simulation hook; production views use fresh transport observations instead.</summary>
    internal void SetDemoStatusCues(bool connected, bool charging, bool worn)
    {
        SetStatusCues(charging, worn);
        if (connected) PlayOpen();
        else ShowClosed();
    }

    /// <summary>The model group to draw. Setting the same value does nothing.</summary>
    public ArtworkGroup Group
    {
        get => _group;
        set
        {
            if (value == _group) return;
            var previous = _group;
            _group = value;
            if (previous.HasCase() && !value.HasCase()) _caseLoop = null;
            ApplyGroup(previous);
        }
    }

    /// <summary>Final state: open (case lid up, or full opacity).</summary>
    public void ShowOpen()
    {
        _caseLoop = null;
        _open = true;
        ApplyState();
    }

    /// <summary>Closed (case lid down, or 72% opacity, 100% with reduced transparency).</summary>
    public void ShowClosed()
    {
        _caseLoop = null;
        _open = false;
        ApplyState();
    }

    /// <summary>Play the opening once. Calling again restarts from closed (no queued motion).</summary>
    public void PlayOpen()
    {
        _caseLoop = null;
        _open = true;
        if (_view is not null)
        {
            _view.PlayOpen();
            return;
        }

        if (_group.HasCase())
        {
            CaseDrawing.PlayOpen();
            return;
        }

        var visual = Motion.VisualOf(DrawingFor(_group));
        visual.StopAnimation("Opacity");
        if (EffectsPolicy.ReduceMotion)
        {
            visual.Opacity = 1f;
            return;
        }

        visual.Opacity = ClosedOpacity;
        var c = visual.Compositor;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, Motion.EaseOut(c));
        fade.Duration = MotionTokens.CaseLid;
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>Loops the case lid (see <see cref="CaseArtwork.StartLidLoop"/>). Groups without a case show open and still.</summary>
    public void StartCaseLoop(double periodSeconds, double openAt, double closeAt)
    {
        // A per-model drawing decides for itself (a drawing without a lid shows open and still).
        if (_view is not null || _group.HasCase())
        {
            _caseLoop = (periodSeconds, openAt, closeAt);
            if (_view is not null) _view.StartCaseLoop(periodSeconds, openAt, closeAt);
            else CaseDrawing.StartLidLoop(periodSeconds, openAt, closeAt);
        }
        else ShowOpen();
    }

    private void ApplyGroup(ArtworkGroup? previous)
    {
        // Stop whatever the old drawing was doing so nothing keeps running while it is hidden.
        if (_view is null && previous is { } old)
        {
            if (old.HasCase()) { if (_open) CaseDrawing.ShowOpen(); else CaseDrawing.ShowClosed(); }
            else Motion.VisualOf(DrawingFor(old)).StopAnimation("Opacity");
        }

        ApplyVisibility();
        // A hosted per-model drawing does not depend on the group: leave its state (and any running animation) alone.
        if (_view is null) ApplyState();
    }

    private void ApplyVisibility()
    {
        bool legacy = _view is null;
        CaseDrawing.Visibility = legacy && _group.HasCase() ? Visibility.Visible : Visibility.Collapsed;
        MaxArt.Visibility = Show(ArtworkGroup.MaxHeadphones);
        OverEarArt.Visibility = Show(ArtworkGroup.OverEarHeadphones);
        OnEarArt.Visibility = Show(ArtworkGroup.OnEarHeadphones);
        CordedArt.Visibility = Show(ArtworkGroup.EarHookCorded);
        NeckbandArt.Visibility = Show(ArtworkGroup.Neckband);
        GenericArt.Visibility = Show(ArtworkGroup.Generic);

        if (_group.HasCase()) CaseDrawing.SetBuds(_group);
    }

    private void RefreshDrawing()
    {
        ApplyVisibility();
        ApplyState();
    }

    /// <summary>
    /// Chooses between the per-model drawing and the legacy group drawings from the model id, colourway and High Contrast.
    /// Returns true when the hosted view changed (the caller then refreshes visibility and the remembered state).
    /// </summary>
    private bool SyncDeviceView()
    {
        var key = EffectsPolicy.HighContrast ? null : ArtworkCatalog.DrawingKeyFor(_modelId);
        var document = key is null ? null : ArtworkStore.Get(key);
        if (document is null) key = null;
        if (key == _viewKey && (key is null || string.Equals(_colorway, _viewColorway, StringComparison.Ordinal))) return false;

        if (_view is { } old)
        {
            old.Stop();
            Stage.Children.Remove(old.Root);
            _view = null;
        }
        else
        {
            // The legacy drawing is about to be hidden: stop whatever it was doing.
            if (_group.HasCase()) { if (_open) CaseDrawing.ShowOpen(); else CaseDrawing.ShowClosed(); }
            else Motion.VisualOf(DrawingFor(_group)).StopAnimation("Opacity");
        }

        // A drawing that fails to build is remembered by key, so it is not rebuilt on every change.
        _viewKey = key;
        _viewColorway = _colorway;
        if (document is not null && DeviceArtworkView.TryCreate(document, _colorway) is { } view)
        {
            view.SetStatusCues(_chargingCue, _wornCue);
            view.SetSplitColumns(_splitColumns);
            view.SetBudsOut(_budLeftOut, _budRightOut);
            _view = view;
            Stage.Children.Insert(Stage.Children.IndexOf(ChargingBadge), view.Root); // the badge stays on top
        }

        PlaceChargingBadge();

        return true;
    }

    private Visibility Show(ArtworkGroup group) => _view is null && _group == group ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Apply the remembered state to the current drawing, with no animation.</summary>
    private void ApplyState()
    {
        if (_view is not null)
        {
            if (_caseLoop is { } loop) _view.StartCaseLoop(loop.Period, loop.OpenAt, loop.CloseAt);
            else if (_open) _view.ShowOpen();
            else _view.ShowClosed();
            return;
        }

        if (_group.HasCase())
        {
            if (_caseLoop is { } loop && !EffectsPolicy.ReduceMotion) CaseDrawing.StartLidLoop(loop.Period, loop.OpenAt, loop.CloseAt);
            else if (_caseLoop is not null) CaseDrawing.ShowOpen();
            else if (_open) CaseDrawing.ShowOpen();
            else CaseDrawing.ShowClosed();
            return;
        }

        var visual = Motion.VisualOf(DrawingFor(_group));
        visual.StopAnimation("Opacity");
        visual.Opacity = _open ? 1f : ClosedOpacity;
    }

    private void ApplyLegacyStatic()
    {
        if (_group.HasCase())
        {
            if (_open) CaseDrawing.ShowOpen();
            else CaseDrawing.ShowClosed();
            return;
        }

        var visual = Motion.VisualOf(DrawingFor(_group));
        visual.StopAnimation("Opacity");
        visual.Opacity = _open ? 1f : ClosedOpacity;
    }

    private void ResumeLegacyLoop()
    {
        if (_group.HasCase() && _caseLoop is { } loop)
            CaseDrawing.StartLidLoop(loop.Period, loop.OpenAt, loop.CloseAt);
    }

    // Case groups have no separate canvas; they are never asked for one.
    private Canvas DrawingFor(ArtworkGroup group) => group switch
    {
        ArtworkGroup.MaxHeadphones => MaxArt,
        ArtworkGroup.OverEarHeadphones => OverEarArt,
        ArtworkGroup.OnEarHeadphones => OnEarArt,
        ArtworkGroup.EarHookCorded => CordedArt,
        ArtworkGroup.Neckband => NeckbandArt,
        _ => GenericArt,
    };
}
