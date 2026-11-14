using AppleDeviceHub.App.Audio;
using AppleDeviceHub.App.Media;
using AppleDeviceHub.App.Shell;
using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Media;
using AppleDeviceHub.Core.Startup;

namespace AppleDeviceHub.App.Companion;

// P4-C stem actions, P4-D per-app output (both experimental, off by default), P4-E run on
// startup and phone audio.
internal sealed partial class CompanionHost
{
    // P4-C
    private readonly StemActionPolicy _stemPolicy = new();
    private StemActionHook? _stemHook;
    private volatile StemContextBox _stemContext = new(StemContext.Inactive);

    // P4-D
    private PerAppRouting? _perApp;
    private PerAppRoutingCoordinator? _perAppCoordinator;
    private AudioSessionWatcher? _sessionWatcher;
    private string? _perAppEndpointId;

    // P4-E
    private PhoneStreaming? _phone;

    /// <summary>The hook thread reads this without a lock; replacing the whole object is atomic.</summary>
    private sealed class StemContextBox(StemContext value)
    {
        public StemContext Value { get; } = value;
    }

    // ----- P4-C: stem actions -----

    /// <summary>Nothing is hooked unless "Stem actions" is on; turning it off removes the hook.</summary>
    private void ApplyStemSetting()
    {
        if (_disposed) return;
        _stemPolicy.Configure(new StemActionSettings(
            _settings.StemActionsEnabled,
            _settings.StemPlayPauseAction,
            _settings.StemNextAction,
            _settings.StemPreviousAction));

        if (!_settings.StemActionsEnabled)
        {
            DisposeStem();
        }
        else if (_stemHook is null)
        {
            UpdateStemContext();
            var hook = new StemActionHook(_stemPolicy, () => _stemContext.Value, LocalLog.Write);
            if (hook.Start())
            {
                _stemHook = hook;
                LocalLog.Write("Stem actions on.");
            }
            else
            {
                hook.Dispose();
                LocalLog.Write("Stem actions could not start: Windows refused the keyboard hook.");
            }
        }
        RefreshStatusTexts();
    }

    /// <summary>A cheap snapshot for the hook thread: are the AirPods connected and the sound output?</summary>
    private void UpdateStemContext()
    {
        var observation = _observation;
        _stemContext = new StemContextBox(new StemContext(
            AirPodsConnected: observation?.Connected == true,
            OutputIsAirPods: observation?.EndpointIsDefault == true));
    }

    private void DisposeStem()
    {
        var hook = _stemHook;
        _stemHook = null;
        hook?.Dispose();
    }

    /// <summary>Refreshes the lines that depend on live counters. Called when the settings page opens.</summary>
    public void RefreshStatusTexts()
    {
        if (!_settings.StemActionsEnabled)
        {
            Status.StemText = "";
        }
        else if (_stemHook is not { } hook)
        {
            Status.StemText = "Clasp can't watch for stem presses, so stem actions are off.";
        }
        else
        {
            Status.StemText = hook.MediaKeysSeen == 0
                ? "Waiting for a stem press. If this doesn't change when you press the stem, stem actions may not work on this PC."
                : $"Heard {hook.MediaKeysSeen} stem press{(hook.MediaKeysSeen == 1 ? "" : "es")} since this was turned on.";
        }
    }

    // ----- P4-D: per-app output -----

    /// <summary>
    /// Checks (read-only) whether Windows offers the per-app output interface Clasp knows. Called
    /// when the settings page opens; it changes nothing on the system.
    /// </summary>
    public void CheckPerAppSupport()
    {
        if (_disposed) return;
        try
        {
            _perApp ??= new PerAppRouting();
            var supported = _perApp.IsSupported;
            Status.PerAppSupported = supported;
            Status.PerAppSupportText = supported ? "" : _perApp.SupportDetail;
        }
        catch (Exception ex)
        {
            Status.PerAppSupported = false;
            Status.PerAppSupportText = "Couldn't check whether this PC supports per-app output.";
            LocalLog.Write($"Per-app output check failed ({ex.GetType().Name}).");
        }
    }

    private void ApplyPerAppSetting(bool initial)
    {
        if (_disposed) return;
        if (!_settings.PerAppOutputEnabled)
        {
            // Turning it off returns every app Clasp routed to the Windows default.
            if (_perAppCoordinator is not null) LogPerApp(_perAppCoordinator.Configure(false, [], null));
            StopSessionWatcher();
            _perAppEndpointId = null;
            return;
        }

        try
        {
            _perApp ??= new PerAppRouting();
            if (!_perApp.IsSupported)
            {
                Status.PerAppSupported = false;
                Status.PerAppSupportText = _perApp.SupportDetail;
                LocalLog.Write($"Per-app output is on but not supported here: {_perApp.SupportDetail}");
                return;
            }
            Status.PerAppSupported = true;
            Status.PerAppSupportText = "";

            _perAppCoordinator ??= new PerAppRoutingCoordinator(new PerAppRoutingPolicy(new RunningProcessSource()), _perApp);
            var rules = PerAppRuleText.Parse(_settings.PerAppRulesText, out _);
            _perAppEndpointId = CurrentAirPodsEndpointId();
            LogPerApp(_perAppCoordinator.Configure(true, rules, _perAppEndpointId));

            if (_sessionWatcher is null)
            {
                _sessionWatcher = new AudioSessionWatcher();
                _sessionWatcher.SessionStarted += OnAudioSessionStarted;
            }
            _sessionWatcher.Refresh();
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Per-app output failed to start ({ex.GetType().Name}).");
        }
    }

    private string? CurrentAirPodsEndpointId()
        => _observation is { HasUsableEndpoint: true } observation ? observation.AudioEndpointId : null;

    /// <summary>The AirPods render endpoint appeared, changed or went away.</summary>
    private void UpdatePerAppEndpoint()
    {
        if (_perAppCoordinator is null || !_settings.PerAppOutputEnabled) return;
        var endpointId = CurrentAirPodsEndpointId();
        if (string.Equals(endpointId, _perAppEndpointId, StringComparison.Ordinal)) return;
        _perAppEndpointId = endpointId;
        LogPerApp(_perAppCoordinator.OnAirPodsChanged(endpointId));
        _sessionWatcher?.Refresh(); // endpoints changed: watch the current set
    }

    private void OnAudioSessionStarted(int processId)
    {
        // Raised on any thread; the policy and the COM calls stay on the UI thread.
        Post(() =>
        {
            if (_perAppCoordinator is null || !_settings.PerAppOutputEnabled) return;
            if (RunningProcessSource.Find(processId) is { } process)
                LogPerApp(_perAppCoordinator.OnProcessStarted(process));
        });
    }

    private static void LogPerApp(IReadOnlyList<PerAppActionOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
            if (!outcome.Result.Succeeded)
                LocalLog.Write($"Per-app output {outcome.Action.Kind} process={outcome.Action.ProcessId}: {outcome.Result.Status} {outcome.Result.Detail}");
    }

    private void StopSessionWatcher()
    {
        var watcher = _sessionWatcher;
        _sessionWatcher = null;
        if (watcher is null) return;
        watcher.SessionStarted -= OnAudioSessionStarted;
        watcher.Dispose();
    }

    private void DisposePerApp()
    {
        // Clear first, so no app stays routed to an output that is going away, then release.
        try
        {
            if (_perAppCoordinator is not null) LogPerApp(_perAppCoordinator.Configure(false, [], null));
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Per-app output cleanup failed ({ex.GetType().Name}).");
        }
        _perAppCoordinator = null;
        StopSessionWatcher();
        _perApp?.Dispose();
        _perApp = null;
    }

    // ----- P4-E: run on startup (the registry is the truth, nothing is stored in settings) -----

    /// <summary>Reads the sign-in entry and Task Manager's switch live; null when the registry could not be read.</summary>
    public static StartupStatus? GetStartupStatus() => StartupRegistration.GetStatus();

    /// <summary>Adds or removes the per-user sign-in entry. Only ever called from the user's toggle.</summary>
    public static StartupChangeResult SetRunOnStartup(bool enable)
    {
        if (!enable) return StartupRegistration.Disable();
        var exe = Environment.ProcessPath;
        return string.IsNullOrEmpty(exe)
            ? StartupMessages.Failed(StartupRegistration.GetStatus() ?? StartupStatus.Off)
            : StartupRegistration.Enable(exe);
    }

    // ----- P4-E: phone audio (nothing is created until the settings page needs it) -----

    /// <summary>Lists phones Windows says can stream audio to this PC (read-only). The search reports failure instead of throwing.</summary>
    public static Task<PhoneDeviceSearch> FindPhonesAsync()
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? FindPhonesCore() : Task.FromResult(new PhoneDeviceSearch([], false));

    /// <summary>The state of the phone stream as last published.</summary>
    public PhoneStreamingState PhoneState => Status.PhoneState;

    /// <summary>Starts streaming from the chosen phone (closing any earlier stream first).</summary>
    public Task StartPhoneAsync(string deviceId, string? deviceName)
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? StartPhoneCore(deviceId, deviceName) : Task.CompletedTask;

    /// <summary>Stops the phone stream. Stopping when nothing is open is not an error.</summary>
    public Task StopPhoneAsync()
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? StopPhoneCore() : Task.CompletedTask;

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private static Task<PhoneDeviceSearch> FindPhonesCore() => PhoneStreaming.FindCandidatesAsync();

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private async Task StartPhoneCore(string deviceId, string? deviceName)
    {
        if (_disposed) return;
        await EnsurePhone().StartAsync(deviceId, deviceName);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private async Task StopPhoneCore()
    {
        if (_phone is null) return;
        await _phone.StopAsync();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private PhoneStreaming EnsurePhone()
    {
        if (_phone is null)
        {
            _phone = new PhoneStreaming();
            _phone.StateChanged += OnPhoneStateChanged;
            Status.PhoneState = _phone.State;
        }
        return _phone;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private void OnPhoneStateChanged(object? sender, PhoneStreamingState state) => Post(() => Status.PhoneState = state);

    private void DisposePhone()
    {
        if (_phone is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        DisposePhoneCore();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private void DisposePhoneCore()
    {
        var phone = _phone;
        _phone = null;
        if (phone is null) return;
        phone.StateChanged -= OnPhoneStateChanged;
        phone.Dispose(); // closes an open phone connection
    }
}
