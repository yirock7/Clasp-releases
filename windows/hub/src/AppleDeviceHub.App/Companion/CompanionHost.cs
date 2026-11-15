using System.ComponentModel;
using AppleDeviceHub.App.Audio;
using AppleDeviceHub.App.Connection;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AppleDeviceHub.App.Companion;

/// <summary>
/// Owns the companion features that sit on top of the tray app: identity feed, connection
/// actions, ear detection, Handsfree, keep-alive, Fix connection, hand-off notice, battery
/// history and alerts, tray badge, links and shortcuts, stem actions, per-app output, run on
/// startup and phone audio. Each feature lives in its own partial file; this file holds what they
/// share: the inputs, the one-second re-evaluation timer and the shutdown order.
/// </summary>
/// <remarks>
/// Threading: everything here runs on the UI thread. Events that arrive on worker threads
/// (transport state, microphone watcher, keep-alive renderer, repair progress, router, audio
/// sessions, phone streaming) are posted to the UI <see cref="DispatcherQueue"/> first. COM and
/// audio calls therefore happen on the UI thread, as the packages require. Every feature that is
/// switched off does nothing and touches nothing.
/// </remarks>
internal sealed partial class CompanionHost : IAsyncDisposable
{
    private readonly HubSettings _settings;
    private readonly DispatcherQueue _queue;
    private readonly HubViewModel _viewModel;
    private readonly IAirPodsTransport _transport;
    private readonly BroadcastObservationTransport? _broadcast;
    private readonly CompanionConnectionViewModel? _connection;
    private readonly WindowsAirPodsConnectionController? _controller;
    private readonly IAudioOutputController? _audio;
    private readonly AudioOutputViewModel? _audioVm;
    private readonly Func<string> _baseTooltip;
    private readonly Action _showSettings;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private ElementTheme _theme = ElementTheme.Default;
    private DispatcherQueueTimer? _tick;
    private volatile ConnectionObservation? _observation;
    private volatile int _connectionGeneration;
    private int _selectionGeneration; // bumped only when a different device is selected
    private string? _observedDeviceId;
    private bool _started;
    private volatile bool _disposed;

    public CompanionHost(
        HubSettings settings,
        DispatcherQueue queue,
        HubViewModel viewModel,
        IAirPodsTransport transport,
        BroadcastObservationTransport? broadcast,
        CompanionConnectionViewModel? connection,
        WindowsAirPodsConnectionController? controller,
        IAudioOutputController? audio,
        AudioOutputViewModel? audioViewModel,
        Func<string> baseTooltip,
        Action showSettings)
    {
        _settings = settings;
        _queue = queue;
        _viewModel = viewModel;
        _transport = transport;
        _broadcast = broadcast;
        _connection = connection;
        _controller = controller;
        _audio = audio;
        _audioVm = audioViewModel;
        _baseTooltip = baseTooltip;
        _showSettings = showSettings;

        InitializeIdentityAndAwareness();
        InitializeRepair();
        InitializeCommands();

        if (_connection is not null)
        {
            _connection.SnapshotApplied += OnSnapshotApplied;
            _connection.SelectedDeviceChanged += OnSelectedDeviceChanged;
            _connection.UserConnectRequested += OnUserConnectRequested;
            _connection.UserDisconnectConfirmed += OnUserDisconnectConfirmed;
        }
        if (_broadcast is not null) _broadcast.SourcesChanged += OnBroadcastSourcesChanged;
        _transport.StateChanged += OnTransportStateChanged;
        if (_audioVm is not null) _audioVm.PropertyChanged += OnAudioViewModelPropertyChanged;
    }

    /// <summary>Status lines for the settings page.</summary>
    public CompanionStatus Status { get; } = new();

    /// <summary>Gives the host the shell windows it updates (tray icon) and opens (flyout, notices).</summary>
    public void AttachShell(TrayIcon tray, FlyoutWindow flyout)
    {
        _tray = tray;
        _flyout = flyout;
        tray.ThemeChanged += OnTrayThemeChanged;
        flyout.BatteryHistoryRequested += OnFlyoutBatteryHistoryRequested;
    }

    public void SetTheme(ElementTheme theme)
    {
        _theme = theme;
        ApplyHandoffTheme();
    }

    /// <summary>Applies every setting once the windows exist. Nothing here runs a feature that is switched off.</summary>
    public async Task StartAsync()
    {
        if (_disposed) return;
        _started = true;
        ApplyPlaybackNeed();
        ApplyHandoffSetting();
        ApplyHandsfreeSetting();
        ApplyKeepAliveSetting();
        ApplyStemSetting();
        ApplyLinksSetting();
        ApplyHotkeys();
        ApplyPerAppSetting(initial: true);
        UpdateTray(force: true);
        EnsureTick();
        await ApplyEarMediaSettingAsync();
    }

    /// <summary>Forwards a changed setting to the feature it belongs to.</summary>
    public void OnSettingChanged(string? name)
    {
        if (_disposed || !_started) return;
        switch (name)
        {
            case nameof(HubSettings.AutoPlayOnConnect):
            case nameof(HubSettings.AutoPauseOnDisconnect):
                ApplyPlaybackNeed();
                break;
            case nameof(HubSettings.PauseMediaOnEarRemoval):
            case nameof(HubSettings.BroadcastEarDetection):
            case nameof(HubSettings.EarRemovedOutputEndpointId):
                _ = ApplyEarMediaSettingAsync();
                break;
            case nameof(HubSettings.HandsfreeMode):
                ApplyHandsfreeSetting();
                break;
            case nameof(HubSettings.LowAudioLatency):
                ApplyKeepAliveSetting();
                break;
            case nameof(HubSettings.HandoffNoticeEnabled):
                ApplyHandoffSetting();
                break;
            case nameof(HubSettings.ShowTrayBatteryPercentage):
            case nameof(HubSettings.DimTrayIconWhenAudioElsewhere):
            case nameof(HubSettings.LowBatteryThresholdPercent):
                UpdateTray();
                break;
            case nameof(HubSettings.AllowClaspLinks):
                ApplyLinksSetting();
                break;
            case nameof(HubSettings.StemActionsEnabled):
            case nameof(HubSettings.StemPlayPauseAction):
            case nameof(HubSettings.StemNextAction):
            case nameof(HubSettings.StemPreviousAction):
                ApplyStemSetting();
                break;
            case nameof(HubSettings.PerAppOutputEnabled):
            case nameof(HubSettings.PerAppRulesText):
                ApplyPerAppSetting(initial: false);
                break;
            default:
                if (HubSettings.IsHotkeyProperty(name)) ApplyHotkeys();
                break;
        }
    }

    /// <summary>Sleep or resume: never carry intent across it, and let the audio link restart cleanly.</summary>
    public void OnSystemPowerChange()
    {
        if (_disposed) return;
        _earMedia?.Forget(); // synchronous: a decision already queued must not survive the power change
        Post(() =>
        {
            _earSwitch.Forget();
            _actionsPolicy.Reset();
            ClearConnectionOrigin();
            _firstObservationForDevice = true;
            _actionsRecheckAt = null;
            _handoff.Reset();
            _keepAlive?.Stop();
            FlushBatteryData();
            EvaluateKeepAlive();
            EnsureTick();
        });
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (now if already there). Dropped after shutdown.</summary>
    private void Post(Action action)
    {
        if (_disposed) return;
        void Guarded()
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception ex) { LocalLog.Write($"Companion handler failed ({ex.GetType().Name})."); }
        }
        if (_queue.HasThreadAccess) Guarded();
        else _queue.TryEnqueue(Guarded);
    }

    // ----- Connection snapshot: the one place Windows' state reaches every feature -----

    private void OnSnapshotApplied(object? sender, ConnectionSnapshot snapshot)
    {
        if (_disposed || _connection is null) return;
        var previousConnected = _observation?.Connected;
        var device = _connection.SelectedDevice;
        SyncSelection(device);
        Status.HasSelectedDevice = device is not null;

        // Only a complete snapshot reaches this event, so a device that is missing from it is
        // simply not selected; it is never read as "disconnected".
        var observation = device is null ? null : ConnectionObservation.From(snapshot, device.Id);
        _observation = observation;
        if (previousConnected != observation?.Connected) _connectionGeneration++;

        PushIdentity(device);
        if (_repair?.IsRunning != true)
        {
            EvaluateConnectionActions(isTick: false);
            ObserveHandoffConnection(observation);
        }
        _earMedia?.NotifyWindowsConnection(observation?.Connected == true);
        ConfigureEarOutputSwitch();
        EvaluateHandsfree();
        EvaluateKeepAlive();
        UpdateStemContext();
        UpdatePerAppEndpoint();
        UpdateTray();
        RefreshLastHeard();
        EnsureTick();
    }

    private void OnSelectedDeviceChanged(object? sender, EventArgs e)
    {
        if (_disposed || _connection is null) return;
        var device = _connection.SelectedDevice;
        SyncSelection(device);
        Status.HasSelectedDevice = device is not null;
        PushIdentity(device);
        ConfigureEarOutputSwitch();
        _observation = null;
        _earMedia?.NotifyWindowsConnection(false);
        EvaluateHandsfree();
        EvaluateKeepAlive();
        UpdateStemContext();
        UpdatePerAppEndpoint();
        UpdateTray();
        RefreshLastHeard();
        EnsureTick();
    }

    /// <summary>A different (or no) AirPods was selected: every "once per connect" memory starts over.</summary>
    private void SyncSelection(ConnectionDevice? device)
    {
        if (string.Equals(device?.Id, _observedDeviceId, StringComparison.Ordinal)) return;
        _observedDeviceId = device?.Id;
        _connectionGeneration++;
        _selectionGeneration++;
        ClearConnectionOrigin();
        _observation = null;
        _actionsPolicy.Reset();
        _firstObservationForDevice = true;
        _actionsRecheckAt = null;
        _handoff.Reset();
        _earMedia?.Forget();
        _earSwitch.Forget();
        _hfPolicy.Reset();
        _hfRecheckAt = null;
        _kaPolicy.Reset();
        _alerts.Reset();
    }

    private void OnAudioViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AudioOutputViewModel.CurrentWindowsOutputId) or nameof(AudioOutputViewModel.CurrentWindowsOutputGeneration)))
            return;
        Post(ObserveEarOutputs);
    }

    // ----- One shared one-second timer, running only while something is waiting for a time -----

    private bool NeedsTick()
        => _actionsRecheckAt is not null
            || _kaRetryAt is not null
            || _hfRecheckAt is not null
            || _trayBadgeExpiresAt is not null
            || _handoff.State == HandoffState.Watching;

    private void EnsureTick()
    {
        if (_disposed) return;
        if (NeedsTick())
        {
            if (_tick is null)
            {
                _tick = _queue.CreateTimer();
                _tick.Interval = TimeSpan.FromSeconds(1);
                _tick.IsRepeating = true;
                _tick.Tick += OnTick;
            }
            if (!_tick.IsRunning) _tick.Start();
        }
        else if (_tick is { IsRunning: true })
        {
            _tick.Stop();
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_actionsRecheckAt is { } actionsAt && now >= actionsAt)
            {
                _actionsRecheckAt = null;
                if (_repair?.IsRunning != true) EvaluateConnectionActions(isTick: true);
            }
            if (_kaRetryAt is { } keepAliveAt && now >= keepAliveAt)
            {
                _kaRetryAt = null;
                EvaluateKeepAlive();
            }
            if (_hfRecheckAt is { } handsfreeAt && now >= handsfreeAt)
            {
                _hfRecheckAt = null;
                EvaluateHandsfree();
            }
            if (_trayBadgeExpiresAt is { } badgeExpiresAt && now >= badgeExpiresAt)
            {
                _trayBadgeExpiresAt = null;
                UpdateTray(force: true);
            }
            if (_handoff.State == HandoffState.Watching) EvaluateHandoff(now);
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Companion timer step failed ({ex.GetType().Name}).");
        }
        finally
        {
            EnsureTick();
        }
    }

    // ----- Shutdown: reverse order of creation -----

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_connection is not null)
        {
            _connection.SnapshotApplied -= OnSnapshotApplied;
            _connection.SelectedDeviceChanged -= OnSelectedDeviceChanged;
            _connection.UserConnectRequested -= OnUserConnectRequested;
            _connection.UserDisconnectConfirmed -= OnUserDisconnectConfirmed;
        }
        if (_broadcast is not null) _broadcast.SourcesChanged -= OnBroadcastSourcesChanged;
        _transport.StateChanged -= OnTransportStateChanged;
        if (_audioVm is not null) _audioVm.PropertyChanged -= OnAudioViewModelPropertyChanged;
        if (_tray is not null) _tray.ThemeChanged -= OnTrayThemeChanged;
        if (_flyout is not null) _flyout.BatteryHistoryRequested -= OnFlyoutBatteryHistoryRequested;

        if (_tick is not null)
        {
            _tick.Stop();
            _tick.Tick -= OnTick;
            _tick = null;
        }

        // Created last, disposed first.
        DisposePhone();
        DisposePerApp();
        DisposeStem();
        DisposeCommands();
        DisposeRepair();
        DisposeKeepAlive();
        DisposeHandsfree();
        await DisposeEarMediaAsync();
        await DisposeConnectionActionsAsync();
        DisposeAwareness();
    }
}
