using AppleDeviceHub.App.Automation;
using AppleDeviceHub.App.Connection;
using AppleDeviceHub.App.Media;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.App.Companion;

// P2-A connection-triggered actions, P2-B ear detection from nearby broadcasts and the ear
// output switch, P2-E Fix connection.
internal sealed partial class CompanionHost
{
    private static readonly TimeSpan RestorationWindow = TimeSpan.FromSeconds(10);

    // P2-A
    private readonly ConnectionActionsPolicy _actionsPolicy = new();
    private ConnectionActionsRunner? _actionsRunner;
    private IPlaybackController? _hostPlayback;
    private Task<IPlaybackController?>? _hostPlaybackTask;
    private int _playbackGeneration;
    private DateTimeOffset? _actionsRecheckAt;
    private bool _firstObservationForDevice = true;
    private string? _outputBeforeUserConnect;

    // Who is dispatching the command that is running right now (a hotkey or an allowed external request).
    // The connect attempt itself owns its origin (CompanionConnectionViewModel / ConnectionAttemptTracker);
    // this only tells the attempt which explicit origin to start with.
    private ConnectionOrigin _commandOrigin = ConnectionOrigin.None;

    // P2-B
    private readonly EarOutputSwitchPolicy _earSwitch = new();
    private EarMediaCoordinator? _earMedia;
    private IMediaSessionController? _earMediaController;
    private int _earGeneration;
    private string _earSwitchSignature = "";

    // P2-E
    private RepairSequence? _repair;
    private CancellationTokenSource? _repairCancellation;

    // ----- P2-A: connection-triggered actions -----

    private ConnectionActionSettings CurrentActionSettings() => new(
        _settings.AutoPlayOnConnect,
        _settings.AutoPauseOnDisconnect,
        _settings.DefaultVolumeOnConnect,
        _settings.SwitchOutputToAirPodsOnConnect);

    /// <summary>Remembers the output in use when the user clicks Connect, so Disconnect can return to it.</summary>
    private void OnUserConnectRequested(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var current = _audioVm?.CurrentWindowsOutputId;
        var airPodsEndpoint = _observation?.AudioEndpointId;
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, airPodsEndpoint, StringComparison.OrdinalIgnoreCase))
            _outputBeforeUserConnect = current;
    }

    /// <summary>The origin of the connect attempt that produced this connection (None when Clasp did not ask for it).</summary>
    private ConnectionOrigin CurrentConnectionOrigin() => _connection?.CurrentConnectionOrigin ?? ConnectionOrigin.None;

    private void ClearConnectionOrigin() => _connection?.ClearConnectionOrigin();

    /// <summary>
    /// After the user disconnects the AirPods, return audio to the output used before Connect when it
    /// is still available. Otherwise Windows has already moved to its own default output.
    /// </summary>
    private void OnUserDisconnectConfirmed(object? sender, EventArgs e)
    {
        ClearConnectionOrigin();
        var previous = _outputBeforeUserConnect;
        _outputBeforeUserConnect = null;
        if (_disposed || _audio is null || string.IsNullOrWhiteSpace(previous)) return;
        try
        {
            var snapshot = _audio.Read();
            var endpoint = snapshot.Endpoints.FirstOrDefault(item => string.Equals(item.Id, previous, StringComparison.OrdinalIgnoreCase));
            if (endpoint is not { IsAvailable: true })
            {
                LocalLog.Write("Disconnected; the previous output is gone, so Windows' default output stays.");
                return;
            }
            var result = _audio.TrySetDefaultOutput(endpoint.Id);
            LocalLog.Write(result.Applied
                ? "Disconnected; audio returned to the output used before Connect."
                : $"Disconnected; the previous output could not be restored ({result.Detail}).");
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Disconnected; restoring the previous output failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Evaluates the policy with the latest observation. The very first observation for a device
    /// (or after sleep) is only a baseline, so AirPods that are already connected when Clasp starts
    /// never trigger anything. Settings changes alone never call this: there is no transition.
    /// </summary>
    private void EvaluateConnectionActions(bool isTick)
    {
        if (_disposed || _observation is not { } observation) return;
        var now = DateTimeOffset.UtcNow;
        var restoring = _audioVm?.LastRestoreRequestedAt is { } restoredAt && now - restoredAt < RestorationWindow;
        var origin = CurrentConnectionOrigin();
        var context = new ConnectionActionContext(
            IsAppStart: _firstObservationForDevice,
            MediaPlaying: _hostPlayback?.Current.Playback == MediaPlayback.Playing,
            RestorationInProgress: restoring,
            LastManualOutputChoiceAt: _audioVm?.LastManualOutputChoiceAt,
            Origin: origin);
        _firstObservationForDevice = false;

        var plan = _actionsPolicy.Evaluate(observation, CurrentActionSettings(), context, now);
        _actionsRecheckAt = plan.RecheckAt;
        // A request covers one connect: once the AirPods are connected and the policy is no longer
        // waiting for their output, a later drop and reconnect is automatic again.
        if (origin != ConnectionOrigin.None && observation.Connected && plan.RecheckAt is null) _connection?.ConsumeConnectionOrigin();
        if (plan.Actions.Count == 0) return;
        LocalLog.Write($"Connection actions: {plan.Reason}");
        _ = RunPlanAsync(plan, _connectionGeneration);
    }

    private async Task RunPlanAsync(ConnectionActionPlan plan, int generation)
    {
        try
        {
            var pause = plan.Actions.Any(action => action.Kind == ConnectionActionKind.Pause);
            bool IsStillApplicable() => !_disposed && generation == _connectionGeneration
                && (pause || _observation?.Connected == true);
            _actionsRunner ??= new ConnectionActionsRunner(_audio, new PlaybackProxy(this, static () => true), RunOnAudioThread, LocalLog.Write);
            await _actionsRunner.RunAsync(
                plan,
                IsStillApplicable,
                playbackController: new PlaybackProxy(this, IsStillApplicable));
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Connection actions failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Runs an audio-controller call on the UI thread, which owns the controller.</summary>
    private Task<AudioOutputWriteResult> RunOnAudioThread(Func<AudioOutputWriteResult> work)
    {
        var completion = new TaskCompletionSource<AudioOutputWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = !_disposed && _queue.TryEnqueue(() =>
        {
            try { completion.TrySetResult(work()); }
            catch (Exception ex) { completion.TrySetResult(new AudioOutputWriteResult(false, $"Windows audio call failed ({ex.GetType().Name}).")); }
        });
        if (!queued) completion.TrySetResult(new AudioOutputWriteResult(false, "Clasp is closing; the audio change was skipped."));
        return completion.Task;
    }

    /// <summary>The Windows media player is only connected while a setting (or a command) needs it.</summary>
    private void ApplyPlaybackNeed()
    {
        if (_disposed) return;
        if (_settings.AutoPlayOnConnect || _settings.AutoPauseOnDisconnect) _ = GetPlaybackAsync();
        else _ = ReleasePlaybackAsync();
    }

    private Task<IPlaybackController?> GetPlaybackAsync()
    {
        if (_disposed || _viewModel.IsDemo) return Task.FromResult<IPlaybackController?>(null);
        return _hostPlaybackTask ??= CreatePlaybackAsync(++_playbackGeneration);
    }

    private async Task<IPlaybackController?> CreatePlaybackAsync(int generation)
    {
        IPlaybackController? controller = null;
        try { controller = await WindowsPlaybackController.TryCreateAsync(LocalLog.Write); }
        catch (Exception ex) { LocalLog.Write($"Windows media controls unavailable ({ex.GetType().Name})."); }
        if (_disposed || generation != _playbackGeneration)
        {
            if (controller is not null) await controller.DisposeAsync();
            return null;
        }
        _hostPlayback = controller;
        return controller;
    }

    private async Task ReleasePlaybackAsync()
    {
        _playbackGeneration++;
        var task = _hostPlaybackTask;
        _hostPlaybackTask = null;
        var controller = _hostPlayback;
        _hostPlayback = null;
        if (task is not null) { try { await task; } catch (Exception) { /* creation failures are already logged */ } }
        if (controller is not null) await controller.DisposeAsync();
    }

    /// <summary>Forwards to the host's lazily created Windows media controller.</summary>
    private sealed class PlaybackProxy(CompanionHost host, Func<bool> isStillApplicable) : IPlaybackController
    {
        public PlaybackSnapshot Current => host._hostPlayback?.Current ?? PlaybackSnapshot.Unavailable;

        public event EventHandler<PlaybackSnapshot>? Changed { add { } remove { } }

        public Task<PlaybackCommandResult> PlayAsync(CancellationToken cancellationToken = default)
            => SendAsync(controller => controller.PlayAsync(cancellationToken));

        public Task<PlaybackCommandResult> PauseAsync(CancellationToken cancellationToken = default)
            => SendAsync(controller => controller.PauseAsync(cancellationToken));

        public async Task<PlaybackCommandResult> NextAsync(CancellationToken cancellationToken = default)
            => await host.GetPlaybackAsync() is { } controller
                ? await controller.NextAsync(cancellationToken)
                : Unavailable;

        public async Task<PlaybackCommandResult> PreviousAsync(CancellationToken cancellationToken = default)
            => await host.GetPlaybackAsync() is { } controller
                ? await controller.PreviousAsync(cancellationToken)
                : Unavailable;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<PlaybackCommandResult> SendAsync(Func<IPlaybackController, Task<PlaybackCommandResult>> send)
        {
            if (!IsStillApplicable()) return Skipped;
            var controller = await host.GetPlaybackAsync();
            if (!IsStillApplicable()) return Skipped;
            return controller is null ? Unavailable : await send(controller);
        }

        private bool IsStillApplicable()
        {
            try { return !host._disposed && isStillApplicable(); }
            catch (Exception) { return false; }
        }

        private static PlaybackCommandResult Unavailable => new(PlaybackCommandStatus.Unavailable, "Windows media controls are unavailable.");
        private static PlaybackCommandResult Skipped => new(PlaybackCommandStatus.Unavailable, "The connection changed before playback could be requested.");
    }

    private async Task DisposeConnectionActionsAsync()
    {
        _actionsRunner?.Dispose();
        _actionsRunner = null;
        await ReleasePlaybackAsync();
    }

    // ----- P2-B: ear detection from nearby broadcasts (opt-in) -----

    private bool NeedsEarCoordinator
        => _settings.PauseMediaOnEarRemoval
            || (_settings.BroadcastEarDetection && !string.IsNullOrEmpty(_settings.EarRemovedOutputEndpointId));

    /// <summary>
    /// The ear coordinator exists only while the pause preference or the ear output switch needs it;
    /// turning both off disposes it, which also forgets any pending resume. The broadcast ear rule
    /// (identity, fresh, stable) lives in the coordinator and is off unless "Ear detection" is on.
    /// </summary>
    private async Task ApplyEarMediaSettingAsync()
    {
        var generation = ++_earGeneration;
        if (_disposed) return;
        if (!NeedsEarCoordinator || _transport is null)
        {
            if (!_settings.PauseMediaOnEarRemoval)
                LocalLog.Write($"Ear-detection media control off (setting disabled; transport origin {_transport?.Info.Origin.ToString() ?? "unavailable"}).");
            await StopEarMediaAsync();
            return;
        }
        if (_earMedia is not null && ((_earMediaController is not null) == _settings.PauseMediaOnEarRemoval))
        {
            ConfigureEarCoordinator(_earMedia);
            ConfigureEarOutputSwitch(force: true);
            return;
        }
        if (_earMedia is not null) await StopEarMediaAsync();

        // The speaker-switch feature uses only stable ear evidence and Windows output APIs. Do
        // not create an SMTC session when the user only wants that feature.
        var wantsMediaControl = _settings.PauseMediaOnEarRemoval;
        var media = wantsMediaControl ? await SmtcMediaSessionController.TryCreateAsync() : null;
        if (_disposed || generation != _earGeneration || !NeedsEarCoordinator || _earMedia is not null)
        {
            if (media is not null) await media.DisposeAsync(); // superseded by a later change
            return;
        }
        var needsOutputSwitch = _settings.BroadcastEarDetection && !string.IsNullOrWhiteSpace(_settings.EarRemovedOutputEndpointId);
        if (media is null && wantsMediaControl)
        {
            LocalLog.Write("Media control unavailable: Windows did not provide media sessions.");
            if (!needsOutputSwitch) return;
        }
        _earMediaController = media;
        var coordinator = new EarMediaCoordinator(new EarMediaPolicy { Enabled = _settings.PauseMediaOnEarRemoval }, media, diagnosticLog: LocalLog.Write);
        _earMedia = coordinator;
        coordinator.Attach(_transport);
        coordinator.BroadcastEarEvaluated += OnBroadcastEarEvaluated;
        ConfigureEarCoordinator(coordinator);
        ConfigureEarOutputSwitch(force: true);
        LocalLog.Write(media is null && wantsMediaControl
            ? "Nearby ear detection remains available for the output switch; media control is unavailable."
            : _settings.BroadcastEarDetection
            ? "Ear-detection media control on (hardware ear data, plus nearby estimates for the selected AirPods when identity is likely)."
            : "Ear-detection media control on (acts only on hardware ear data).");
    }

    private void ConfigureEarCoordinator(EarMediaCoordinator coordinator)
    {
        coordinator.Enabled = _settings.PauseMediaOnEarRemoval;
        coordinator.BroadcastIdentity = () => _broadcast?.IdentityConfidence ?? IdentityConfidence.Unknown;
        coordinator.WindowsConnected = () => _observation?.Connected == true;
        coordinator.BroadcastEarDetection = _settings.BroadcastEarDetection;
    }

    private async Task StopEarMediaAsync()
    {
        var coordinator = _earMedia;
        var media = _earMediaController;
        _earMedia = null;
        _earMediaController = null;
        _earSwitch.Configure("", null, null);
        _earSwitchSignature = "";
        if (coordinator is not null)
        {
            coordinator.BroadcastEarEvaluated -= OnBroadcastEarEvaluated;
            await coordinator.DisposeAsync();
        }
        if (media is not null) await media.DisposeAsync();
    }

    /// <summary>The user acted on playback by hand: a pending resume must not override that.</summary>
    public void ForgetEarMedia() => _earMedia?.Forget();

    private async Task DisposeEarMediaAsync()
    {
        _earGeneration++;
        await StopEarMediaAsync();
    }

    /// <summary>
    /// Tells the output-switch policy which endpoints are the selected AirPods and which speaker to
    /// use. Reconfiguring forgets Clasp's claim, so it only happens when one of them actually changed.
    /// </summary>
    private void ConfigureEarOutputSwitch(bool force = false)
    {
        if (_earMedia is null) return;
        var speaker = _settings.BroadcastEarDetection ? _settings.EarRemovedOutputEndpointId : "";
        var device = _connection?.SelectedDevice;
        var container = device?.ContainerId;
        var endpointIds = device is null || _connection is null
            ? Array.Empty<string>()
            : _connection.Snapshot.AudioEndpoints
                .Where(endpoint => string.Equals(endpoint.ContainerId, container, StringComparison.OrdinalIgnoreCase))
                .Select(endpoint => endpoint.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        var signature = $"{speaker}|{container}|{string.Join(',', endpointIds)}";
        if (!force && signature == _earSwitchSignature) return;
        _earSwitchSignature = signature;
        _earSwitch.Configure(speaker, endpointIds, container);
    }

    private void OnBroadcastEarEvaluated(object? sender, BroadcastEarResult result)
    {
        // Raised on whichever thread delivered the broadcast state.
        if (!_disposed) _queue.TryEnqueue(() => ApplyEarOutputSwitch(result));
    }

    private void ObserveEarOutputs()
    {
        if (_audio is null || _earMedia is null) return;
        try { _earSwitch.Observe(_audio.Read()); }
        catch (Exception ex) { LocalLog.Write($"Ear output check failed ({ex.GetType().Name})."); }
    }

    private void ApplyEarOutputSwitch(BroadcastEarResult result)
    {
        if (_disposed || _audio is null || !_settings.BroadcastEarDetection
            || string.IsNullOrEmpty(_settings.EarRemovedOutputEndpointId)) return;
        // Nearby readings can be faked, so a broadcast alone never moves the sound: the user's own AirPods
        // must really be connected in Windows (the gate checks this too; this is the second lock).
        if (_observation?.Connected != true) return;
        try
        {
            var requests = _earSwitch.OnEar(result, _audio.Read(), DateTimeOffset.UtcNow);
            foreach (var request in requests)
            {
                // Read again right before writing: the default may have moved since the evaluation.
                var defaults = _audio.Read().Defaults;
                defaults.TryGetValue(request.Role, out var current);
                if (string.Equals(current, request.EndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    // An earlier request in this batch already moved every role (a Windows API limit).
                    _earSwitch.ReportResult(request.Role, true);
                    continue;
                }
                if (!string.Equals(current, request.ExpectedCurrentEndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    _earSwitch.ReportResult(request.Role, false); // someone else changed it: leave it alone
                    continue;
                }
                var write = _audio.TrySetDefaultOutput(request.EndpointId);
                _earSwitch.ReportResult(request.Role, write.Applied);
                LocalLog.Write($"Ear output switch role={request.Role}; switchBack={request.SwitchBack}; applied={write.Applied}; result={write.Detail}");
            }
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Ear output switch failed ({ex.GetType().Name}).");
        }
    }

    // ----- P2-E: Fix connection -----

    private void InitializeRepair()
    {
        if (_controller is null) return;
        _repair = new RepairSequence(new BluetoothRepair(), _controller);
        _repair.RunningChanged += OnRepairRunningChanged;
        _repair.ProgressChanged += OnRepairProgress;
    }

    private void OnRepairRunningChanged(object? sender, EventArgs e) => Post(() => Status.RepairIsRunning = _repair?.IsRunning == true);

    private void OnRepairProgress(object? sender, RepairProgress progress) => Post(() =>
    {
        Status.RepairSucceeded = false;
        Status.RepairText = progress.Message;
    });

    /// <summary>
    /// Runs the one-click Bluetooth repair for the selected AirPods. Asked for by the user only;
    /// it never repeats. Returns the plain message that is also shown on the settings page.
    /// </summary>
    public async Task<string> RunRepairAsync()
    {
        if (_disposed || _repair is null || _connection?.SelectedDevice is not { } device)
        {
            const string choose = "Choose your AirPods in Settings first.";
            Status.RepairSucceeded = false;
            Status.RepairText = choose;
            return choose;
        }
        if (_repair.IsRunning) return Status.RepairText;

        // The radio cycle drops the AirPods on purpose: that is not a hand-off, and the
        // reconnect afterwards must not trigger "play on connect".
        _handoff.NoteUserDisconnect(DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        _repairCancellation = cancellation;
        LocalLog.Write("Fix connection started.");
        RepairResult result;
        try
        {
            result = await _repair.RunAsync(device.Id, device.ContainerId, cancellation.Token);
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Fix connection failed ({ex.GetType().Name}).");
            result = new RepairResult(RepairOutcome.Failed, "Fix connection stopped. Make sure Bluetooth is on in Windows Settings.");
        }
        finally
        {
            _repairCancellation = null;
        }

        LocalLog.Write($"Fix connection finished: {result.Outcome}.");
        if (!_disposed)
        {
            Status.RepairSucceeded = result.IsSuccess;
            Status.RepairText = result.Message;
            Status.RepairIsRunning = _repair.IsRunning;
            // Start from a clean baseline: the reconnect after a repair is not a "real" connect.
            _actionsPolicy.Reset();
            ClearConnectionOrigin();
            _firstObservationForDevice = true;
            _actionsRecheckAt = null;
            _handoff.Reset();
            _ = _connection.RefreshAsync();
            EnsureTick();
        }
        return result.Message;
    }

    /// <summary>Cancels a running repair (the radio is turned back on if it had been switched off).</summary>
    public void CancelRepair()
    {
        try { _repairCancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* finished while the button was pressed */ }
    }

    private void DisposeRepair()
    {
        CancelRepair();
        if (_repair is null) return;
        _repair.RunningChanged -= OnRepairRunningChanged;
        _repair.ProgressChanged -= OnRepairProgress;
        _repair = null;
    }
}
