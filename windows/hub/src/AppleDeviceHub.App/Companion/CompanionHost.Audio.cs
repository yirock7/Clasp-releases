using AppleDeviceHub.App.Audio;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Connection;

namespace AppleDeviceHub.App.Companion;

// P2-C Handsfree mode (including Smart), P2-D low audio latency.
internal sealed partial class CompanionHost
{
    // P2-C
    private readonly HandsfreePolicy _hfPolicy = new();
    private MicrophoneUsageWatcher? _microphone;
    private DateTimeOffset? _hfRecheckAt;
    private bool _hfBusy;
    private bool _hfDirty;
    private HandsfreeMode _hfLastMode = HandsfreeMode.AlwaysOn;

    // P2-D
    private readonly KeepAlivePolicy _kaPolicy = new();
    private KeepAliveRenderer? _keepAlive;
    private DateTimeOffset? _kaRetryAt;
    private string? _kaEndpointSeenId;      // endpoint the timestamp below belongs to (never logged)
    private long _kaEndpointSeenAt;         // Stopwatch timestamp when that endpoint was first usable

    // ----- P2-C: Handsfree -----

    /// <summary>
    /// "Always on" is Windows' own behaviour: Clasp changes nothing and does not listen to the
    /// microphone. The microphone watcher exists only in Smart mode. Clasp does not restore
    /// Handsfree when it exits; choosing "Always on" again does.
    /// </summary>
    private void ApplyHandsfreeSetting()
    {
        if (_disposed) return;
        var mode = _settings.HandsfreeMode;
        if (mode != _hfLastMode)
        {
            _hfLastMode = mode;
            _hfPolicy.ResetBlock(); // the user changed their mind: allow one fresh try
        }

        if (mode == HandsfreeMode.Smart)
        {
            if (_microphone is null)
            {
                _microphone = new MicrophoneUsageWatcher();
                _microphone.Changed += OnMicrophoneUsageChanged;
                _microphone.ReadinessChanged += OnMicrophoneUsageReadinessChanged;
                _microphone.Start();
            }
        }
        else
        {
            StopMicrophoneWatcher();
        }
        EvaluateHandsfree();
    }

    private void OnMicrophoneUsageChanged(object? sender, MicrophoneUsageChangedEventArgs e)
        => Post(EvaluateHandsfree); // raised on a background thread

    private void OnMicrophoneUsageReadinessChanged(object? sender, EventArgs e)
        => Post(EvaluateHandsfree); // both unknown and recovered reads must be reevaluated

    private void StopMicrophoneWatcher()
    {
        var watcher = _microphone;
        _microphone = null;
        if (watcher is null) return;
        watcher.Changed -= OnMicrophoneUsageChanged;
        watcher.ReadinessChanged -= OnMicrophoneUsageReadinessChanged;
        watcher.Dispose();
    }

    private void EvaluateHandsfree()
    {
        if (_disposed) return;
        if (_hfBusy)
        {
            _hfDirty = true; // look again when the running change finishes
            return;
        }

        var connected = _observation?.Connected == true;
        var microphoneSnapshot = _microphone?.CurrentSnapshot ?? MicrophoneUsageSnapshot.Unknown;
        if (_settings.HandsfreeMode == HandsfreeMode.Smart && connected && !microphoneSnapshot.IsReady)
        {
            _hfRecheckAt = null;
            Status.HandsfreeText = "Checking whether your microphone is in use…";
            return;
        }
        var micInUse = microphoneSnapshot.IsMicrophoneInUse;
        var decision = _hfPolicy.Evaluate(_settings.HandsfreeMode, micInUse, connected, HandsfreeState.Unknown, DateTimeOffset.UtcNow);
        _hfRecheckAt = decision.RecheckAt;
        Status.HandsfreeText = HandsfreeStatusLine(decision);

        if (decision.Action == HandsfreeAction.None) return;
        var address = _connection?.SelectedDevice is { } device ? BluetoothAddressParser.TryParse(device.Id) : null;
        if (address is not ulong bluetoothAddress)
        {
            _hfPolicy.ReportOutcome(decision.Action, HandsfreeOutcome.DeviceNotFound);
            Status.HandsfreeText = "Couldn't find your AirPods to change Handsfree. Clasp left it as it was.";
            LocalLog.Write("Handsfree change skipped: no Bluetooth address in the selected device id.");
            return;
        }
        _ = ApplyHandsfreeActionAsync(decision.Action, bluetoothAddress, _connectionGeneration);
    }

    private async Task ApplyHandsfreeActionAsync(HandsfreeAction action, ulong address, int generation)
    {
        _hfBusy = true;
        HandsfreeResult result;
        try
        {
            LocalLog.Write($"Handsfree {(action == HandsfreeAction.Enable ? "enable" : "disable")} requested ({_settings.HandsfreeMode}).");
            result = await HandsfreeController.SetEnabledAsync(address, action == HandsfreeAction.Enable);
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Handsfree change failed ({ex.GetType().Name}).");
            result = new HandsfreeResult(HandsfreeOutcome.Failed, 0);
        }
        _hfBusy = false;
        if (_disposed) return;
        if (generation != _connectionGeneration)
        {
            if (_hfDirty)
            {
                _hfDirty = false;
                EvaluateHandsfree();
            }
            EnsureTick();
            return;
        }

        _hfPolicy.ReportOutcome(action, result.Outcome);
        LocalLog.Write($"Handsfree result: {result.Outcome} (Win32 {result.Win32Error}).");
        Status.HandsfreeText = result.Outcome switch
        {
            HandsfreeOutcome.AccessDenied => "Windows needs administrator access to change Handsfree. Clasp left it as it was.",
            HandsfreeOutcome.DeviceNotFound => "Windows couldn't find these AirPods. Clasp left Handsfree as it was.",
            HandsfreeOutcome.Failed => $"Windows couldn't change Handsfree (error {result.Win32Error}).",
            _ => Status.HandsfreeText,
        };
        if (_hfDirty || result.Outcome == HandsfreeOutcome.Succeeded)
        {
            _hfDirty = false;
            EvaluateHandsfree();
        }
        EnsureTick();
    }

    private string HandsfreeStatusLine(HandsfreeDecision decision)
    {
        if (_hfPolicy.IsBlocked) return $"{_hfPolicy.BlockedReason} Clasp left it as it was.";
        if (_settings.HandsfreeMode == HandsfreeMode.AlwaysOn) return "";
        return decision.Reason;
    }

    private void DisposeHandsfree() => StopMicrophoneWatcher();

    // ----- P2-D: low audio latency -----

    private void ApplyKeepAliveSetting()
    {
        if (_disposed) return;
        _kaPolicy.Reset();
        EvaluateKeepAlive();
    }

    private void EvaluateKeepAlive()
    {
        if (_disposed) return;
        var observation = _observation;
        // The observation's endpoint is the AirPods render endpoint; it is "the default" when
        // Windows' playback output (Multimedia, else Console) is that same endpoint.
        var endpointId = observation is { HasUsableEndpoint: true } ? observation.AudioEndpointId : null;
        var defaultId = observation is { EndpointIsDefault: true } ? observation.AudioEndpointId : null;
        if (!string.Equals(endpointId, _kaEndpointSeenId, StringComparison.OrdinalIgnoreCase))
        {
            _kaEndpointSeenId = endpointId;
            _kaEndpointSeenAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        var decision = _kaPolicy.Evaluate(_settings.LowAudioLatency, observation?.Connected == true, endpointId, defaultId);
        _kaRetryAt = decision.Reason == KeepAliveReason.BackingOff ? decision.RetryAt : null;

        if (decision.ShouldRun && endpointId is not null)
        {
            if (_keepAlive is null)
            {
                _keepAlive = new KeepAliveRenderer();
                _keepAlive.StateChanged += OnKeepAliveStateChanged;
            }
            var alreadyOnThisEndpoint = _keepAlive.State is KeepAliveRendererState.Starting or KeepAliveRendererState.Running
                && string.Equals(_keepAlive.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase);
            if (!alreadyOnThisEndpoint) _keepAlive.Start(endpointId);
        }
        else
        {
            _keepAlive?.Stop();
            if (!_settings.LowAudioLatency) DisposeKeepAlive();
        }
        Status.LowLatencyText = KeepAliveStatusLine(decision.Reason);
    }

    private static string KeepAliveStatusLine(KeepAliveReason reason) => reason switch
    {
        KeepAliveReason.Running => "On",
        KeepAliveReason.NotConnected => "Waiting for your AirPods to connect",
        KeepAliveReason.NoEndpoint => "Waiting for Windows to show your AirPods",
        KeepAliveReason.NotDefaultOutput => "Paused. Your AirPods aren't the sound output.",
        KeepAliveReason.BackingOff => "Couldn't start. Trying again.",
        KeepAliveReason.RetriesExhausted => "Stopped until your AirPods reconnect",
        _ => "",
    };

    private void OnKeepAliveStateChanged(KeepAliveRendererState state, string detail)
    {
        // Raised on the renderer's worker thread (Stopped on the caller's thread).
        if (state == KeepAliveRendererState.Running)
        {
            // One line per start, so it can be lined up with the "Connect:" lines. No device names.
            Post(() =>
            {
                var sinceEndpoint = _kaEndpointSeenId is null ? "" : $" {System.Diagnostics.Stopwatch.GetElapsedTime(_kaEndpointSeenAt).TotalMilliseconds:0} ms after the sound output appeared";
                LocalLog.Write($"Low audio latency stream started{sinceEndpoint}.");
            });
            return;
        }
        if (state != KeepAliveRendererState.Failed) return;
        Post(() =>
        {
            LocalLog.Write($"Low audio latency stream failed: {detail}");
            _kaPolicy.ReportFailure();
            EvaluateKeepAlive();
            EnsureTick();
        });
    }

    private void DisposeKeepAlive()
    {
        var renderer = _keepAlive;
        _keepAlive = null;
        if (renderer is null) return;
        renderer.StateChanged -= OnKeepAliveStateChanged;
        renderer.Dispose();
    }
}
