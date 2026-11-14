namespace AppleDeviceHub.Core.Connection;

/// <summary>Whether the Windows Bluetooth radio is switched on.</summary>
public enum BluetoothRadioState
{
    Unknown = 0,
    On = 1,
    Off = 2,
}

/// <summary>The outcome of one radio-switch request.</summary>
public enum RadioSwitchOutcome
{
    Success = 0,

    /// <summary>Windows refused radio control (privacy setting or policy).</summary>
    AccessDenied = 1,

    /// <summary>This PC has no Bluetooth radio Clasp can control.</summary>
    NoRadio = 2,

    /// <summary>Windows reported the radio as busy or the request as not currently possible.</summary>
    Busy = 3,

    Failed = 4,
}

/// <summary>Result of <see cref="IBluetoothRadioSwitch.GetStateAsync"/>.</summary>
public sealed record RadioStateResult(RadioSwitchOutcome Outcome, BluetoothRadioState State, string? Detail = null);

/// <summary>Result of <see cref="IBluetoothRadioSwitch.SetStateAsync"/>.</summary>
public sealed record RadioSwitchResult(RadioSwitchOutcome Outcome, string? Detail = null);

/// <summary>
/// Turns the Windows Bluetooth radio on or off. Implementations must not touch audio devices,
/// pairings or any other system setting. Native work must stay off the UI thread.
/// </summary>
public interface IBluetoothRadioSwitch
{
    Task<RadioStateResult> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the radio on or off and returns once Windows reports the new state or gives up.</summary>
    Task<RadioSwitchResult> SetStateAsync(bool turnOn, CancellationToken cancellationToken = default);
}

/// <summary>The visible steps of a repair run, in order.</summary>
public enum RepairStep
{
    TurningOff = 0,
    Settling = 1,
    TurningOn = 2,
    WaitingForDevice = 3,
    Reconnecting = 4,
    Finished = 5,
}

/// <summary>One progress notification for the UI.</summary>
public sealed record RepairProgress(RepairStep Step, string Message);

/// <summary>How a repair run ended.</summary>
public enum RepairOutcome
{
    /// <summary>Bluetooth restarted and the AirPods are connected again.</summary>
    Success = 0,

    /// <summary>Bluetooth restarted but the AirPods did not reconnect.</summary>
    ReconnectFailed = 1,

    /// <summary>Bluetooth restarted but the paired AirPods never reappeared in Windows.</summary>
    DeviceNotReady = 2,

    AccessDenied = 3,
    NoRadio = 4,
    RadioBusy = 5,

    /// <summary>The radio was already off, so Clasp left it alone.</summary>
    RadioWasOff = 6,

    TurnOffTimedOut = 7,
    TurnOnTimedOut = 8,

    /// <summary>Clasp turned Bluetooth off and could not turn it back on.</summary>
    RadioStuckOff = 9,

    Cancelled = 10,

    /// <summary>Another repair was already running; nothing was changed.</summary>
    AlreadyRunning = 11,

    Failed = 12,
}

/// <summary>The final result of a repair run with a plain-language message safe to show as-is.</summary>
public sealed record RepairResult(RepairOutcome Outcome, string Message)
{
    public bool IsSuccess => Outcome == RepairOutcome.Success;
}

/// <summary>Per-step time limits. Defaults follow the product plan.</summary>
public sealed record RepairOptions
{
    public TimeSpan TurnOffTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan TurnOnTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan DeviceReadyTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReconnectVerifyTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// User-initiated Bluetooth repair: radio off, settle, radio on, wait for the paired device,
/// reconnect it once, verify. Never retries by itself, allows one run at a time, and always
/// tries to leave the radio on if it switched it off, even on failure or cancel.
/// </summary>
public sealed class RepairSequence
{
    private readonly IBluetoothRadioSwitch _radio;
    private readonly IAirPodsConnectionController _connection;
    private readonly RepairOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _running;

    public RepairSequence(
        IBluetoothRadioSwitch radio,
        IAirPodsConnectionController connection,
        RepairOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? new RepairOptions();
        _delay = delay ?? Task.Delay;
    }

    /// <summary>True while a run is in progress; use to disable the button.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Raised when <see cref="IsRunning"/> changes. May fire on a worker thread.</summary>
    public event EventHandler? RunningChanged;

    /// <summary>Raised at the start of each step. May fire on a worker thread.</summary>
    public event EventHandler<RepairProgress>? ProgressChanged;

    public async Task<RepairResult> RunAsync(
        string deviceId,
        string expectedContainerId,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new(RepairOutcome.AlreadyRunning, "Already fixing the connection. Give it a moment.");
        RunningChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            return await RunCoreAsync(deviceId, expectedContainerId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<RepairResult> RunCoreAsync(string deviceId, string containerId, CancellationToken ct)
    {
        bool attemptedOff = false;
        RepairResult result;
        try
        {
            result = await StepsAsync(deviceId, containerId, () => attemptedOff = true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new(RepairOutcome.Cancelled, "Fix canceled.");
        }
        catch (Exception)
        {
            result = new(RepairOutcome.Failed, "Couldn't restart Bluetooth. Try again.");
        }

        if (attemptedOff && result.Outcome is not (RepairOutcome.Success or RepairOutcome.ReconnectFailed
            or RepairOutcome.DeviceNotReady))
        {
            // The radio may still be off; make sure it is back on. Not cancellable, but bounded.
            if (!await EnsureOnAsync().ConfigureAwait(false))
                result = new(RepairOutcome.RadioStuckOff,
                    "Clasp couldn't turn Bluetooth back on. Turn it on in Windows Settings > Bluetooth & devices.");
            else if (result.Outcome == RepairOutcome.Cancelled)
                result = new(RepairOutcome.Cancelled, "Fix canceled. Bluetooth is back on.");
        }
        Report(RepairStep.Finished, result.Message);
        return result;
    }

    private async Task<RepairResult> StepsAsync(string deviceId, string containerId, Action markOff, CancellationToken ct)
    {
        var state = await _radio.GetStateAsync(ct).ConfigureAwait(false);
        var early = Classify(state.Outcome);
        if (early is not null) return early;
        if (state.State == BluetoothRadioState.Off)
            return new(RepairOutcome.RadioWasOff,
                "Bluetooth is off. Turn it on, then try Fix connection again.");

        Report(RepairStep.TurningOff, "Turning Bluetooth off…");
        markOff();
        var off = await TimedAsync(_options.TurnOffTimeout, t => _radio.SetStateAsync(false, t), ct).ConfigureAwait(false);
        if (off is null)
            return new(RepairOutcome.TurnOffTimedOut, "Windows is taking too long to turn Bluetooth off.");
        early = Classify(off.Outcome);
        if (early is not null) return early;

        Report(RepairStep.Settling, "Just a moment…");
        await _delay(_options.SettleDelay, ct).ConfigureAwait(false);

        Report(RepairStep.TurningOn, "Turning Bluetooth back on…");
        var on = await TimedAsync(_options.TurnOnTimeout, t => _radio.SetStateAsync(true, t), ct).ConfigureAwait(false);
        if (on is null)
            return new(RepairOutcome.TurnOnTimedOut, "Windows is taking too long to turn Bluetooth back on.");
        early = Classify(on.Outcome);
        if (early is not null) return early;

        Report(RepairStep.WaitingForDevice, "Looking for your AirPods…");
        if (!await WaitForAsync(deviceId, containerId, requireConnected: false, _options.DeviceReadyTimeout, ct).ConfigureAwait(false))
            return new(RepairOutcome.DeviceNotReady,
                "Bluetooth restarted, but your AirPods didn't show up. Take them out of the case and try Connect.");

        Report(RepairStep.Reconnecting, "Reconnecting your AirPods…");
        var attempt = await TimedAsync(_options.ReconnectTimeout,
            t => _connection.ReconnectAsync(deviceId, containerId, t), ct).ConfigureAwait(false);
        if (attempt is not null && attempt.Kind == ConnectionAttemptKind.Attempted
            && await WaitForAsync(deviceId, containerId, requireConnected: true, _options.ReconnectVerifyTimeout, ct).ConfigureAwait(false))
            return new(RepairOutcome.Success, "Bluetooth restarted and your AirPods reconnected.");
        return new(RepairOutcome.ReconnectFailed,
            "Bluetooth restarted, but the AirPods didn't reconnect. Take them out of the case and try Connect.");
    }

    private static RepairResult? Classify(RadioSwitchOutcome outcome) => outcome switch
    {
        RadioSwitchOutcome.Success => null,
        RadioSwitchOutcome.AccessDenied => new(RepairOutcome.AccessDenied, "Windows didn't allow Clasp to turn Bluetooth off."),
        RadioSwitchOutcome.NoRadio => new(RepairOutcome.NoRadio, "Clasp couldn't find a Bluetooth radio on this PC."),
        RadioSwitchOutcome.Busy => new(RepairOutcome.RadioBusy, "Bluetooth is busy right now. Try again in a moment."),
        _ => new(RepairOutcome.Failed, "Couldn't change Bluetooth. Try again."),
    };

    /// <summary>Runs <paramref name="work"/> with a deadline; null means the deadline passed. Caller cancel still throws.</summary>
    private static async Task<T?> TimedAsync<T>(TimeSpan timeout, Func<CancellationToken, Task<T>> work, CancellationToken ct)
        where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await work(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<bool> WaitForAsync(string deviceId, string containerId, bool requireConnected, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var snapshot = await _connection.ReadSnapshotAsync(cts.Token).ConfigureAwait(false);
                if (snapshot.IsComplete && snapshot.Devices.Any(d =>
                        string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ConnectionIdentity.NormalizeContainerId(d.ContainerId),
                            ConnectionIdentity.NormalizeContainerId(containerId), StringComparison.OrdinalIgnoreCase)
                        && (!requireConnected || d.IsConnected)))
                    return true;
                await _delay(_options.PollInterval, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> EnsureOnAsync()
    {
        using var cts = new CancellationTokenSource(_options.TurnOnTimeout);
        try
        {
            var state = await _radio.GetStateAsync(cts.Token).ConfigureAwait(false);
            if (state.Outcome == RadioSwitchOutcome.Success && state.State == BluetoothRadioState.On) return true;
            var on = await _radio.SetStateAsync(true, cts.Token).ConfigureAwait(false);
            return on.Outcome == RadioSwitchOutcome.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Report(RepairStep step, string message) => ProgressChanged?.Invoke(this, new(step, message));
}
