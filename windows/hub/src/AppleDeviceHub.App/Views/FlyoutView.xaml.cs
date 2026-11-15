using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Artwork;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Resources;
using AppleDeviceHub.Core.Updates;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// Tray flyout content. Host contract: set <see cref="ViewModel"/>, size the window to
/// 380 DIP x DesiredSize (clamped; the content scrolls), listen for the request events,
/// and call <see cref="OnShown"/> after showing. Escape anywhere raises
/// <see cref="DismissRequested"/>.
/// </summary>
public sealed partial class FlyoutView : UserControl
{
    private static readonly TimeSpan HaloPeriod = TimeSpan.FromMilliseconds(1400);

    private HubViewModel? _viewModel;
    private HubViewModel? _subscribed;
    private PlaybackViewModel? _playback;
    private PlaybackViewModel? _subscribedPlayback;
    private AudioOutputViewModel? _audioOutput;
    private AudioOutputViewModel? _subscribedAudioOutput;
    private CompanionConnectionViewModel? _connection;
    private CompanionConnectionViewModel? _subscribedConnection;
    private UpdateViewModel? _updates;
    private HubSettings? _settings;
    private bool _haloRunning;
    private HeroState _hero;
    private FocusState _restoreFocus = FocusState.Unfocused;
    private bool _isShown;
    private long _shownGeneration;
    private bool _nearbyDetailsExpanded;
    private bool _projectingOutputVolume;
    private bool _outputPointerActive;
    private bool _outputKeyboardActive;
    private bool _outputInteractionInvalidated;
    private string? _outputCapturedId;
    private long _outputCapturedGeneration;
    private long _outputCapturedViewGeneration;
    private double _pendingOutputValue;
    private DispatcherQueueTimer? _outputVolumeTimer;
    private DispatcherQueueTimer? _artworkCueTimer;
    private bool _playRingsWhenLoaded;
    private readonly List<BatteryItemViewModel> _watchedBatteries = new();

    public FlyoutView()
    {
        InitializeComponent();
        _artworkCueTimer = DispatcherQueue.CreateTimer();
        _artworkCueTimer.IsRepeating = false;
        _artworkCueTimer.Tick += OnArtworkCueFreshnessTick;
        // handledEventsToo: Escape dismisses even when a child control handled the key.
        AddHandler(KeyDownEvent, new KeyEventHandler(OnAnyKeyDown), true);
        // Slider thumb input is often handled inside the template; observe it at the slider boundary.
        OutputVolumeSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnOutputVolumePointerPressed), true);
        OutputVolumeSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnOutputVolumePointerReleased), true);
        OutputVolumeSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnOutputVolumePointerCanceled), true);
        OutputVolumeSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnOutputVolumePointerCaptureLost), true);
        OutputVolumeSlider.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnOutputVolumeKeyDown), true);
        OutputVolumeSlider.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnOutputVolumeKeyUp), true);
        ModeSelector.SelectionRequested += OnModeRequested;
        ModeSelector.IsOptionSupported = option => _viewModel?.IsModeSupported(option.Mode) ?? false;
        Motion.VisualOf(Halo).Opacity = 0f;
        // The scroller hands its content unlimited height, so this is the natural height.
        ContentStack.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) >= 0.5) ContentHeightChanged?.Invoke(this, EventArgs.Empty);
        };
        Loaded += (_, _) =>
        {
            if (_isShown)
            {
                Attach();
                AttachPlayback();
                AttachAudioOutput();
            }
            ModeSelector.RefreshAvailability();
            SyncHero(replay: false);
            if (_playRingsWhenLoaded)
            {
                _playRingsWhenLoaded = false;
                PlayCaseOpen();
            }
        };
        Unloaded += (_, _) =>
        {
            OnHidden(_shownGeneration);
            StopHalo();
        };
    }

    public HubViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value)) return;
            Detach();
            _viewModel = value;
            if (IsLoaded && _isShown) Attach();
            // Required after load; harmless before it (x:Bind then simply starts initialised),
            // and it also covers a view model swapped while the view was unloaded.
            Bindings.Update();
            ModeSelector.RefreshAvailability();
            if (IsLoaded) SyncHero(replay: false);
        }
    }

    public PlaybackViewModel? Playback
    {
        get => _playback;
        set
        {
            if (ReferenceEquals(_playback, value)) return;
            DetachPlayback();
            _playback = value;
            if (_isShown) AttachPlayback();
            Bindings.Update();
        }
    }

    public AudioOutputViewModel? AudioOutput
    {
        get => _audioOutput;
        set
        {
            if (ReferenceEquals(_audioOutput, value)) return;
            DetachAudioOutput();
            _audioOutput = value;
            if (_isShown) AttachAudioOutput();
            Bindings.Update();
            ProjectOutputVolume();
        }
    }

    public CompanionConnectionViewModel? Connection
    {
        get => _connection;
        set
        {
            if (ReferenceEquals(_connection, value)) return;
            DetachConnection();
            _connection = value;
            if (_isShown) AttachConnection();
            Bindings.Update();
            if (IsLoaded) SyncHero(replay: false);
        }
    }

    /// <summary>Update checks: the one-time question and the "update available" banner.</summary>
    public UpdateViewModel? Updates
    {
        get => _updates;
        set
        {
            if (ReferenceEquals(_updates, value)) return;
            if (_updates is not null) _updates.PropertyChanged -= OnUpdatesPropertyChanged;
            _updates = value;
            if (_updates is not null) _updates.PropertyChanged += OnUpdatesPropertyChanged;
            Bindings.Update();
        }
    }

    /// <summary>User settings for nearby wording and saved per-device artwork choices.</summary>
    public HubSettings? Settings
    {
        get => _settings;
        set
        {
            if (ReferenceEquals(_settings, value)) return;
            if (_settings is not null) _settings.PropertyChanged -= OnSettingsPropertyChanged;
            _settings = value;
            if (_settings is not null) _settings.PropertyChanged += OnSettingsPropertyChanged;
            Bindings.Update();
            if (IsLoaded) SyncHero(replay: false);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HubSettings.ArtworkColorways))
        {
            void Refresh()
            {
                if (ReferenceEquals(sender, _settings)) SyncHero(replay: false);
            }
            if (DispatcherQueue.HasThreadAccess) Refresh();
            else DispatcherQueue.TryEnqueue(Refresh);
            return;
        }
        if (e.PropertyName != nameof(HubSettings.ObserveNearbyAirPods)) return;
        void Update()
        {
            if (!ReferenceEquals(sender, _settings)) return;
            Bindings.Update();
            ClearHiddenBroadcastPick();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    public bool ShowUpdateConsent => Updates?.ShowConsentPrompt == true;
    public bool ShowUpdateBanner => Updates is { } updates && (updates.ShowUpdateBanner || updates.Phase is UpdatePhase.Downloading or UpdatePhase.Installing);
    public string UpdateBannerText => Updates?.StatusText ?? "";
    public string UpdateInstallText => Updates?.InstallButtonText ?? Strings.Flyout_Update_InstallFallback;
    public bool CanInstallUpdate => Updates?.CanInstall == true;
    public bool UpdateIsPending => Updates?.Phase is UpdatePhase.Downloading or UpdatePhase.Installing;

    private void OnUpdatesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Update()
        {
            if (!ReferenceEquals(sender, _updates)) return;
            Bindings.Update();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void OnUpdateConsentYesClick(object sender, RoutedEventArgs e) => Updates?.AcceptAutomaticChecks();

    private void OnUpdateConsentNoClick(object sender, RoutedEventArgs e) => Updates?.DeclineAutomaticChecks();

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (Updates is not { } updates) return;
        try { await updates.InstallAsync(); }
        catch (Exception ex) { LocalLog.Write($"Update install failed ({ex.GetType().Name})."); }
    }

    public bool HasPlaybackControls => Playback is not null;
    public string PlaybackPlayerNameText => Playback?.PlayerName ?? "";
    public string PlaybackTitleText => !string.IsNullOrWhiteSpace(Playback?.Title)
        ? Playback.Title!
        : Playback?.HasPlayer == true ? Strings.Flyout_Playback_NoTrackDetails : Strings.Flyout_Playback_NoActivePlayer;
    public string PlaybackStateText => Playback?.Current.Playback switch
    {
        MediaPlayback.Playing => Strings.Flyout_Playback_StatePlaying,
        MediaPlayback.Paused => Strings.Flyout_Playback_StatePaused,
        MediaPlayback.Stopped => Strings.Flyout_Playback_StateStopped,
        _ => Playback?.HasPlayer == true ? Strings.Flyout_Playback_StateReady : Strings.Flyout_Playback_StateUnavailable,
    };
    // The routine "Controlling {player}..." line is hidden (the player name is already shown); every
    // failure, busy or no-player status is not routine and still appears.
    public string PlaybackStatusText => Playback is { StatusIsRoutine: false } playback ? playback.Status : "";
    public bool PlaybackCanNext => Playback?.CanNext == true;
    public bool PlaybackCanPrevious => Playback?.CanPrevious == true;
    public bool PlaybackIsBusy => Playback?.IsBusy == true;
    public bool PlaybackCenterCanExecute => PlaybackCenterAction is not null;
    public string PlaybackCenterActionName => PlaybackCenterAction switch
    {
        PlaybackAction.Play => Strings.Flyout_Playback_ActionPlay,
        PlaybackAction.Pause => Strings.Flyout_Playback_ActionPause,
        _ => Strings.Flyout_Playback_ActionUnavailable,
    };
    public string PlaybackCenterGlyph => Playback?.Current.Playback == MediaPlayback.Playing ? "\uE769" : "\uE768";
    private PlaybackAction? PlaybackCenterAction => Playback is { } playback
        ? playback.Current.Playback == MediaPlayback.Playing
            ? playback.CanPause ? PlaybackAction.Pause : null
            : playback.CanPlay ? PlaybackAction.Play : null
        : null;

    public string HeaderTitleText => _viewModel?.IsDemo == true
        ? _viewModel.DeviceName
        : Connection?.SelectedDeviceName ?? "AirPods";

    public bool ShowPairedConnection => _viewModel?.IsDemo != true && Connection is not null;
    public bool HeaderIsConnected => _viewModel?.IsDemo == true ? _viewModel.IsConnected : Connection?.IsConnected == true;
    public bool HeaderIsConnecting => _viewModel?.IsDemo == true ? _viewModel.IsConnecting : Connection?.IsConnecting == true;

    public string PairedConnectionStatusText => Connection switch
    {
        { IsConnected: true, IsCurrentOutput: true } => Strings.Flyout_Connection_ConnectedCurrentOutput,
        { IsConnected: true } => Strings.Flyout_Connection_ConnectedSoundElsewhere,
        { HasSelectedDevice: true } connection => connection.StatusText,
        { } connection => connection.StatusText,
        _ => Strings.Flyout_Connection_CantCheck,
    };

    public string PairedConnectionDetailText => Connection switch
    {
        { DetailText: not null } connection => connection.DetailText,
        { IsConnected: true } => "",
        _ => Strings.Flyout_Connection_ChooseInSettings,
    };
    public bool CanConnectPairedDevice => Connection is { } connection && (connection.CanConnect || connection.CanDisconnect);
    public bool PairedConnectIsPending => Connection is { } pending && (pending.IsConnecting || pending.IsDisconnecting);
    public BatteryItemViewModel? WindowsBatteryItem => Connection?.WindowsBattery;
    public bool ShowNearbyRings => _viewModel is { } vm && (vm.Left.IsKnown || vm.Right.IsKnown || vm.Case.IsKnown || vm.Single.IsKnown);
    public bool ShowWindowsRing => !ShowNearbyRings && Connection?.WindowsBattery.IsKnown == true;

    // Single-battery models (headphones) get one ring (the artwork is always shown and follows the model group).
    // Until a reading exists the Windows ring may still show; with neither, the one ring is drawn unknown rather than three phantom earbud and case rings.
    private bool IsSingleBatteryDevice => _viewModel?.IsSingleBattery == true;
    public bool ShowSingleRing => IsSingleBatteryDevice && !ShowWindowsRing;
    public bool ShowThreeRings => !ShowWindowsRing && !IsSingleBatteryDevice;
    public bool ShowCaseRing => _viewModel?.HasCaseBattery != false;
    public GridLength CaseColumnWidth => ShowCaseRing ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    private bool ShowBatteryHint => !ShowNearbyRings && !ShowWindowsRing && !string.IsNullOrEmpty(Connection?.WindowsBatteryHint);

    /// <summary>Nearby detection is switched on in Settings and this build listens to broadcasts.</summary>
    private bool NearbyDetectionOn => _viewModel?.IsBroadcastObservation == true && _settings?.ObserveNearbyAirPods == true;

    /// <summary>One short line under the rings; the longer caveat lives in <see cref="BatterySourceHelp"/>.</summary>
    public string BatterySourceText
    {
        get
        {
            if (ShowNearbyRings)
                return _viewModel!.NearbyIdentityConfidence.IsLikely() ? Strings.Flyout_BatterySource_NearbyEstimate : Strings.Flyout_BatterySource_NearbyEstimateMaybeOther;
            if (ShowWindowsRing) return Strings.Flyout_BatterySource_ReportedByWindows;
            if (_viewModel?.IsBroadcastObservation == true)
            {
                if (NearbyDetectionOn)
                    return _viewModel.UnavailableReason is { Length: > 0 } unavailable ? unavailable : string.Format(CultureInfo.CurrentCulture, Strings.Flyout_BatterySource_LookingFor, _viewModel.DeviceNoun);
                return ShowBatteryHint ? Connection!.WindowsBatteryHint : Strings.Flyout_BatterySource_NearbyDetectionOff;
            }
            return ShowBatteryHint ? Connection!.WindowsBatteryHint : Strings.Flyout_BatterySource_AppearsWhenReported;
        }
    }

    public string BatterySourceHelp => ShowNearbyRings
            ? OtherDeviceHelpText
        : ShowBatteryHint && !NearbyDetectionOn
            ? Strings.Flyout_BatterySource_HelpEnableHandsfree
        : "";
    private string OtherDeviceHelpText => _viewModel?.DeviceNoun is { } noun && noun != "AirPods"
        ? string.Format(CultureInfo.CurrentCulture, Strings.Flyout_BatterySource_HelpNearbyOtherSet, noun)
        : Strings.Flyout_BatterySource_HelpNearbyOtherPair;
    public bool ShowPairNewAirPods => Connection?.NeedsPairing == true;

    /// <summary>
    /// The source picker is only for the two moments a person has to choose: no paired AirPods are known
    /// (so a nearby set is the only thing to look at), or two different pairs are heard and cannot be told apart.
    /// </summary>
    public bool ShowBroadcastSection => _viewModel is { HasBroadcastSources: true } vm
        && (Connection?.NeedsPairing == true || vm.IsBroadcastAmbiguous);

    /// <summary>Start/Stop is gone for nearby detection (Settings owns it); Demo and other transports keep their button.</summary>
    public bool ShowConnectButton => _viewModel is { IsBroadcastObservation: false };

    /// <summary>The divider above the footer, only when something sits below it.</summary>
    public bool ShowFooterDivider => ShowConnectButton || ShowBroadcastSection
        || _viewModel?.IsConnected == true || !string.IsNullOrEmpty(ConnectionHintText);
    public string PairedConnectText => Connection switch
    {
        { IsDisconnecting: true } => Strings.Flyout_Connect_Disconnecting,
        { IsConnecting: true } => Strings.Flyout_Connect_Connecting,
        { IsConnected: true } => Strings.Flyout_Connect_Disconnect,
        _ => Strings.Flyout_Connect_Connect,
    };

    public string TransportActionText => _viewModel is not { } vm ? Strings.Flyout_Connect_Connect
        : vm.IsDemo ? vm.ConnectActionText
        : string.Format(CultureInfo.CurrentCulture, Strings.Flyout_Transport_ControlsAction, vm.ConnectActionText);

    public string ConnectionCaptionText
    {
        get
        {
            if (_viewModel is not { } vm) return Strings.Flyout_Status_Unavailable;
            if (vm.IsDemo) return vm.ConnectionHeadline;
            if (Connection is not null) return PairedConnectionStatusText;
            return Strings.Flyout_Status_Unavailable;
        }
    }
    public string ConnectionHintText
    {
        get
        {
            if (_viewModel is not { } vm) return "";
            if (vm.IsDemo) return vm.ConnectionHint;
            return "";
        }
    }

    public string CurrentOutputNameText => AudioOutput switch
    {
        { CurrentWindowsOutputId: not null, CurrentWindowsOutputName.Length: > 0 } output => output.CurrentWindowsOutputName,
        { CurrentWindowsOutputId: not null } => Strings.Flyout_Output_CurrentWindows,
        _ => Strings.Flyout_Output_NoneFound,
    };
    public string CurrentOutputCaptionText => AudioOutput switch
    {
        null => Strings.Flyout_Output_VolumeUnavailable,
        { CurrentWindowsOutputId: null } output => string.IsNullOrWhiteSpace(output.CurrentWindowsOutputVolumeStatusText)
            ? Strings.Flyout_Output_NoSpeaker
            : output.CurrentWindowsOutputVolumeStatusText,
        { CanAdjustCurrentOutput: true } => Strings.Flyout_Output_DefaultPlayback,
        { } output => string.IsNullOrWhiteSpace(output.CurrentWindowsOutputVolumeStatusText)
            ? Strings.Flyout_Output_CantChangeVolume
            : output.CurrentWindowsOutputVolumeStatusText,
    };
    public string CurrentOutputVolumeText => AudioOutput?.CurrentWindowsOutputVolume is float volume
        ? volume.ToString("P0", CultureInfo.CurrentCulture)
        : ""; // nothing to report: no lone dash beside "No sound output found"
    public string CurrentOutputSliderName => AudioOutput?.CurrentWindowsOutputName is { Length: > 0 } name
        ? string.Format(CultureInfo.CurrentCulture, Strings.Flyout_Output_SliderNameWithDevice, name)
        : Strings.Flyout_Output_SliderName;
    /// <summary>The routine "Default Windows playback output" caption is hidden; only a caption that explains a problem shows.</summary>
    public bool ShowCurrentOutputCaption => !(AudioOutput is { CurrentWindowsOutputId: not null, CanAdjustCurrentOutput: true });
    public bool CanAdjustCurrentOutput => AudioOutput?.CanAdjustCurrentOutput == true;
    public bool ShowNearbyDetails => _nearbyDetailsExpanded && _viewModel?.HasBroadcastSources == true;
    public string NearbyDetailsActionName => _nearbyDetailsExpanded ? Strings.Flyout_Nearby_HideDetails : Strings.Flyout_Nearby_Details;

    private string _bluetoothLaunchStatusText = "";
    public string BluetoothLaunchStatusText
    {
        get => _bluetoothLaunchStatusText;
        private set
        {
            if (_bluetoothLaunchStatusText == value) return;
            _bluetoothLaunchStatusText = value;
            Bindings.Update();
        }
    }

    public event EventHandler? DismissRequested;

    public event EventHandler? SettingsRequested;

    /// <summary>Raised by the history button; the host decides where the battery history view is shown.</summary>
    public event EventHandler? BatteryHistoryRequested;

    /// <summary>
    /// Shows or hides the "Last heard ..." line under the connection status. The host passes
    /// <c>LastSeenTracker.Text(...)</c> when the AirPods are not connected, otherwise null.
    /// </summary>
    public void SetLastHeardText(string? text)
    {
        LastHeardLine.Text = text ?? "";
        LastHeardLine.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The content's natural height changed (connect/disconnect shows or hides sections, a
    /// caption appears): the host should refit the window so nothing is clipped or padded.
    /// </summary>
    public event EventHandler? ContentHeightChanged;

    /// <summary>Starts the visible-session subscriptions, refreshes the default output, and restores focus.</summary>
    /// <param name="byKeyboard">
    /// Opened from the keyboard (Win+B, Enter on the tray icon): focus with
    /// <see cref="FocusState.Keyboard"/> so the focus ring is visible. Pointer opens stay
    /// programmatic (no ring).
    /// </param>
    /// <param name="takeFocus">
    /// False for the passive case-open pop-up: the panel is shown without moving keyboard focus into it.
    /// </param>
    public void OnShown(long generation, bool byKeyboard = false, bool takeFocus = true)
    {
        if (_isShown && _shownGeneration != generation) OnHidden(_shownGeneration);
        _shownGeneration = generation;
        _isShown = true;
        Attach();
        AttachPlayback();
        AttachAudioOutput();
        AttachConnection();
        Bindings.Update();
        ClearHiddenBroadcastPick();
        ModeSelector.RefreshAvailability();
        AudioOutput?.RefreshCurrentWindowsOutput();
        _ = Connection?.RefreshAsync();
        ProjectOutputVolume();
        SyncHero(replay: true);
        if (takeFocus) FocusFirstControl(byKeyboard ? FocusState.Keyboard : FocusState.Programmatic);
    }

    /// <summary>
    /// The flyout's opening light. With the lens drawn, every panel's rim and edge band ramp in
    /// (<see cref="Design.GlassLens.Materialise"/>); otherwise the old one-off rim glint plays. One effect, never both.
    /// Call right after the entrance animation starts.
    /// </summary>
    public void PlayEntranceGlint()
    {
        RootSurface.PlayEntranceGlint(); // does nothing while the lens is drawn
        Design.GlassLens.Materialise(this);
    }

    /// <summary>Case-open moment: the lid opens and the visible rings fill. Safe to call repeatedly.</summary>
    public void PlayCaseOpen()
    {
        if (!IsLoaded)
        {
            _playRingsWhenLoaded = true;
            return;
        }
        Artwork.PlayOpen();
        if (ShowThreeRings)
        {
            LeftRing.PlayFill();
            RightRing.PlayFill();
            if (ShowCaseRing) CaseRing.PlayFill();
        }
        if (ShowSingleRing) SingleRing.PlayFill();
        if (ShowWindowsRing) WindowsRing.PlayFill();
    }

    /// <summary>Invalidates pending user volume intent as soon as the flyout starts closing.</summary>
    public void OnHidden(long generation)
    {
        if (_isShown && generation != _shownGeneration) return;
        _isShown = false;
        CancelOutputVolumeIntent();
        _artworkCueTimer?.Stop();
        Artwork.SuspendCueAnimations();
        Detach();
        DetachPlayback();
        DetachAudioOutput();
        DetachConnection();
        StopHalo();
    }

    /// <summary>
    /// Focus the listening-mode selector or the primary connection action, then volume and settings.
    /// </summary>
    public void FocusFirstControl(FocusState state = FocusState.Programmatic)
    {
        if (ModeSelector.FocusSelection())
        {
            // The selector focuses programmatically; promote it so a keyboard user sees the ring.
            if (state == FocusState.Keyboard && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Control focused)
                focused.Focus(FocusState.Keyboard);
            return;
        }
        if (ShowPairedConnection && TryFocus(PairedConnectButton, state)) return;
        if (TryFocus(ConnectButton, state)) return;
        if (TryFocus(OutputVolumeSlider, state)) return;
        SettingsIconButton.Focus(state);
    }

    private static bool TryFocus(Control control, FocusState state)
        => control.Visibility == Visibility.Visible && control.IsEnabled && control.Focus(state);

    /// <summary>
    /// A source picked while the picker was showing must not wait invisibly and reappear later as a
    /// stale choice (the pair it pointed at may be gone): once the picker hides, the pick is dropped.
    /// </summary>
    private void ClearHiddenBroadcastPick()
    {
        if (_viewModel is { HasSelectedBroadcastSource: true } vm && !ShowBroadcastSection)
            vm.SelectBroadcastSource(Guid.Empty);
    }

    private void OnBroadcastSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BroadcastSourcePicker.SelectedItem is BroadcastSourceViewModel source)
            _viewModel?.SelectBroadcastSource(source.Id);
    }

    /// <summary>
    /// After Connect/Disconnect (Start/End session) the clicked button is replaced or disabled;
    /// once the attempt settles, move focus to the visible relevant control (mode selector or the
    /// replacement button) so keyboard users are not dropped. Only after an action from this view.
    /// </summary>
    private void RestoreFocusAfterConnectionChange()
    {
        if (_restoreFocus == FocusState.Unfocused || _viewModel is not { } vm) return;
        if (vm.IsConnecting || vm.IsDisconnectPending) return; // not settled yet
        var state = _restoreFocus;
        _restoreFocus = FocusState.Unfocused;
        // Let x:Bind apply the new visibility first, then focus.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsLoaded) FocusFirstControl(state);
        });
    }

    // ---- View model plumbing (only what x:Bind cannot do) ---------------------------------

    private void Attach()
    {
        if (_viewModel is null || ReferenceEquals(_subscribed, _viewModel)) return;
        Detach();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _subscribed = _viewModel;
        WatchBatteries();
    }

    private void Detach()
    {
        if (_subscribed is null) return;
        _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = null;
        WatchBatteries();
    }

    /// <summary>
    /// Ring visibility depends on each battery's IsKnown (nearby L/R/Case and the Windows reading);
    /// x:Bind cannot see those through the OneTime bindings, so refresh when one changes.
    /// Re-targets the watched set to what is currently subscribed/shown.
    /// </summary>
    private void WatchBatteries()
    {
        foreach (var item in _watchedBatteries) item.PropertyChanged -= OnBatteryPropertyChanged;
        _watchedBatteries.Clear();
        if (!_isShown) return;
        if (_subscribed is { } vm)
        {
            _watchedBatteries.Add(vm.Left);
            _watchedBatteries.Add(vm.Right);
            _watchedBatteries.Add(vm.Case);
            _watchedBatteries.Add(vm.Single);
        }
        if (_subscribedConnection is { } connection) _watchedBatteries.Add(connection.WindowsBattery);
        foreach (var item in _watchedBatteries) item.PropertyChanged += OnBatteryPropertyChanged;
    }

    private void OnBatteryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BatteryItemViewModel.IsKnown)) return;
        void Update()
        {
            if (_isShown) Bindings.Update();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void AttachPlayback()
    {
        if (!_isShown || _playback is null || ReferenceEquals(_subscribedPlayback, _playback)) return;
        DetachPlayback();
        _subscribedPlayback = _playback;
        _subscribedPlayback.PropertyChanged += OnPlaybackPropertyChanged;
    }

    private void DetachPlayback()
    {
        if (_subscribedPlayback is null) return;
        _subscribedPlayback.PropertyChanged -= OnPlaybackPropertyChanged;
        _subscribedPlayback = null;
    }

    private void AttachAudioOutput()
    {
        if (!_isShown || _audioOutput is null || ReferenceEquals(_subscribedAudioOutput, _audioOutput)) return;
        DetachAudioOutput();
        _subscribedAudioOutput = _audioOutput;
        _subscribedAudioOutput.PropertyChanged += OnAudioOutputPropertyChanged;
    }

    private void DetachAudioOutput()
    {
        if (_subscribedAudioOutput is null) return;
        _subscribedAudioOutput.PropertyChanged -= OnAudioOutputPropertyChanged;
        _subscribedAudioOutput = null;
    }

    private void AttachConnection()
    {
        if (!_isShown || Connection is null || ReferenceEquals(_subscribedConnection, Connection)) return;
        DetachConnection();
        _subscribedConnection = Connection;
        _subscribedConnection.PropertyChanged += OnConnectionPropertyChanged;
        WatchBatteries();
    }

    private void DetachConnection()
    {
        if (_subscribedConnection is null) return;
        _subscribedConnection.PropertyChanged -= OnConnectionPropertyChanged;
        _subscribedConnection = null;
        WatchBatteries();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Update()
        {
            if (!_isShown || !ReferenceEquals(sender, _connection)) return;
            Bindings.Update();
            ClearHiddenBroadcastPick();
            SyncHero(replay: false);
            if (e.PropertyName is nameof(CompanionConnectionViewModel.StatusText)
                or nameof(CompanionConnectionViewModel.IsConnected)
                or nameof(CompanionConnectionViewModel.IsConnecting))
                AnnounceLiveRegion(ConnectionLine);
        }

        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HubViewModel.DeviceName)
            or nameof(HubViewModel.ConnectionHeadline)
            or nameof(HubViewModel.IsConnected)
            or nameof(HubViewModel.IsConnecting)
            or nameof(HubViewModel.IsBroadcastObservation)
            or nameof(HubViewModel.IsObserving)
            or nameof(HubViewModel.HasBroadcastSources)
            or nameof(HubViewModel.IsBroadcastAmbiguous)
            or nameof(HubViewModel.NearbyIdentityConfidence)
            or nameof(HubViewModel.UnavailableReason)
            or nameof(HubViewModel.HasSelectedBroadcastSource)
            or nameof(HubViewModel.HasUnavailableReason)
            or nameof(HubViewModel.ConnectionHint)
            or nameof(HubViewModel.BatteryLayout)
            or nameof(HubViewModel.DeviceNoun))
        {
            Bindings.Update();
            ClearHiddenBroadcastPick();
        }

        switch (e.PropertyName)
        {
            case nameof(HubViewModel.AreControlsAvailable):
                ModeSelector.RefreshAvailability();
                break;
            case nameof(HubViewModel.ModeStatusText):
                AnnounceLiveRegion(ModeStatus);
                break;
            case nameof(HubViewModel.ConnectionHeadline):
                AnnounceLiveRegion(ConnectionLine);
                break;
            case nameof(HubViewModel.LastErrorText):
                if (_viewModel?.HasError == true) AnnounceLiveRegion(ErrorText);
                break;
            case nameof(HubViewModel.UnavailableReason):
                if (_viewModel?.HasUnavailableReason == true) AnnounceLiveRegion(UnavailableText);
                break;
            case nameof(HubViewModel.ArtworkGroup):
            case nameof(HubViewModel.ArtworkModelId):
                // A different model: SyncHero swaps the drawing and keeps the remembered open or closed state.
                SyncHero(replay: false);
                break;
            case nameof(HubViewModel.IsConnected):
            case nameof(HubViewModel.IsConnecting):
                // The connect moment: lid opens and rings fill as the connection lands.
                SyncHero(replay: false);
                RestoreFocusAfterConnectionChange();
                break;
            case nameof(HubViewModel.ArtworkIsCharging):
            case nameof(HubViewModel.ArtworkIsWorn):
            case nameof(HubViewModel.ArtworkBudsOut):
                SyncHero(replay: false);
                break;
            case nameof(HubViewModel.ArtworkCueExpiresAt):
                ArmArtworkCueExpiry();
                break;
            case nameof(HubViewModel.IsDisconnectPending):
                RestoreFocusAfterConnectionChange();
                break;
        }
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Update()
        {
            if (!_isShown) return;
            Bindings.Update();
            if (e.PropertyName is nameof(PlaybackViewModel.Status) or nameof(PlaybackViewModel.Title))
                AnnounceLiveRegion(PlaybackStatusLine);
        }

        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void OnAudioOutputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Update()
        {
            if (!_isShown || !ReferenceEquals(sender, _audioOutput)) return;
            if (e.PropertyName is nameof(AudioOutputViewModel.CurrentWindowsOutputId)
                or nameof(AudioOutputViewModel.CurrentWindowsOutputGeneration))
            {
                InvalidateOutputIntentForTargetChange();
            }
            Bindings.Update();
            ProjectOutputVolume();
            if (e.PropertyName == nameof(AudioOutputViewModel.CurrentWindowsOutputVolumeStatusText))
                AnnounceLiveRegion(OutputVolumeSlider);
        }

        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void EnsureOutputVolumeTimer()
    {
        if (_outputVolumeTimer is not null) return;
        _outputVolumeTimer = DispatcherQueue.CreateTimer();
        _outputVolumeTimer.Interval = TimeSpan.FromMilliseconds(100);
        _outputVolumeTimer.IsRepeating = false;
        _outputVolumeTimer.Tick += OnOutputVolumeTimerTick;
    }

    private bool CaptureCurrentOutput()
    {
        if (AudioOutput is not { CanAdjustCurrentOutput: true, CurrentWindowsOutputId: { Length: > 0 } id } output)
            return false;
        _outputCapturedId = id;
        _outputCapturedGeneration = output.CurrentWindowsOutputGeneration;
        _outputCapturedViewGeneration = _shownGeneration;
        return true;
    }

    private bool IsOutputCaptureCurrent()
        => _isShown
            && _outputCapturedViewGeneration == _shownGeneration
            && AudioOutput is { CanAdjustCurrentOutput: true } output
            && string.Equals(_outputCapturedId, output.CurrentWindowsOutputId, StringComparison.OrdinalIgnoreCase)
            && _outputCapturedGeneration == output.CurrentWindowsOutputGeneration;

    private void ScheduleOutputVolumeIntent(double value)
    {
        if (!_isShown || _outputInteractionInvalidated || !IsOutputCaptureCurrent()) return;
        _pendingOutputValue = Math.Clamp(value, 0d, 1d);
        EnsureOutputVolumeTimer();
        _outputVolumeTimer!.Stop();
        _outputVolumeTimer.Start();
    }

    private void OnOutputVolumeTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsOutputCaptureCurrent() || AudioOutput is not { } output || _outputCapturedId is not { } id)
        {
            InvalidateOutputIntentForTargetChange();
            ProjectOutputVolume();
            return;
        }

        output.SetCurrentOutputVolume(id, _outputCapturedGeneration, (float)_pendingOutputValue);
        if (!_outputPointerActive && !_outputKeyboardActive)
            ClearOutputCapture();
        ProjectOutputVolume();
    }

    private void ProjectOutputVolume()
    {
        if (!_isShown || _outputPointerActive || _outputKeyboardActive || _outputVolumeTimer?.IsRunning == true) return;
        // An unknown volume is not zero: leave the (disabled) slider alone and let the "—" text
        // say so. The projection guard below means this can never be read back as user intent.
        if (AudioOutput?.CurrentWindowsOutputVolume is not float value) return;
        _projectingOutputVolume = true;
        try
        {
            OutputVolumeSlider.Value = Math.Clamp(value, OutputVolumeSlider.Minimum, OutputVolumeSlider.Maximum);
        }
        finally { _projectingOutputVolume = false; }
    }

    private void ClearOutputCapture()
    {
        _outputCapturedId = null;
        _outputCapturedGeneration = 0;
        _outputCapturedViewGeneration = 0;
    }

    private void CancelOutputVolumeIntent()
    {
        _outputVolumeTimer?.Stop();
        _outputPointerActive = false;
        _outputKeyboardActive = false;
        _outputInteractionInvalidated = false;
        ClearOutputCapture();
    }

    private void InvalidateOutputIntentForTargetChange()
    {
        bool interactionActive = _outputPointerActive || _outputKeyboardActive;
        _outputVolumeTimer?.Stop();
        ClearOutputCapture();
        _outputInteractionInvalidated = interactionActive;
    }

    /// <summary>Live regions only speak when the change event is raised explicitly.</summary>
    private static void AnnounceLiveRegion(FrameworkElement element)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // ---- Hero ----------------------------------------------------------------------------

    private enum HeroState { None, Idle, Connecting, Connected }

    /// <summary>
    /// Connected: lid open, rings filled. Connecting: lid open, halo pulsing. Otherwise: lid
    /// closed. Animates only on a real transition (several view-model properties change together),
    /// unless <paramref name="replay"/> asks for the full opening (each time the flyout opens).
    /// </summary>
    private void SyncHero(bool replay)
    {
        if (_viewModel is not { } vm || !IsLoaded) return;
        // The drawing follows the model even when the connection state did not change.
        Artwork.SetModel(vm.ArtworkModelId, SavedArtworkColorway());
        Artwork.Group = vm.ArtworkGroup;
        Artwork.SetStatusCues(vm.ArtworkIsCharging, vm.ArtworkIsWorn);
        SyncBudsOut(vm);
        ArmArtworkCueExpiry();
        // Either link counts: the Clasp control session or the Windows audio connection.
        bool connected = vm.IsConnected || Connection?.IsConnected == true;
        bool connecting = vm.IsConnecting || Connection?.IsConnecting == true || Connection?.IsDisconnecting == true;
        var next = connected ? HeroState.Connected : connecting ? HeroState.Connecting : HeroState.Idle;
        var previous = _hero;
        if (next == previous && !replay) return;
        _hero = next;
        bool animate = replay || previous != HeroState.None;

        if (next == HeroState.Connecting && _isShown) StartHalo();
        else StopHalo();

        switch (next)
        {
            case HeroState.Connected when !animate:
                Artwork.ShowOpen();
                break;
            case HeroState.Connected:
                // Coming from Connecting the lid is already open: only the rings fill.
                if (previous == HeroState.Connecting && !replay) Artwork.ShowOpen();
                else Artwork.PlayOpen();
                break;
            case HeroState.Connecting:
                if (animate) Artwork.PlayOpen();
                else Artwork.ShowOpen();
                break;
            default:
                Artwork.ShowClosed();
                break;
        }

        if (!_isShown) Artwork.SuspendCueAnimations();
    }

    private string? CurrentArtworkDeviceId => _viewModel?.IsDemo == true
        ? _viewModel.ArtworkDeviceId
        : !string.IsNullOrWhiteSpace(Connection?.SelectedDeviceId)
            ? Connection.SelectedDeviceId
            : _viewModel?.ArtworkDeviceId;

    /// <summary>The saved colourway for the current model, or null (the drawing's first colourway) when none or no longer declared.</summary>
    private string? SavedArtworkColorway()
    {
        var modelId = _viewModel?.ArtworkModelId;
        var saved = _settings?.GetArtworkColorway(CurrentArtworkDeviceId, modelId);
        if (saved is null) return null;
        var key = ArtworkCatalog.DrawingKeyFor(modelId);
        var document = key is null ? null : ArtworkStore.Get(key);
        return document?.Colorways.Any(way => string.Equals(way.Id, saved, StringComparison.Ordinal)) == true ? saved : null;
    }

    private void ArmArtworkCueExpiry()
    {
        if (_artworkCueTimer is null) return;
        _artworkCueTimer.Stop();
        if (!_isShown || !IsLoaded || _viewModel?.ArtworkCueExpiresAt is not { } expiry) return;
        var remaining = expiry - DateTimeOffset.UtcNow;
        _artworkCueTimer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        _artworkCueTimer.Start();
    }

    private void OnArtworkCueFreshnessTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_isShown || !IsLoaded || _viewModel is not { } vm) return;
        Artwork.SetStatusCues(vm.ArtworkIsCharging, vm.ArtworkIsWorn);
        SyncBudsOut(vm);
        ArmArtworkCueExpiry();
    }

    private void OnArtworkGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && _viewModel is { } vm) SyncBudsOut(vm);
    }

    /// <summary>
    /// The buds that are out of the case stand over their own battery rings (left over L, right over R) and the case over the case ring.
    /// Only with three rings and a case ring, so the columns line up; anything else keeps the single case.
    /// </summary>
    private void SyncBudsOut(HubViewModel vm)
    {
        if (!ShowThreeRings || !ShowCaseRing || ArtworkViewbox.ActualHeight <= 0 || ArtworkGrid.ActualWidth <= 0 || LeftRing.ActualWidth <= 0)
        {
            Artwork.ClearSplitColumns();
            Artwork.SetBudsOut(false, false);
            return;
        }

        // Ring centres relative to the middle of the drawing, in stage units (the stage is shown scaled by the viewbox).
        var scale = ArtworkViewbox.ActualHeight / 150d;
        double Offset(FrameworkElement ring)
            => (ring.TransformToVisual(ArtworkGrid).TransformPoint(new Windows.Foundation.Point(ring.ActualWidth / 2, 0)).X - (ArtworkGrid.ActualWidth / 2)) / scale;
        Artwork.SetSplitColumns(Offset(LeftRing), Offset(RightRing), Offset(CaseRing));
        var buds = vm.ArtworkBudsOut;
        Artwork.SetBudsOut(buds.Left, buds.Right);
    }
    /// <summary>
    /// A slow breathing glow behind the case while a connection attempt runs: a progress cue,
    /// bounded by the view model's connect timeout. Reduced motion: a steady glow instead.
    /// </summary>
    private void StartHalo()
    {
        if (_haloRunning) return;
        _haloRunning = true;
        var visual = Motion.VisualOf(Halo);
        var size = Halo.ActualSize;
        visual.CenterPoint = new Vector3(size.X / 2, size.Y / 2, 0);
        if (EffectsPolicy.ReduceMotion)
        {
            Motion.AnimateOpacity(Halo, 0.7f, MotionTokens.ReducedFade);
            return;
        }

        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
        var breathe = c.CreateScalarKeyFrameAnimation();
        breathe.InsertKeyFrame(0f, 0.35f);
        breathe.InsertKeyFrame(0.5f, 0.95f, ease);
        breathe.InsertKeyFrame(1f, 0.35f, ease);
        breathe.Duration = HaloPeriod;
        breathe.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", breathe);

        var swell = c.CreateVector3KeyFrameAnimation();
        swell.InsertKeyFrame(0f, new Vector3(0.88f, 0.88f, 1f));
        swell.InsertKeyFrame(0.5f, new Vector3(1.06f, 1.06f, 1f), ease);
        swell.InsertKeyFrame(1f, new Vector3(0.88f, 0.88f, 1f), ease);
        swell.Duration = HaloPeriod;
        swell.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Scale", swell);
    }

    private void StopHalo()
    {
        if (!_haloRunning) return;
        _haloRunning = false;
        var visual = Motion.VisualOf(Halo);
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        Motion.AnimateOpacity(Halo, 0f, MotionTokens.Dismiss);
    }

    // ---- Input ---------------------------------------------------------------------------

    private void OnAnyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOutputVolumePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isShown) return;
        _outputPointerActive = true;
        _outputInteractionInvalidated = false;
        if (!CaptureCurrentOutput()) InvalidateOutputIntentForTargetChange();
    }

    private void OnOutputVolumePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _outputPointerActive = false;
        FinishOutputInteractionIfIdle();
    }

    private void OnOutputVolumePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_outputPointerActive) return;
        _outputPointerActive = false;
        _outputVolumeTimer?.Stop();
        ClearOutputCapture();
        _outputInteractionInvalidated = false;
        ProjectOutputVolume();
    }

    private void OnOutputVolumePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_outputPointerActive) return;
        if (!e.Pointer.IsInContact)
        {
            // WinUI can report capture loss before PointerReleased. Keep the 100 ms click/drag
            // intent alive; the routed release handler will normally finish the interaction too.
            _outputPointerActive = false;
            FinishOutputInteractionIfIdle();
            return;
        }

        OnOutputVolumePointerCanceled(sender, e);
    }

    private void OnOutputVolumeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isShown || !IsOutputVolumeKey(e.Key)) return;
        if (!_outputKeyboardActive)
        {
            _outputKeyboardActive = true;
            _outputInteractionInvalidated = false;
            if (!CaptureCurrentOutput()) InvalidateOutputIntentForTargetChange();
        }
    }

    private void OnOutputVolumeKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!IsOutputVolumeKey(e.Key)) return;
        _outputKeyboardActive = false;
        FinishOutputInteractionIfIdle();
    }

    private static bool IsOutputVolumeKey(VirtualKey key)
        => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown;

    private void FinishOutputInteractionIfIdle()
    {
        if (_outputPointerActive || _outputKeyboardActive) return;
        if (_outputInteractionInvalidated)
        {
            if (_outputVolumeTimer?.IsRunning != true) ClearOutputCapture();
            _outputInteractionInvalidated = false;
        }
        else if (_outputVolumeTimer?.IsRunning != true)
        {
            ClearOutputCapture();
        }
        ProjectOutputVolume();
    }

    private void OnOutputVolumeValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_projectingOutputVolume || !_isShown || _outputInteractionInvalidated) return;

        // Automation clients (including Narrator's range-value pattern) can change Slider.Value
        // without pointer or keyboard events. Projection updates are separately guarded above.
        if (_outputCapturedId is null && !CaptureCurrentOutput()) return;
        ScheduleOutputVolumeIntent(e.NewValue);
    }

    private void OnNearbyDetailsClick(object sender, RoutedEventArgs e)
    {
        _nearbyDetailsExpanded = !_nearbyDetailsExpanded;
        Bindings.Update();
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm) return;
        // Remember how the action was invoked so focus returns the same way when it settles.
        _restoreFocus = sender is Control { FocusState: FocusState.Keyboard } ? FocusState.Keyboard : FocusState.Programmatic;
        try
        {
            if (vm.IsConnected) await vm.DisconnectAsync();
            else await vm.ConnectAsync();
            RestoreFocusAfterConnectionChange(); // an immediate failure settles without a state change
        }
        catch (Exception)
        {
            // The view model reports failures through LastErrorText; never crash the flyout.
        }
    }

    private async void OnPairedConnectClick(object sender, RoutedEventArgs e)
    {
        if (Connection is not { } connection) return;
        try
        {
            if (connection.CanDisconnect) await connection.DisconnectAsync();
            else if (connection.CanConnect) await connection.ConnectAsync();
        }
        catch (Exception)
        {
            // The connection view model reports the bounded Windows result in its status text.
        }
    }

    private async void OnModeRequested(object? sender, ListeningMode mode)
    {
        if (_viewModel is not { } vm) return;
        try
        {
            await vm.RequestModeAsync(mode);
        }
        catch (Exception)
        {
            // The view model reports failures through LastErrorText; never crash the flyout.
        }
    }

    private async void OnAwarenessToggled(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm || sender is not ToggleSwitch toggle) return;
        try
        {
            // Programmatic IsOn updates echo here; the view model ignores unchanged values.
            await vm.SetConversationAwarenessAsync(toggle.IsOn);
        }
        catch (Exception)
        {
        }
    }

    private async void OnStrengthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_viewModel is not { } vm) return;
        try
        {
            await vm.SetAdaptiveStrengthAsync(e.NewValue);
        }
        catch (Exception)
        {
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnBatteryHistoryClick(object sender, RoutedEventArgs e) => BatteryHistoryRequested?.Invoke(this, EventArgs.Empty);

    private async void OnPlaybackCenterClick(object sender, RoutedEventArgs e)
    {
        // Resolve and capture the explicit action before dispatch; this never sends a generic toggle.
        var action = PlaybackCenterAction;
        if (action is not PlaybackAction.Play and not PlaybackAction.Pause) return;
        await RunPlaybackCommandAsync(vm => action == PlaybackAction.Pause ? vm.PauseAsync() : vm.PlayAsync());
    }

    private async void OnPlaybackPreviousClick(object sender, RoutedEventArgs e)
        => await RunPlaybackCommandAsync(vm => vm.PreviousAsync());

    private async void OnPlaybackNextClick(object sender, RoutedEventArgs e)
        => await RunPlaybackCommandAsync(vm => vm.NextAsync());

    private async Task RunPlaybackCommandAsync(Func<PlaybackViewModel, Task> command)
    {
        if (Playback is not { } playback) return;
        try
        {
            await command(playback);
        }
        catch (OperationCanceledException)
        {
            // Closing or disabling the feature cancels outstanding media work.
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Windows media command failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Opens Windows' own "Add a device" dialog; Clasp never pairs devices itself.</summary>
    private void OnPairNewAirPodsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings-connectabledevices:devicediscovery") { UseShellExecute = true });
            BluetoothLaunchStatusText = Strings.Flyout_Pairing_AddDeviceOpened;
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Windows Add a device could not be opened ({ex.GetType().Name}).");
            OnOpenBluetoothSettingsClick(sender, e);
        }
    }

    private void OnOpenBluetoothSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            BluetoothLaunchStatusText = Strings.Flyout_Pairing_BluetoothSettingsOpened;
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Windows Bluetooth settings could not be opened ({ex.GetType().Name}).");
            BluetoothLaunchStatusText = Strings.Flyout_Pairing_BluetoothSettingsFailed;
        }
    }
}
