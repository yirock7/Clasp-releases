using AppleDeviceHub.App.Battery;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.App.Companion;

// P1-A identity feed, P3-A hand-off notice, P3-B/P3-C battery history, alerts and last seen,
// P4-A tray silhouette tinted by battery level.
internal sealed partial class CompanionHost
{
    /// <summary>History only records a reading while it is this young, so a state republished for its age cannot look like a new measurement.</summary>
    private static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The tray number stays up this long after the last broadcast (the same as the transport's LostAfter):
    /// in-ear broadcasts are sparse, and the owner watches levels over hours. After it, no number.
    /// </summary>
    private static readonly TimeSpan MaxTrayReadingAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(2);

    private readonly HandoffDetector _handoff = new();
    private readonly LowBatteryAlertPolicy _alerts = new();
    private HandoffNoticeWindow? _handoffWindow;
    private BatteryHistory? _history;
    private LastSeenTracker? _lastSeen;
    private LastCaseMemory? _lastCaseMemory;
    /// <summary>The device whose remembered case level was last given to the transport (null = none), so it is given once per selection, never again after the transport dropped it.</summary>
    private string? _rememberedCaseDeviceId;
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;
    private int _sourcesQueued;
    /// <summary>Newest transport state with the epoch and identity captured when it arrived; only the newest is applied.</summary>
    private TransportUpdate? _latestTransport;
    private sealed record TransportUpdate(DeviceState State, SelectedDeviceEpoch Epoch, IdentityConfidence Identity);
    private int _transportPending;
    private TrayBadge? _lastBadge;
    private DateTimeOffset? _trayBadgeExpiresAt;

    /// <summary>Raised when the flyout's battery-history button is pressed.</summary>
    public event EventHandler? BatteryHistoryRequested;

    private void InitializeIdentityAndAwareness()
    {
        // The demo has no real battery data, so nothing is read from or written to disk for it.
        if (_viewModel.IsDemo) return;
        var store = new FileBatteryHistoryStore();
        _history = new BatteryHistory(store);
        _history.Load();
        _lastSeen = new LastSeenTracker(store);
        _lastSeen.Load();
        _lastCaseMemory = new LastCaseMemory(store);
        _lastCaseMemory.Load();
    }

    // ----- P1-A: which nearby broadcast is mine -----

    /// <summary>
    /// Tells the nearby-broadcast transport which paired AirPods is selected (its Apple model id,
    /// Windows' connected state as a timing clue and Windows' battery as an optional clue). Only
    /// called for complete snapshots or a selection change; null clears the model filter.
    /// </summary>
    private void PushIdentity(ConnectionDevice? device)
    {
        if (_broadcast is null) return;
        _broadcast.SetPairedDevice(device?.ModelId, device?.Id);
        // SetPairedDevice clears the transport's remembered case on a change, so hand it over afterwards, once per selection.
        // (Not on every snapshot: the transport may have dropped it on purpose because it could belong to another pair.)
        if (!string.Equals(_rememberedCaseDeviceId, device?.Id, StringComparison.Ordinal))
        {
            _rememberedCaseDeviceId = device?.Id;
            var remembered = _lastCaseMemory?.Get(device?.Id);
            _broadcast.SetRememberedCase(remembered?.Percent, remembered?.Time);
        }
        if (device is null) return;
        _broadcast.ObserveWindowsConnection(device.IsConnected);
        _broadcast.SetWindowsBattery(device.WindowsBatteryPercent);
    }

    // ----- P3-A: "Your AirPods moved to another device" -----

    private void ApplyHandoffSetting()
    {
        _handoff.Enabled = _settings.HandoffNoticeEnabled;
        EnsureTick();
    }

    private void ObserveHandoffConnection(ConnectionObservation? observation)
    {
        if (observation is null) return;
        _handoff.ObserveConnection(observation.Connected, DateTimeOffset.UtcNow);
    }

    /// <summary>The nearby source supported by an independent pairing clue for the selected AirPods, if one is heard.</summary>
    private BroadcastSource? MatchedSource()
        => _broadcast?.Sources
            .Where(source => source.Identity.HasIndependentClue())
            .OrderByDescending(source => source.Identity)
            .ThenByDescending(source => source.LastHeardAt)
            .FirstOrDefault();

    private void EvaluateHandoff(DateTimeOffset now)
    {
        var source = MatchedSource();
        _handoff.ObserveBroadcast(source is null ? null : HandoffBroadcast.FromSource(source), now);
        if (_handoff.Evaluate(now)) PresentHandoff();
    }

    private void PresentHandoff()
    {
        if (_connection is null) return;
        if (NativeMethods.IsUserBusy())
        {
            LocalLog.Write("Hand-off notice suppressed while the user is busy or in full screen.");
            return;
        }
        _handoffWindow ??= new HandoffNoticeWindow(_connection);
        ApplyHandoffTheme();
        _handoffWindow.Present();
    }

    private void ApplyHandoffTheme() => _handoffWindow?.SetTheme(_theme);

    private void OnBroadcastSourcesChanged(object? sender, EventArgs e)
    {
        // Raised on the transport's thread for every sighting: coalesce into one UI pass.
        var epoch = new SelectedDeviceEpoch(_settings.AirPodsDeviceId, _connectionGeneration);
        if (Interlocked.Exchange(ref _sourcesQueued, 1) != 0) return;
        if (_disposed || !_queue.TryEnqueue(() => HandleSourcesChanged(epoch))) Interlocked.Exchange(ref _sourcesQueued, 0);
    }

    private void HandleSourcesChanged(SelectedDeviceEpoch epoch)
    {
        Interlocked.Exchange(ref _sourcesQueued, 0);
        if (_disposed) return;
        if (!epoch.IsCurrent(_settings.AirPodsDeviceId, _connectionGeneration))
        {
            // A selection change invalidated the queued sighting. Re-read the current source set
            // under the new generation so an event coalesced behind the stale one is not lost.
            // With no paired device selected no epoch is ever current, so re-reading would requeue
            // this pass forever (a UI-thread loop at full CPU); there is nothing to apply then.
            if (!string.IsNullOrWhiteSpace(_settings.AirPodsDeviceId)) OnBroadcastSourcesChanged(this, EventArgs.Empty);
            return;
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_handoff.State == HandoffState.Watching) EvaluateHandoff(now);
            ObserveLastSeen();
            UpdateTray();
            RefreshLastHeard();
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Nearby update failed ({ex.GetType().Name}).");
        }
        EnsureTick();
    }

    // ----- P3-B / P3-C: battery history, low-battery alert, last seen -----

    private void OnTransportStateChanged(object? sender, DeviceState state)
    {
        var epoch = new SelectedDeviceEpoch(_settings.AirPodsDeviceId, _connectionGeneration);
        var identity = state.Origin switch
        {
            DataOrigin.Broadcast => _broadcast?.IdentityConfidence ?? IdentityConfidence.Unknown,
            DataOrigin.Hardware => IdentityConfidence.LikelyYours,
            _ => IdentityConfidence.Unknown,
        };
        Volatile.Write(ref _latestTransport, new TransportUpdate(state, epoch, identity));
        if (Interlocked.Exchange(ref _transportPending, 1) != 0) return;
        Post(() =>
        {
            // Reset first so a state arriving while we apply queues a fresh callback.
            Interlocked.Exchange(ref _transportPending, 0);
            if (Volatile.Read(ref _latestTransport) is not { } latest) return;
            if (!latest.Epoch.IsCurrent(_settings.AirPodsDeviceId, _connectionGeneration)) return;
            UpdateTray();
            RecordBattery(latest.State, latest.Epoch.DeviceId, latest.Identity);
            RememberCase(latest.State, latest.Epoch.DeviceId, latest.Identity);
        });
    }

    private void RecordBattery(DeviceState state, string? deviceId, IdentityConfidence identity)
    {
        if (_history is null || state.IsDemo) return;
        if (string.IsNullOrEmpty(deviceId)) return;

        BatterySampleSource source;
        switch (state.Origin)
        {
            case DataOrigin.Broadcast:
                source = BatterySampleSource.Broadcast;
                break;
            case DataOrigin.Hardware:
                // The driver session talks to the paired AirPods itself.
                source = BatterySampleSource.Driver;
                break;
            default:
                return;
        }

        // The reading's own time, never "now": a state republished because it went stale must not
        // look like a fresh measurement. A part kept on screen from an older message (the case while the
        // buds are in the ears) is left out of the sample rather than stored as just measured.
        if (BatterySampling.FromState(state.Battery, source) is not { } sample) return;
        var now = DateTimeOffset.UtcNow;
        if (now - sample.Time > MaxReadingAge) return;

        _history.Record(deviceId, sample, identity);

        var alert = _alerts.Evaluate(deviceId, identity, sample.Left, sample.Right, sample.PodCharging, _settings.LowBatteryThresholdPercent, state.Layout, state.DeviceNoun);
        if (alert is not null && !Toasts.ShowLowBattery(alert))
            LocalLog.Write("Low battery alert could not be shown as a notification.");

        if (now - _lastFlush >= FlushEvery) FlushBatteryData();
    }

    /// <summary>
    /// Remembers a fresh case level across restarts. Display only: the gate is Probable or better (what is on screen), looser
    /// than battery history's, because the value is labelled with its age and replaced by the next reading. It never feeds
    /// alerts, automation or battery history. A level the transport merely republished carries its old time, so it is not newer.
    /// It writes only the saved file (used at the next restart), never the transport's fallback.
    /// </summary>
    private void RememberCase(DeviceState state, string? deviceId, IdentityConfidence identity)
    {
        if (_lastCaseMemory is null || state.IsDemo || state.Origin != DataOrigin.Broadcast) return;
        if (string.IsNullOrEmpty(deviceId) || !identity.IsLikely()) return;
        var level = state.Battery.Case.Level;
        if (!level.HasValue || level.Value is not int percent || level.ObservedAt is not { } at || at > DateTimeOffset.UtcNow) return;
        var stored = _lastCaseMemory.Get(deviceId);
        if (!_lastCaseMemory.Observe(deviceId, percent, at)) return;
        // A new or changed level is rare: write at once. With the lid open the time advances several times a second at the
        // same percent: keep that in memory and let the periodic flush (and Quit) write it, so the disk is not hit each time.
        if (stored is null || stored.Percent != percent) _lastCaseMemory.Flush();
        else if (DateTimeOffset.UtcNow - _lastFlush >= FlushEvery) FlushBatteryData();
        // Deliberately nothing is written back to the transport: this runs queued on the UI thread, possibly after the
        // transport dropped its copy because of ambiguity, and would bring it back. The transport carries the newest level
        // across Stop/Start itself. This only feeds the saved file, which is read at the next restart.
    }

    private void ObserveLastSeen()
    {
        if (_lastSeen is null) return;
        var deviceId = _settings.AirPodsDeviceId;
        if (string.IsNullOrEmpty(deviceId)) return;
        if (MatchedSource() is { } source) _lastSeen.Observe(deviceId, source.Identity, source.Rssi, source.LastHeardAt);
    }

    private void FlushBatteryData()
    {
        _lastFlush = DateTimeOffset.UtcNow;
        _history?.Flush();
        _lastSeen?.Flush();
        _lastCaseMemory?.Flush();
    }

    /// <summary>
    /// "Clear history": forgets every recorded reading, last-seen time and remembered case level, for all devices,
    /// and writes the now-empty files at once. Call on the UI thread (the data is owned by it).
    /// </summary>
    public void ClearBatteryData()
    {
        _history?.Clear();
        _lastSeen?.Clear();
        _lastCaseMemory?.Clear();
        // The transport also holds the case level for the current selection; drop that copy too.
        _broadcast?.SetRememberedCase(null, null);
        FlushBatteryData();
        LocalLog.Write("Battery history cleared by the user.");
    }

    /// <summary>Recorded readings for the selected AirPods (empty when none).</summary>
    public IReadOnlyList<BatterySample> BatterySamples()
        => AppleDeviceHub.App.Battery.DemoBatteryHistory.Enabled
            ? (AppleDeviceHub.App.Battery.DemoBatteryHistory.Sparse
                ? AppleDeviceHub.App.Battery.DemoBatteryHistory.GenerateSparse(DateTimeOffset.Now)
                : AppleDeviceHub.App.Battery.DemoBatteryHistory.Generate(DateTimeOffset.Now))
            : _history is null || string.IsNullOrEmpty(_settings.AirPodsDeviceId)
            ? Array.Empty<BatterySample>()
            : _history.Samples(_settings.AirPodsDeviceId);

    /// <summary>When a likely match for the selected AirPods was last heard, or null.</summary>
    public DateTimeOffset? LastHeardAt()
        => _lastSeen is null || string.IsNullOrEmpty(_settings.AirPodsDeviceId) ? null : _lastSeen.Get(_settings.AirPodsDeviceId)?.Time;

    /// <summary>Updates the flyout's "Last heard ..." line: shown only while Windows has the AirPods disconnected.</summary>
    public void RefreshLastHeard()
    {
        if (_flyout is null || _disposed) return;
        string? text = null;
        var deviceId = _settings.AirPodsDeviceId;
        if (_lastSeen is not null && !string.IsNullOrEmpty(deviceId) && _observation?.Connected != true)
            text = _lastSeen.Text(deviceId, DateTimeOffset.UtcNow);
        _flyout.SetLastHeardText(text);
    }

    private void OnFlyoutBatteryHistoryRequested(object? sender, EventArgs e) => BatteryHistoryRequested?.Invoke(this, EventArgs.Empty);

    // ----- P4-A: the silhouette on the tray icon, tinted by battery level -----

    /// <summary>
    /// The state the tray number may use: never demo data, never a nearby estimate unless it is
    /// likely the selected AirPods, never an old reading. Null means "show no number".
    /// </summary>
    private DeviceState? TrayState()
    {
        var state = _transport.Current;
        if (state is null || state.IsDemo) return null;
        if (state.Origin == DataOrigin.Broadcast)
            if (_broadcast is not { Phase: BroadcastPhase.Fresh or BroadcastPhase.Stale } || !_broadcast.IdentityConfidence.IsLikely()) return null;
        return TrayReadingExpiresAt(state) is { } expiresAt && DateTimeOffset.UtcNow <= expiresAt ? state : null;
    }

    private static DateTimeOffset? TrayReadingExpiresAt(DeviceState? state)
    {
        if (state is null) return null;
        DateTimeOffset? earliest = null;
        foreach (var part in new[] { state.Battery.Left, state.Battery.Right })
        {
            if (!part.Level.HasValue) continue;
            if (part.Level.ObservedAt is not { } at) return null;
            if (earliest is null || at < earliest) earliest = at;
        }
        return earliest?.Add(MaxTrayReadingAge);
    }

    /// <summary>Redraws the tray icon and tooltip when what they show changed.</summary>
    public void UpdateTray(bool force = false)
    {
        if (_tray is not { } tray || _disposed) return;
        var state = TrayState();
        // Demo mode only: the demo state drives the silhouette and tint (screenshots). Never history or alerts.
        var demo = _transport.Current is { IsDemo: true } d ? d : null;
        var outputIsAirPods = demo is not null || _observation?.EndpointIsDefault == true;
        if (demo is not null) state = demo;
        // Per-earbud nearby readings win; otherwise Windows' own reading while the AirPods are connected here.
        var badge = state is null && _connection?.WindowsBatteryPercent is int windowsPercent
            ? TrayBadgeModel.FromWindowsBattery(windowsPercent, outputIsAirPods, _settings.ShowTrayBatteryPercentage, _settings.DimTrayIconWhenAudioElsewhere, _connection.SelectedDeviceNoun)
            : TrayBadgeModel.From(state, outputIsAirPods, _settings.ShowTrayBatteryPercentage, _settings.DimTrayIconWhenAudioElsewhere);
        // The silhouette comes only from the selected Windows-paired device, never from broadcasts (those are spoofable).
        var device = _connection?.SelectedDevice;
        var glyph = demo is not null
            ? TrayGlyphModel.ForDemo(demo.ModelId, demo.Link.Summarize() == ConnectionSummary.Connected, _settings.DimTrayIconWhenAudioElsewhere)
            : TrayGlyphModel.For(
                device is null ? null : AppleBluetoothIdentity.TryParseModelId(device.Id) ?? device.ModelId,
                device is not null,
                device?.IsConnected == true,
                outputIsAirPods,
                _settings.DimTrayIconWhenAudioElsewhere);
        var tint = TrayGlyphModel.TintFor(badge.Level, badge.Charging, _settings.LowBatteryThresholdPercent, _settings.ShowTrayBatteryPercentage);
        badge = badge with { Glyph = glyph with { Dimmed = glyph.Dimmed || badge.Dimmed }, Tint = tint };
        _trayBadgeExpiresAt = state is not null && demo is null ? TrayReadingExpiresAt(state) : null;
        if (!force && badge == _lastBadge)
        {
            EnsureTick();
            return;
        }
        _lastBadge = badge;

        if (badge.Glyph is not { Group: not null } drawn)
        {
            // No device selected: the packaged themed Clasp logo.
            tray.ResetIcon();
        }
        else
        {
            var icon = TrayIconRenderer.Render(drawn, badge.Tint, NativeMethods.GetDpiForSystem());
            if (icon != 0) tray.SetIcon(icon); // the tray icon takes ownership of the handle
            else tray.ResetIcon();
        }
        RefreshTooltip();
        EnsureTick();
    }

    /// <summary>The tooltip: the badge text (exact percentage) while a level is known, else Clasp's usual line.</summary>
    public void RefreshTooltip()
    {
        if (_tray is not { } tray || _disposed) return;
        tray.SetTooltip(_lastBadge is { Level: not null } badge ? badge.Tooltip : _baseTooltip());
    }

    private void OnTrayThemeChanged(object? sender, EventArgs e) => Post(() => UpdateTray(force: true));

    private void DisposeAwareness()
    {
        FlushBatteryData();
        Toasts.Shutdown();
    }
}
